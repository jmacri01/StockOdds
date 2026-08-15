using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// DOES THE 5m GATE CHANGE THE BEST STRIKE PAIR, OR ONLY WHICH DAYS TO TRADE?
	//
	// A day filter and a strike input are different things, and this project already has one clean case
	// of a signal that is valuable as the former and harmful as the latter (the daily exposure target:
	// IR .169 -> .414 across target buckets as a filter, paired t -4.79 as a strike input). So the
	// question is NOT "which pair scores best under the gate" -- with ~20 pairs on 53 sessions
	// something always scores best. It is whether the RANKING of pairs shifts between gated and
	// ungated. If it does not, strike choice and the gate are independent and shipped stands.
	//
	// TWO PROTECTIONS AGAINST THE OBVIOUS TRAP:
	//   1. Every pair is priced on the SAME sessions, so pair-vs-pair is PAIRED. Differences are far
	//      better estimated than levels, and the paired t against the shipped pair is the statistic to
	//      read -- not the standalone IR of whichever cell topped a 20-cell grid.
	//   2. All pairs are risk-matched BY CONSTRUCTION: risk is a fixed fraction of bankroll lost on a
	//      full loss, so a wider or nearer spread is not quietly staking more.
	// ================================================================================================
	internal static class FiveMinStrikeGrid
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static double Gate = 0.10;
		public static int MinN = 15;
		public static double ShipShort = 0.35, ShipLong = 0.15;
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };
		public static double[] Shorts = { 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.60 };
		public static double[] Longs = { 0.05, 0.10, 0.15, 0.20, 0.25 };

		// Per session: the underlying facts needed to re-price ANY strike pair, so the grid never refits
		// anything per cell beyond the strikes themselves.
		private sealed record Sess(string Sym, DateTime D, double S, double ST, double Iv, double Exp5);

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

		// Return per unit of risk staked for one strike pair on one session (full loss = -1.0).
		private static double? Ret(Sess x, double shortD, double longD)
		{
			double T = 1.0 / 252.0;
			double kS = StrikeForPutDelta(x.S, x.Iv, T, shortD);
			double kL = StrikeForPutDelta(x.S, x.Iv, T, longD);
			double cr = Put(x.S, kS, x.Iv, T) - Put(x.S, kL, x.Iv, T);
			double ml = (kS - kL) - cr;
			if (cr <= 1e-9 || ml <= 1e-9) return null;
			double po = -Math.Max(0, kS - x.ST) + Math.Max(0, kL - x.ST);
			return (cr + po) / ml;
		}

		public static async Task Run()
		{
			var sess = new List<Sess>();
			foreach (var symbol in Symbols)
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
				List<OhlcBar> intra;
				try { intra = await IntradayClient.GetAsync(symbol, "5m", "60d"); }
				catch { continue; }
				if (intra.Count < 100) continue;

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
				var iEng = BankrollSimulator.Run(intra, 10_000.0);
				var e5 = new Dictionary<DateTime, double>();
				for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
					e5[iEng.ReturnDates[k].Date] = iEng.Positions[k];

				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					if (!e5.TryGetValue(dSig, out double x5)) continue;
					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					sess.Add(new Sess(symbol, dTr, S, ST, h * VolRiskPremium, x5));
				}
			}
			if (sess.Count == 0) { Console.WriteLine("no data"); return; }

			var gated = sess.Where(x => x.Exp5 < Gate).ToList();
			Console.WriteLine($"\n===== PUT-SPREAD STRIKE GRID UNDER THE 5m GATE =====");
			Console.WriteLine($"{sess.Count} shipped-qualifying sessions, {gated.Count} of them 5m-gated " +
				$"({sess.Min(x => x.D):yyyy-MM-dd} -> {sess.Max(x => x.D):yyyy-MM-dd})");
			Console.WriteLine($"shipped pair = short {ShipShort:0.00} / long {ShipLong:0.00}; " +
				$"every pair priced on the SAME sessions and risk-matched by construction");
			if (gated.Count < MinN) { Console.WriteLine("too few gated sessions"); return; }

			(double mean, double ir, double win, int n) Stat(List<Sess> src, double sd_, double ld)
			{
				var r = src.Select(x => Ret(x, sd_, ld)).Where(v => v.HasValue).Select(v => Risk * v!.Value).ToList();
				if (r.Count < 2) return (0, 0, 0, r.Count);
				double m = r.Average();
				double s = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				return (m, s > 1e-12 ? m / s : 0, 100.0 * r.Count(z => z > 0) / r.Count, r.Count);
			}
			// Paired difference against the shipped pair on the SAME sessions.
			(double d, double t) Paired(List<Sess> src, double sd_, double ld)
			{
				var d = new List<double>();
				foreach (var x in src)
				{
					var a = Ret(x, sd_, ld); var b = Ret(x, ShipShort, ShipLong);
					if (a.HasValue && b.HasValue) d.Add(Risk * (a.Value - b.Value));
				}
				if (d.Count < 3) return (0, 0);
				double m = d.Average();
				double s = Math.Sqrt(d.Sum(z => (z - m) * (z - m)) / (d.Count - 1));
				return (m, s > 1e-12 ? m / (s / Math.Sqrt(d.Count)) : 0);
			}

			Console.WriteLine($"\n{"short",6} {"long",6} {"net",6} | {"GATED mean%",12} {"IR",8} {"win%",7} " +
				$"{"vs ship pp",11} {"t",7} | {"UNGATED IR",11} {"rank g",7} {"rank u",7}");
			var rows = new List<(double s, double l, double irG, double irU, double mG, double win, double dp, double t)>();
			foreach (var s_ in Shorts)
				foreach (var l in Longs)
				{
					if (l >= s_ - 1e-9) continue;
					var g = Stat(gated, s_, l);
					var u = Stat(sess, s_, l);
					if (g.n < MinN) continue;
					var p = Paired(gated, s_, l);
					rows.Add((s_, l, g.ir, u.ir, g.mean, g.win, p.d, p.t));
				}
			var byG = rows.OrderByDescending(r => r.irG).Select(r => (r.s, r.l)).ToList();
			var byU = rows.OrderByDescending(r => r.irU).Select(r => (r.s, r.l)).ToList();
			foreach (var r in rows.OrderByDescending(r => r.irG))
			{
				string mark = Math.Abs(r.s - ShipShort) < 1e-9 && Math.Abs(r.l - ShipLong) < 1e-9 ? " <== SHIPPED" : "";
				Console.WriteLine($"{r.s,6:0.00} {r.l,6:0.00} {r.s - r.l,6:0.00} | {100 * r.mG,12:+0.0000;-0.0000} {r.irG,8:0.000} " +
					$"{r.win,7:0.0} {100 * r.dp,11:+0.0000;-0.0000} {r.t,7:+0.00;-0.00} | {r.irU,11:0.000} " +
					$"{byG.IndexOf((r.s, r.l)) + 1,7} {byU.IndexOf((r.s, r.l)) + 1,7}{mark}");
			}

			// THE ACTUAL QUESTION: does the gate reorder the pairs, or just lift them all?
			double[] ra = rows.Select(r => (double)byG.IndexOf((r.s, r.l))).ToArray();
			double[] rb = rows.Select(r => (double)byU.IndexOf((r.s, r.l))).ToArray();
			double ma = ra.Average(), mb = rb.Average();
			double num = 0, da = 0, db = 0;
			for (int i = 0; i < ra.Length; i++) { num += (ra[i] - ma) * (rb[i] - mb); da += (ra[i] - ma) * (ra[i] - ma); db += (rb[i] - mb) * (rb[i] - mb); }
			double rho = (da > 0 && db > 0) ? num / Math.Sqrt(da * db) : 0;
			Console.WriteLine($"\nrank correlation between GATED and UNGATED orderings: {rho:+0.000;-0.000} over {rows.Count} pairs");
			Console.WriteLine("  near +1 => the gate is a DAY filter only and leaves strike choice alone (keep shipped)");
			Console.WriteLine("  low/negative => the gate genuinely changes which pair is best");
			var best = rows.OrderByDescending(r => r.irG).First();
			Console.WriteLine($"\nbest gated pair {best.s:0.00}/{best.l:0.00} beats shipped by {100 * best.dp:+0.0000;-0.0000}pp " +
				$"at paired t {best.t:+0.00;-0.00} -- selected from {rows.Count} cells, so read the t, not the rank");
		}
	}
}
