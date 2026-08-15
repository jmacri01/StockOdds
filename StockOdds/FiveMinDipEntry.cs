using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// DON'T SKIP THE SESSION -- WAIT FOR THE DIP.
	//
	// The prior-close gate (5m exposure < 0.10 at yesterday's last bar) skips roughly half of all
	// sessions, and those skipped sessions still WIN about 85% of the time. The complaint is fair: a
	// rally on a skipped session is pure opportunity cost.
	//
	// The fix this tests: instead of gating on yesterday's closing reading, ENTER INTRADAY at the first
	// 5m bar whose exposure drops under the threshold. Since within-session exposure touches ~0 in every
	// single session (median min 0.000, max 0.098 across 107 sessions), such a bar essentially always
	// exists -- so this converts a filter that rejects half the days into an entry-TIMING rule that
	// trades nearly all of them.
	//
	// WHAT IT COSTS, and why it is not free: entering later means a SHORTER tenor. Less credit, a
	// narrower spread, less time for theta to do the work the trade depends on. And the late-session
	// artifact is real -- with IV frozen the modelled spread collapses toward intrinsic as T -> 0, so a
	// tiny max-loss denominator gets levered to the full risk budget in a spread that would be
	// unquotable. MinBarsLeft guards that, and the guard is swept rather than assumed.
	// ================================================================================================
	internal static class FiveMinDipEntry
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static double Gate = 0.10;
		public static int MinBarsLeft = 12;          // of ~78 5m bars; ~1 hour must remain to enter
		public static int MinN = 12;
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };

		// PriorExp = yesterday's closing 5m exposure (what the shipped gate reads).
		// DipBar   = index of the first bar today whose exposure < Gate, or -1.
		private sealed record Sess(string Sym, DateTime D, double PriorExp, int DipBar, int NBars,
			double ROpen, double RDip);

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

		// Open a spread at spot S with fraction `frac` of the session remaining; settle at ST.
		private static double? Trade(double S, double ST, double iv, double frac)
		{
			double T = Math.Max(1e-9, frac) / 252.0;
			double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
			double kL = StrikeForPutDelta(S, iv, T, WingDelta);
			double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
			double ml = (kS - kL) - cr;
			if (cr <= 1e-9 || ml <= 1e-9) return null;
			double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
			return (cr + po) / ml;
		}

		private static string Wk(DateTime d) => $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";

		public static async Task Run()
		{
			var all = new List<Sess>();
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
				var expPath = new Dictionary<DateTime, List<double>>();
				var barsOf = new Dictionary<DateTime, List<OhlcBar>>();
				for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
				{
					var d = iEng.ReturnDates[k].Date;
					if (!expPath.TryGetValue(d, out var lst)) expPath[d] = lst = new List<double>();
					lst.Add(iEng.Positions[k]);
				}
				foreach (var g in intra.GroupBy(b => b.Date.Date)) barsOf[g.Key] = g.OrderBy(b => b.Date).ToList();

				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					if (!expPath.TryGetValue(dSig, out var pPrev) || pPrev.Count == 0) continue;
					if (!expPath.TryGetValue(dTr, out var pToday) || !barsOf.TryGetValue(dTr, out var tb)) continue;
					int n = Math.Min(pToday.Count, tb.Count);
					if (n < MinBarsLeft + 2) continue;

					double iv = h * VolRiskPremium;
					double S0 = tb[0].Open, ST = tb[n - 1].Close;
					if (S0 <= 0 || ST <= 0) continue;
					var rOpen = Trade(S0, ST, iv, 1.0);
					if (rOpen == null) continue;

					// first bar today whose exposure is under the gate AND leaves enough session to trade
					int dip = -1;
					for (int j = 0; j < n - MinBarsLeft; j++)
						if (pToday[j] < Gate) { dip = j; break; }
					double rDip = double.NaN;
					if (dip >= 0)
					{
						double Sd = tb[dip].Close;
						var r = Sd > 0 ? Trade(Sd, ST, iv, (double)(n - 1 - dip) / n) : null;
						if (r != null) rDip = r.Value;
					}
					all.Add(new Sess(symbol, dTr, pPrev[^1], dip, n, rOpen.Value, rDip));
				}
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			var worst = all.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.ROpen)).First().Key;
			Console.WriteLine($"\n===== WAIT FOR THE DIP INSTEAD OF SKIPPING THE SESSION =====");
			Console.WriteLine($"{all.Count} sessions, {all.Select(x => Wk(x.D)).Distinct().Count()} weeks; " +
				$"gate {Gate:0.00}, entry needs >= {MinBarsLeft} of ~{all.Select(x => x.NBars).DefaultIfEmpty(0).Max()} bars left");
			int skipped = all.Count(x => x.PriorExp >= Gate);
			var skipRally = all.Where(x => x.PriorExp >= Gate).ToList();
			Console.WriteLine($"the prior-close gate SKIPS {skipped} of {all.Count} ({100.0 * skipped / all.Count:0.0}%); " +
				$"those sessions win {100.0 * skipRally.Count(x => x.ROpen > 0) / Math.Max(1, skipRally.Count):0.0}% " +
				$"and average {100 * Risk * (skipRally.Count > 0 ? skipRally.Average(x => x.ROpen) : 0):+0.0000;-0.0000}% if traded at the open");
			Console.WriteLine($"an intraday dip exists on {100.0 * all.Count(x => x.DipBar >= 0) / all.Count:0.0}% of sessions " +
				$"(median dip bar {all.Where(x => x.DipBar >= 0).Select(x => x.DipBar).OrderBy(v => v).ElementAt(Math.Max(0, all.Count(x => x.DipBar >= 0) / 2))} of ~{all.Select(x => x.NBars).DefaultIfEmpty(0).Max()})");

			void Row(string lbl, IEnumerable<(Sess s, double r)> src, List<Sess> universe)
			{
				var t = src.Where(x => !double.IsNaN(x.r)).ToList();
				if (t.Count < MinN) { Console.WriteLine($"{lbl,-44} {t.Count,4}   too few"); return; }
				var r = t.Select(x => Risk * x.r).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				// per-SESSION mean over the whole universe: a rule that trades less must not be flattered
				// by its own selectivity, so untraded sessions count as zero here.
				double perSess = r.Sum() / universe.Count;
				Console.WriteLine($"{lbl,-44} {t.Count,4} {100.0 * t.Count / universe.Count,6:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 1e-12 ? m / sd : 0),8:0.000} {100 * perSess,11:+0.0000;-0.0000}");
			}

			foreach (var (tag, univ) in new[] { ("FULL SAMPLE", all), ("W23 REMOVED", all.Where(x => Wk(x.D) != worst).ToList()) })
			{
				Console.WriteLine($"\n-- {tag} --");
				Console.WriteLine($"{"rule",-44} {"n",4} {"%sess",6} {"mean/tr%",10} {"win%",7} {"IR",8} {"per-session%",11}");
				Row("A. trade EVERY session at the open", univ.Select(x => (x, x.ROpen)), univ);
				Row("B. prior-close gate, enter at open [current]", univ.Where(x => x.PriorExp < Gate).Select(x => (x, x.ROpen)), univ);
				Row("C. dip entry, every session", univ.Select(x => (x, x.RDip)), univ);
				Row("D. hybrid: open if gated, else dip", univ.Select(x => (x, x.PriorExp < Gate ? x.ROpen : x.RDip)), univ);
				Row("   the SKIPPED sessions, entered at open", univ.Where(x => x.PriorExp >= Gate).Select(x => (x, x.ROpen)), univ);
				Row("   the SKIPPED sessions, entered at dip", univ.Where(x => x.PriorExp >= Gate).Select(x => (x, x.RDip)), univ);
			}
			Console.WriteLine("\nper-session% counts untraded sessions as zero, so it is the column that decides whether");
			Console.WriteLine("recovering the skipped days is worth the shorter tenor. mean/tr% alone rewards selectivity.");

			// ---- ARTIFACT CONTROL: does the hybrid's edge come from LATE entries? -----------------------
			// With IV frozen the modelled spread converges to intrinsic as T -> 0, so a late entry books a
			// tiny max-loss denominator that the risk budget then levers enormously -- in a spread whose
			// real bid-ask would swallow the credit. If the hybrid's advantage grows as later entries are
			// permitted, that is the artifact rather than an edge. A genuine effect should be flat or
			// STRONGER when only early entries are allowed.
			Console.WriteLine($"\n-- artifact control: sweep the minimum bars that must remain to enter --");
			Console.WriteLine($"{"minBarsLeft",12} {"~mins left",11} {"n dip",6} {"hybrid IR",10} {"hybrid /sess%",14} {"A: open-only /sess%",20}");
			var exW = all.Where(x => Wk(x.D) != worst).ToList();
			foreach (int mb in new[] { 4, 12, 24, 39, 60 })
			{
				var rows = new List<double>();
				foreach (var x in exW)
				{
					// recompute the dip under THIS guard, from the stored path is not possible, so re-derive
					// using the recorded dip bar: a dip only counts if enough of the session remains.
					bool dipOk = x.DipBar >= 0 && x.NBars - 1 - x.DipBar >= mb;
					double r = x.PriorExp < Gate ? x.ROpen : (dipOk && !double.IsNaN(x.RDip) ? x.RDip : double.NaN);
					if (!double.IsNaN(r)) rows.Add(Risk * r);
				}
				if (rows.Count < MinN) { Console.WriteLine($"{mb,12}   too few"); continue; }
				double m = rows.Average();
				double sd = Math.Sqrt(rows.Sum(z => (z - m) * (z - m)) / (rows.Count - 1));
				double perSess = rows.Sum() / exW.Count;
				double aPer = exW.Sum(x => Risk * x.ROpen) / exW.Count;
				Console.WriteLine($"{mb,12} {5 * mb,11} {rows.Count,6} {(sd > 1e-12 ? m / sd : 0),10:0.000} " +
					$"{100 * perSess,14:+0.0000;-0.0000} {100 * aPer,20:+0.0000;-0.0000}");
			}
			Console.WriteLine("   (the dip bar itself is fixed; only the guard on how much session must remain moves)");

			// ---- HYBRID vs TRADE-EVERYTHING, paired on the same sessions -------------------------------
			// D and A trade the SAME days and differ only in entry timing, so the comparison is paired and
			// far better powered than comparing two separately-selected samples. The block bootstrap
			// resamples whole weeks, which is what caught the exposure gate being a single episode.
			var pairD = exW.Where(x => !double.IsNaN(x.PriorExp < Gate ? x.ROpen : x.RDip)).ToList();
			var dif = pairD.Select(x => Risk * ((x.PriorExp < Gate ? x.ROpen : x.RDip) - x.ROpen)).ToList();
			double md = dif.Average();
			double sdd = Math.Sqrt(dif.Sum(z => (z - md) * (z - md)) / (dif.Count - 1));
			var wks = pairD.GroupBy(x => Wk(x.D)).ToList();
			var rnd = new Random(20260815);
			int nb = 0, le = 0;
			for (int it = 0; it < 5000; it++)
			{
				var samp = new List<Sess>(pairD.Count);
				for (int w = 0; w < wks.Count; w++) samp.AddRange(wks[rnd.Next(wks.Count)]);
				if (samp.Count < 10) continue;
				nb++;
				if (samp.Average(x => Risk * ((x.PriorExp < Gate ? x.ROpen : x.RDip) - x.ROpen)) <= 0) le++;
			}
			// WHEN does the dip actually happen on the sessions that matter? D and A differ only on the
			// sessions the gate would skip. If the dip on those is bar 0, then "dip entry" is really just
			// "enter at the close of the first 5m bar instead of the open" -- a five-minute shift, which
			// would make the whole effect a single-bar timing quirk rather than a regime read.
			var diffOnly = exW.Where(x => x.PriorExp >= Gate && x.DipBar >= 0).ToList();
			if (diffOnly.Count > 0)
			{
				var bars = diffOnly.Select(x => x.DipBar).OrderBy(v => v).ToList();
				Console.WriteLine($"\n-- when the dip fires on the {diffOnly.Count} sessions where D differs from A --");
				Console.WriteLine($"   dip bar: min {bars[0]}, p25 {bars[bars.Count / 4]}, median {bars[bars.Count / 2]}, " +
					$"p75 {bars[3 * bars.Count / 4]}, max {bars[^1]}  (of ~79 bars; bar 0 = first 5 minutes)");
				Console.WriteLine($"   fires at bar 0: {100.0 * bars.Count(b => b == 0) / bars.Count:0.0}%   " +
					$"within the first half hour (bar <= 5): {100.0 * bars.Count(b => b <= 5) / bars.Count:0.0}%   " +
					$"after midday (bar >= 39): {100.0 * bars.Count(b => b >= 39) / bars.Count:0.0}%");
				// Split the paired gain by WHEN the dip fired -- if it all sits in bar 0 it is a one-bar quirk.
				foreach (var (lbl, sel) in new[] { ("bar 0 only", (Func<Sess, bool>)(x => x.DipBar == 0)),
				                                    ("bars 1-5", x => x.DipBar >= 1 && x.DipBar <= 5),
				                                    ("bars 6+", x => x.DipBar >= 6) })
				{
					var g = diffOnly.Where(sel).Where(x => !double.IsNaN(x.RDip)).ToList();
					if (g.Count < 5) { Console.WriteLine($"   {lbl,-12} n={g.Count,3}   too few"); continue; }
					var d3 = g.Select(x => Risk * (x.RDip - x.ROpen)).ToList();
					double m3 = d3.Average();
					double s3 = d3.Count > 1 ? Math.Sqrt(d3.Sum(z => (z - m3) * (z - m3)) / (d3.Count - 1)) : 0;
					Console.WriteLine($"   {lbl,-12} n={g.Count,3}  dip minus open {100 * m3,9:+0.0000;-0.0000}pp  " +
						$"t {(s3 > 1e-12 ? m3 / (s3 / Math.Sqrt(d3.Count)) : 0),6:+0.00;-0.00}");
				}
			}

			Console.WriteLine($"\n-- hybrid (D) minus trade-everything (A), paired on {pairD.Count} sessions, W23 removed --");
			Console.WriteLine($"   mean difference {100 * md:+0.0000;-0.0000}pp/session   paired t {md / (sdd / Math.Sqrt(dif.Count)):+0.00;-0.00}   " +
				$"block-bootstrap P(diff<=0) = {(nb > 0 ? (double)le / nb : 1):0.000}");
			Console.WriteLine($"   the two differ ONLY on the {pairD.Count(x => x.PriorExp >= Gate)} sessions the gate would skip; " +
				$"D enters those at the dip, A at the open");
			// Same paired test restricted to those sessions, where the whole difference lives.
			var only = pairD.Where(x => x.PriorExp >= Gate).ToList();
			if (only.Count >= MinN)
			{
				var d2 = only.Select(x => Risk * (x.RDip - x.ROpen)).Where(v => !double.IsNaN(v)).ToList();
				double m2 = d2.Average();
				double s2 = Math.Sqrt(d2.Sum(z => (z - m2) * (z - m2)) / (d2.Count - 1));
				Console.WriteLine($"   on those {d2.Count} alone: dip minus open {100 * m2:+0.0000;-0.0000}pp, " +
					$"paired t {m2 / (s2 / Math.Sqrt(d2.Count)):+0.00;-0.00}");
			}
		}
	}
}
