using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// GATE ON ST BULL INSTEAD OF EXPOSURE.
	//
	// The shipped rule already skips ST Bear. This asks the stricter question: trade ONLY when the
	// candle state is Bull, dropping BullNeutral and BearNeutral too.
	//
	// TWO VERSIONS, VERY DIFFERENT EVIDENTIAL WEIGHT:
	//   DAILY ST  - runs on the full daily-expiry history across four instruments (thousands of
	//               sessions). This is the one that can actually settle the question.
	//   5m ST     - limited to the ~60-day 5m window (110 sessions), i.e. the same short sample that
	//               produced the retracted exposure gate.
	//
	// ROBUSTNESS IS BUILT IN FROM THE START, not bolted on after the fact. The 5m exposure gate passed
	// a 20k permutation test at p = 0.033 and was still entirely one week (2026-W23) -- permutation
	// asks whether the pairing is random, which it genuinely was not, rather than whether the result
	// survives removing one episode. So every arm here reports:
	//   - the full-sample number
	//   - the same number with the single worst week removed
	//   - a BLOCK BOOTSTRAP resampling whole WEEKS, which respects the fact that sessions inside a week
	//     (and across correlated index products on the same days) are not independent observations
	// ================================================================================================
	internal static class StBullGate
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static DateTime From = new DateTime(2022, 11, 1);
		public static int MinN = 15;
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };

		private sealed record Tr(string Sym, DateTime D, double R, ShortTermState DailySt, ShortTermState? St5);

		private static double NormCdf(double x)
		{
			double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
			double p = 1.0 - 0.3989422804014327 * Math.Exp(-x * x / 2.0) *
				(0.319381530 * t - 0.356563782 * t * t + 1.781477937 * t * t * t
				 - 1.821255978 * t * t * t * t + 1.330274429 * t * t * t * t * t);
			return x >= 0 ? p : 1.0 - p;
		}

		private static double Put(double s, double k, double v, double t)
		{
			if (t <= 0 || v <= 0) return Math.Max(0, k - s);
			double d1 = (Math.Log(s / k) + 0.5 * v * v * t) / (v * Math.Sqrt(t));
			return k * NormCdf(-(d1 - v * Math.Sqrt(t))) - s * NormCdf(-d1);
		}

		private static double StrikeForPutDelta(double s, double v, double t, double delta)
		{
			double lo = s * 0.5, hi = s * 1.5;
			for (int i = 0; i < 80; i++)
			{
				double mid = 0.5 * (lo + hi);
				double d1 = (Math.Log(s / mid) + 0.5 * v * v * t) / (v * Math.Sqrt(t));
				if (NormCdf(-d1) < delta) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
		}

		private static string Wk(DateTime d) => $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";

		public static async Task Run()
		{
			var all = new List<Tr>();
			foreach (var symbol in Symbols)
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
				var pos = new Dictionary<DateTime, double>();
				for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
					pos[eng.ReturnDates[k].Date] = eng.Positions[k];
				var stm = new Dictionary<DateTime, ShortTermState>();
				for (int k = 0; k < eng.StState.Count && k < eng.ReturnDates.Count; k++)
					stm[eng.ReturnDates[k].Date] = eng.StState[k];
				var hv = new Dictionary<DateTime, double>();
				for (int i = 1; i < daily.Count; i++)
				{
					int j0 = Math.Max(1, i - (HvWindow - 1));
					var lr = new List<double>();
					for (int j = j0; j <= i; j++)
						if (daily[j - 1].Close > 0 && daily[j].Close > 0) lr.Add(Math.Log(daily[j].Close / daily[j - 1].Close));
					if (lr.Count >= 10)
					{
						double m = lr.Average();
						hv[daily[i].Date.Date] = Math.Max(0.05, Math.Sqrt(lr.Sum(x => (x - m) * (x - m)) / (lr.Count - 1)) * Math.Sqrt(252.0));
					}
				}
				var st5 = new Dictionary<DateTime, ShortTermState>();
				try
				{
					var intra = await IntradayClient.GetAsync(symbol, "5m", "60d");
					if (intra.Count >= 100)
					{
						var iEng = BankrollSimulator.Run(intra, 10_000.0);
						for (int k = 0; k < iEng.StState.Count && k < iEng.ReturnDates.Count; k++)
							st5[iEng.ReturnDates[k].Date] = iEng.StState[k];
					}
				}
				catch { }

				double T = 1.0 / 252.0;
				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (dTr < From) continue;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (!stm.TryGetValue(dSig, out var dst)) continue;
					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					all.Add(new Tr(symbol, dTr, (cr + po) / ml, dst,
						st5.TryGetValue(dSig, out var s5) ? s5 : null));
				}
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			void Row(string lbl, List<Tr> t, int denom)
			{
				if (t.Count < MinN) { Console.WriteLine($"{lbl,-38} {t.Count,5}   (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var z in r) { e *= 1 + z; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				Console.WriteLine($"{lbl,-38} {t.Count,5} {100.0 * t.Count / denom,6:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 1e-12 ? m / sd : 0),8:0.000} " +
					$"{(sd > 0 ? m / (sd / Math.Sqrt(r.Count)) : 0),7:+0.00;-0.00} {dd,8:0.00}");
			}

			// Week-block bootstrap of (gated mean - ungated mean). Weeks are the resampling unit because
			// sessions within a week -- and across correlated index products on the same days -- move
			// together; treating each session as independent is what let one week masquerade as a signal.
			void Block(string lbl, List<Tr> universe, Func<Tr, bool> gate)
			{
				var weeks = universe.GroupBy(x => Wk(x.D)).ToList();
				if (weeks.Count < 8) { Console.WriteLine($"  {lbl,-36} too few weeks"); return; }
				double Obs(List<Tr> s)
				{
					var g = s.Where(gate).Select(x => x.R).ToList();
					return g.Count > 0 ? Risk * (g.Average() - s.Average(x => x.R)) : 0;
				}
				double obs = Obs(universe);
				var rnd = new Random(20260814);
				int n = 0, le = 0;
				for (int it = 0; it < 4000; it++)
				{
					var samp = new List<Tr>(universe.Count);
					for (int w = 0; w < weeks.Count; w++) samp.AddRange(weeks[rnd.Next(weeks.Count)]);
					if (samp.Count(gate) < 5) continue;
					n++;
					if (Obs(samp) <= 0) le++;
				}
				// Drop-the-worst-week: the check that would have caught the 5m exposure gate.
				var worst = weeks.OrderBy(g => g.Average(x => x.R)).First().Key;
				var exW = universe.Where(x => Wk(x.D) != worst).ToList();
				Console.WriteLine($"  {lbl,-36} edge {100 * obs,8:+0.0000;-0.0000}pp   " +
					$"block-bootstrap P(edge<=0) = {(n > 0 ? (double)le / n : 1),5:0.000}   " +
					$"minus worst week ({worst}): {100 * Obs(exW),8:+0.0000;-0.0000}pp");
			}

			// ---------------- DAILY ST STATE: the high-powered test ----------------
			Console.WriteLine($"\n===== GATE ON ST BULL INSTEAD OF EXPOSURE =====");
			Console.WriteLine($"DAILY ST state, {all.Count} sessions {all.Min(x => x.D):yyyy-MM-dd} -> {all.Max(x => x.D):yyyy-MM-dd}, " +
				$"{Symbols.Length} instruments, {all.Select(x => Wk(x.D)).Distinct().Count()} weeks");
			Console.WriteLine($"\n{"arm",-38} {"n",5} {"%all",6} {"mean%",10} {"win%",7} {"IR",8} {"t",7} {"maxDD%",8}");
			Row("ALL states (no ST filter)", all, all.Count);
			Row("SHIPPED: skip Bear only", all.Where(x => x.DailySt != ShortTermState.Bear).ToList(), all.Count);
			Row("ST Bull ONLY", all.Where(x => x.DailySt == ShortTermState.Bull).ToList(), all.Count);
			Row("Bull + BullNeutral", all.Where(x => x.DailySt is ShortTermState.Bull or ShortTermState.BullNeutral).ToList(), all.Count);
			Console.WriteLine("  -- each state alone --");
			foreach (var s in new[] { ShortTermState.Bull, ShortTermState.BullNeutral, ShortTermState.BearNeutral, ShortTermState.Bear })
				Row($"    {s}", all.Where(x => x.DailySt == s).ToList(), all.Count);

			Console.WriteLine($"\n-- robustness of the DAILY Bull-only gate, measured against the shipped (skip-Bear) universe --");
			var shipUniv = all.Where(x => x.DailySt != ShortTermState.Bear).ToList();
			Block("Bull only vs skip-Bear", shipUniv, x => x.DailySt == ShortTermState.Bull);
			Block("Bull+BullNeutral vs skip-Bear", shipUniv, x => x.DailySt is ShortTermState.Bull or ShortTermState.BullNeutral);
			Console.WriteLine($"\n-- per-instrument, Bull-only vs shipped skip-Bear (mean%) --");
			foreach (var sym in Symbols)
			{
				var u = shipUniv.Where(x => x.Sym == sym).ToList();
				var b = u.Where(x => x.DailySt == ShortTermState.Bull).ToList();
				if (u.Count < 30 || b.Count < 10) { Console.WriteLine($"   {sym,-5} too few"); continue; }
				Console.WriteLine($"   {sym,-5} shipped n={u.Count,4} {100 * Risk * u.Average(x => x.R),8:+0.0000;-0.0000}   " +
					$"Bull-only n={b.Count,4} {100 * Risk * b.Average(x => x.R),8:+0.0000;-0.0000}");
			}

			// ---------------- 5m ST STATE: the short-sample version ----------------
			var w5 = all.Where(x => x.St5 != null).ToList();
			if (w5.Count >= MinN * 2)
			{
				Console.WriteLine($"\n-- 5m ST state, {w5.Count} sessions only ({w5.Select(x => Wk(x.D)).Distinct().Count()} weeks) " +
					$"-- same short window that produced the retracted exposure gate --");
				Console.WriteLine($"\n{"arm",-38} {"n",5} {"%all",6} {"mean%",10} {"win%",7} {"IR",8} {"t",7} {"maxDD%",8}");
				Row("5m-covered, shipped filters", w5.Where(x => x.DailySt != ShortTermState.Bear).ToList(), w5.Count);
				Row("5m ST Bull ONLY", w5.Where(x => x.St5 == ShortTermState.Bull).ToList(), w5.Count);
				Row("5m ST NOT Bull", w5.Where(x => x.St5 != ShortTermState.Bull).ToList(), w5.Count);
				Console.WriteLine();
				Block("5m Bull only", w5, x => x.St5 == ShortTermState.Bull);
			}
		}
	}
}
