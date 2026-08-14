using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// DROP THE DAILY FILTERS, GATE ONLY ON 5m EXPOSURE.
	//
	// The shipped rule qualifies a session on the DAILY engine (exposure >= 0.10 and not ST Bear).
	// This asks whether the 5m exposure gate can REPLACE that qualification rather than sit on top of
	// it -- trade every candle that has a same-day expiry, and let 5m exposure < 0.10 be the only
	// filter, with and without the positive-gamma condition.
	//
	// Dropping the daily filters is the one change here that ADDS sample: over the same ~60-day 5m
	// window it admits the sessions the shipped rule rejects, so this runs on more observations than
	// the earlier 5m work rather than fewer.
	//
	// THE 2x2 IS THE POINT. "Replace" and "add to" look identical in a single-arm table and are only
	// distinguishable by crossing the two filters:
	//   - if 5m < 0.10 works only where the daily rule ALSO qualifies, it adds nothing new
	//   - if it works on daily-REJECTED sessions too, it genuinely substitutes for the daily rule
	//   - if the two cells are additive, keep both
	//
	// GAMMA COVERAGE IS SHORTER THAN THE 5m WINDOW. The UW gamma series ends before the 5m bars do, so
	// gamma-conditioned arms run on a strict subset. They are reported against a baseline restricted to
	// the SAME subset, never against the full-window baseline.
	// ================================================================================================
	internal static class PutSpreadAllCandles
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double DailyLo = 0.10;      // the SHIPPED daily floor, recorded not enforced
		public static double FiveMinGate = 0.10;
		public static int MinN = 15;
		public static (string sym, string dat)[] Symbols =
			{ ("SPY", "spx"), ("QQQ", "qqq"), ("IWM", "iwm"), ("GLD", "gld") };

		private sealed record Tr(string Sym, DateTime D, double R, double Exp5, bool DailyOk, double Cp);

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

		private static Dictionary<DateTime, double> LoadCallPut(string dataSym)
		{
			var map = new Dictionary<DateTime, double>();
			string path = Path.Combine(Path.GetFullPath(Universe.DataDir), $"gex_uw_{dataSym}.csv");
			if (!File.Exists(path)) return map;
			var lines = File.ReadAllLines(path);
			var h = lines[0].Split(',');
			int di = Array.IndexOf(h, "date"), ci = Array.IndexOf(h, "call_gex"), pi = Array.IndexOf(h, "put_gex");
			if (di < 0 || ci < 0 || pi < 0) return map;
			for (int i = 1; i < lines.Length; i++)
			{
				var p = lines[i].Split(',');
				if (p.Length <= Math.Max(ci, pi)) continue;
				if (DateTime.TryParse(p[di], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
					&& double.TryParse(p[ci], NumberStyles.Any, CultureInfo.InvariantCulture, out double c)
					&& double.TryParse(p[pi], NumberStyles.Any, CultureInfo.InvariantCulture, out double pg)
					&& Math.Abs(pg) > 1e-9)
					map[d.Date] = c / Math.Abs(pg);
			}
			return map;
		}

		public static async Task Run()
		{
			var all = new List<Tr>();
			foreach (var (symbol, dataSym) in Symbols)
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
				var cp = LoadCallPut(dataSym);

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
				var exp5 = new Dictionary<DateTime, double>();
				try
				{
					var intra = await IntradayClient.GetAsync(symbol, "5m", "60d");
					if (intra.Count >= 100)
					{
						var iEng = BankrollSimulator.Run(intra, 10_000.0);
						for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
							exp5[iEng.ReturnDates[k].Date] = iEng.Positions[k];
					}
				}
				catch (Exception ex) { Console.WriteLine($"  {symbol}: 5m unavailable ({ex.Message})"); continue; }

				double T = 1.0 / 252.0;
				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (!exp5.TryGetValue(dSig, out double e5)) continue;      // 5m coverage defines the window
					// NO daily filters applied -- only RECORDED, so the 2x2 can be built
					bool stBear = stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear;
					if (!pos.TryGetValue(dSig, out double dExp)) continue;
					bool dailyOk = dExp >= DailyLo && !stBear;
					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					all.Add(new Tr(symbol, dTr, (cr + po) / ml, e5,
						dailyOk, cp.TryGetValue(dSig, out double c) ? c : double.NaN));
				}
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			int denom = all.Count;
			void Table(string lbl, List<Tr> t)
			{
				if (t.Count < MinN) { Console.WriteLine($"{lbl,-42} {t.Count,5}   REFUSED (n < {MinN})"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double se = sd / Math.Sqrt(r.Count);
				double e = 1, pk = 1, dd = 0;
				foreach (var z in r) { e *= 1 + z; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				Console.WriteLine($"{lbl,-42} {t.Count,5} {100.0 * t.Count / denom,6:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 1e-12 ? m / sd : 0),8:0.000} " +
					$"{(se > 0 ? m / se : 0),7:+0.00;-0.00} {dd,8:0.00}");
			}

			Console.WriteLine($"\n===== SHIPPED PUT SPREAD, ALL DAILY CANDLES, GATED ONLY ON 5m =====");
			Console.WriteLine($"{all.Count} sessions {all.Min(x => x.D):yyyy-MM-dd} -> {all.Max(x => x.D):yyyy-MM-dd} " +
				$"across {all.Select(x => x.Sym).Distinct().Count()} instruments (window set by 5m coverage)");
			Console.WriteLine($"daily rule would qualify {100.0 * all.Count(x => x.DailyOk) / all.Count:0.0}%; " +
				$"5m < {FiveMinGate:0.00} on {100.0 * all.Count(x => x.Exp5 < FiveMinGate) / all.Count:0.0}%; " +
				$"gamma covers {100.0 * all.Count(x => !double.IsNaN(x.Cp)) / all.Count:0.0}%");

			Console.WriteLine($"\n{"arm",-42} {"n",5} {"%all",6} {"mean%",10} {"win%",7} {"IR",8} {"t",7} {"maxDD%",8}");
			Table("ALL candles, no gate at all", all);
			Table("SHIPPED daily filters only", all.Where(x => x.DailyOk).ToList());
			Table($"5m < {FiveMinGate:0.00} ONLY (all candles)", all.Where(x => x.Exp5 < FiveMinGate).ToList());
			Table($"5m < {FiveMinGate:0.00} AND shipped daily", all.Where(x => x.Exp5 < FiveMinGate && x.DailyOk).ToList());
			Table($"5m >= {FiveMinGate:0.00} (control)", all.Where(x => x.Exp5 >= FiveMinGate).ToList());

			// ---- REPLACE OR ADD? the 2x2 -------------------------------------------------------------
			Console.WriteLine($"\n-- 2x2: does the 5m gate work where the DAILY rule rejects? --");
			Table("  daily OK   & 5m <  gate", all.Where(x => x.DailyOk && x.Exp5 < FiveMinGate).ToList());
			Table("  daily OK   & 5m >= gate", all.Where(x => x.DailyOk && x.Exp5 >= FiveMinGate).ToList());
			Table("  daily FAIL & 5m <  gate", all.Where(x => !x.DailyOk && x.Exp5 < FiveMinGate).ToList());
			Table("  daily FAIL & 5m >= gate", all.Where(x => !x.DailyOk && x.Exp5 >= FiveMinGate).ToList());

			// ---- WITH / WITHOUT POSITIVE GAMMA, on the gamma-covered subset only ----------------------
			var g = all.Where(x => !double.IsNaN(x.Cp)).ToList();
			Console.WriteLine($"\n-- gamma arms, on the {g.Count} gamma-covered sessions ONLY " +
				$"({g.Min(x => x.D):yyyy-MM-dd} -> {g.Max(x => x.D):yyyy-MM-dd}) --");
			denom = Math.Max(1, g.Count);
			Table("  gamma subset: all candles", g);
			Table("  gamma subset: callPut >= 1.00", g.Where(x => x.Cp >= 1.0).ToList());
			Table($"  gamma subset: 5m < gate", g.Where(x => x.Exp5 < FiveMinGate).ToList());
			Table($"  5m < gate & callPut >= 1.00", g.Where(x => x.Exp5 < FiveMinGate && x.Cp >= 1.0).ToList());
			Table($"  5m < gate & callPut <  1.00", g.Where(x => x.Exp5 < FiveMinGate && x.Cp < 1.0).ToList());
			denom = all.Count;

			// ---- DOES GEX STILL MATTER ONCE THE 5m GATE IS ON? -----------------------------------------
			// Restricted to SHIPPED-qualifying sessions (the frame the 5m gate actually works in) and to
			// gamma-covered dates. Two senses of "matter" are tested separately because they are different
			// levers: the ratio as a GATE (skip below 1.00) and the ratio as a SIZE multiplier.
			var sq = all.Where(x => x.DailyOk && !double.IsNaN(x.Cp)).ToList();
			Console.WriteLine($"\n===== DOES GEX MATTER ON TOP OF THE 5m GATE? (shipped-qualifying, gamma-covered) =====");
			Console.WriteLine($"{sq.Count} sessions; callPut >= 1.00 on {100.0 * sq.Count(x => x.Cp >= 1) / Math.Max(1, sq.Count):0.0}%");
			denom = Math.Max(1, sq.Count);
			Console.WriteLine($"\n{"arm",-42} {"n",5} {"%all",6} {"mean%",10} {"win%",7} {"IR",8} {"t",7} {"maxDD%",8}");
			Table("shipped only (no 5m, no gex)", sq);
			Table("5m < gate", sq.Where(x => x.Exp5 < FiveMinGate).ToList());
			Table("callPut >= 1.00 only", sq.Where(x => x.Cp >= 1).ToList());
			Console.WriteLine("  -- the 2x2 --");
			Table("  5m < gate  & callPut >= 1.00", sq.Where(x => x.Exp5 < FiveMinGate && x.Cp >= 1).ToList());
			Table("  5m < gate  & callPut <  1.00", sq.Where(x => x.Exp5 < FiveMinGate && x.Cp < 1).ToList());
			Table("  5m >= gate & callPut >= 1.00", sq.Where(x => x.Exp5 >= FiveMinGate && x.Cp >= 1).ToList());
			Table("  5m >= gate & callPut <  1.00", sq.Where(x => x.Exp5 >= FiveMinGate && x.Cp < 1).ToList());

			// The ratio is inert-to-INVERTED off the SPX complex (see uw-ratio-spy-qqq-only), so pooling
			// IWM/GLD into a gex test imports known noise. Reported separately rather than mixed.
			var sq2 = sq.Where(x => x.Sym == "SPY" || x.Sym == "QQQ").ToList();
			if (sq2.Count >= MinN)
			{
				Console.WriteLine($"  -- SPY+QQQ only ({sq2.Count} sessions), where the ratio is known to carry signal --");
				denom = sq2.Count;
				Table("  5m < gate", sq2.Where(x => x.Exp5 < FiveMinGate).ToList());
				Table("  5m < gate & callPut >= 1.00", sq2.Where(x => x.Exp5 < FiveMinGate && x.Cp >= 1).ToList());
				Table("  5m < gate & callPut <  1.00", sq2.Where(x => x.Exp5 < FiveMinGate && x.Cp < 1).ToList());
			}
			denom = all.Count;

			// SIZING, not gating. Paired on the SAME sessions -- stake varies, outcome does not -- so this
			// isolates cov(stake, outcome) and has more power than cutting the sample in two.
			void PairSize(string lbl, List<Tr> t)
			{
				if (t.Count < MinN) { Console.WriteLine($"  {lbl,-40} n={t.Count,4}  REFUSED (n < {MinN})"); return; }
				var d = t.Select(x => Risk * (Math.Min(2.0, x.Cp) - 1.0) * x.R).ToList();
				double m = d.Average();
				double sd = Math.Sqrt(d.Sum(z => (z - m) * (z - m)) / (d.Count - 1));
				double avgRisk = t.Average(x => Risk * Math.Min(2.0, x.Cp));
				Console.WriteLine($"  {lbl,-40} n={t.Count,4}  diff {100 * m,8:+0.0000;-0.0000}pp  " +
					$"t {m / (sd / Math.Sqrt(d.Count)),6:+0.00;-0.00}  avg risk {100 * avgRisk,5:0.0}% vs {100 * Risk:0.0}%");
			}
			Console.WriteLine($"\n-- ratio as SIZING (risk = base x min(callPut,2)), paired vs flat on the same sessions --");
			PairSize("all shipped-qualifying", sq);
			PairSize("within 5m < gate", sq.Where(x => x.Exp5 < FiveMinGate).ToList());
			PairSize("within 5m >= gate", sq.Where(x => x.Exp5 >= FiveMinGate).ToList());
			PairSize("SPY+QQQ, within 5m < gate", sq2.Where(x => x.Exp5 < FiveMinGate).ToList());

			Console.WriteLine($"\n-- per-instrument, 5m < {FiveMinGate:0.00} only (all candles) --");
			foreach (var kv in all.GroupBy(x => x.Sym).OrderBy(x => x.Key))
			{
				denom = kv.Count();
				Table($"  {kv.Key}: all candles", kv.ToList());
				Table($"  {kv.Key}: 5m < gate", kv.Where(x => x.Exp5 < FiveMinGate).ToList());
			}
		}
	}
}
