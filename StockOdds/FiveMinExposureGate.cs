using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// THE 5-MINUTE EXPOSURE GATE, PUSHED AS FAR AS 60 DAYS OF DATA ALLOWS.
	//
	// On SPY alone the gate looked strong (IR 0.675 -> 1.259) but rested on 16 gated trades, and a
	// window control showed the 1h gate LOSING on those same sessions -- so it is not a regime effect,
	// it is either interval-specific or noise. Yahoo will not serve more 5m history, so the only ways
	// to add evidence are:
	//
	//   1. POOL ACROSS INSTRUMENTS. Four names at ~30-40 sessions each is ~130-150 observations.
	//      They overlap in calendar time and SPY/QQQ are highly correlated, so the effective sample is
	//      well below the nominal count -- pooling is reported alongside per-name SIGN AGREEMENT, which
	//      is the honest read when the units are not independent.
	//
	//   2. TEST CONTINUOUSLY, NOT AT A THRESHOLD. A Spearman correlation over every session uses all
	//      the data and picks no cutoff, so it cannot be flattered by a lucky split. If the gate is
	//      real the rank correlation must be NEGATIVE (low exposure -> high return).
	//
	//   3. PERMUTE. With this few trades the question "could the gap arise by chance?" is answered by
	//      shuffling the exposure labels within each instrument and re-measuring, rather than by a
	//      t-statistic whose assumptions the left tail violates.
	// ================================================================================================
	internal static class FiveMinExposureGate
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static double Gate = 0.10;
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };

		private sealed record Tr(string Sym, DateTime D, double R, double Exp, ShortTermState St5);

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

		private static async Task<List<Tr>> Build(string symbol)
		{
			var outp = new List<Tr>();
			FiveperecentBandTest.UseCalendar(symbol);
			var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(daily, 10_000.0);
			List<OhlcBar> intra;
			try { intra = await IntradayClient.GetAsync(symbol, "5m", "60d"); }
			catch (Exception ex) { Console.WriteLine($"  {symbol}: 5m fetch failed ({ex.Message})"); return outp; }
			if (intra.Count < 100) return outp;

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
			// 5m engine -> exposure at the last bar of each session (known by the next session's open)
			var iEng = BankrollSimulator.Run(intra, 10_000.0);
			var lastExp = new Dictionary<DateTime, double>();
			for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
				lastExp[iEng.ReturnDates[k].Date] = iEng.Positions[k];
			// same read for the 5m CANDLE state -- last bar of the session, so it is known by the next open
			var lastSt = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < iEng.StState.Count && k < iEng.ReturnDates.Count; k++)
				lastSt[iEng.ReturnDates[k].Date] = iEng.StState[k];

			double T = 1.0 / 252.0;
			for (int i = 1; i + 1 < daily.Count; i++)
			{
				var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
				if (!lastExp.TryGetValue(dSig, out double iexp)) continue;
				if (!lastSt.TryGetValue(dSig, out var ist)) continue;
				double S = daily[i + 1].Open, ST = daily[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				double iv = h * VolRiskPremium;
				double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) continue;
				double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
				outp.Add(new Tr(symbol, dTr, (cr + po) / ml, iexp, ist));
			}
			return outp;
		}

		private static double Spearman(List<double> a, List<double> b)
		{
			double[] Rank(List<double> v)
			{
				var idx = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray();
				var r = new double[v.Count];
				int p = 0;
				while (p < idx.Length)
				{
					int q = p;
					while (q + 1 < idx.Length && Math.Abs(v[idx[q + 1]] - v[idx[p]]) < 1e-12) q++;
					double avg = (p + q) / 2.0 + 1;
					for (int k = p; k <= q; k++) r[idx[k]] = avg;
					p = q + 1;
				}
				return r;
			}
			var ra = Rank(a); var rb = Rank(b);
			double ma = ra.Average(), mb = rb.Average();
			double num = 0, da = 0, db = 0;
			for (int i = 0; i < ra.Length; i++) { num += (ra[i] - ma) * (rb[i] - mb); da += (ra[i] - ma) * (ra[i] - ma); db += (rb[i] - mb) * (rb[i] - mb); }
			return (da > 0 && db > 0) ? num / Math.Sqrt(da * db) : 0;
		}

		private static void Table(string lbl, List<Tr> t, int denom)
		{
			if (t.Count < 5) { Console.WriteLine($"{lbl,-30} {t.Count,6}   (too few)"); return; }
			var r = t.Select(x => Risk * x.R).ToList();
			double m = r.Average();
			double sd = r.Count > 1 ? Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1)) : 0;
			double e = 1, pk = 1, dd = 0;
			foreach (var z in r) { e *= 1 + z; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
			Console.WriteLine($"{lbl,-30} {t.Count,6} {100.0 * t.Count / denom,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
				$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {100 * r.Min(),9:+0.00;-0.00}");
		}

		public static async Task Run()
		{
			var all = new List<Tr>();
			var perSym = new Dictionary<string, List<Tr>>();
			Console.WriteLine($"\n===== 5-MINUTE EXPOSURE GATE, ALL INSTRUMENTS (gate: exposure < {Gate:0.00}) =====");
			foreach (var sym in Symbols)
			{
				var t = await Build(sym);
				if (t.Count == 0) { Console.WriteLine($"  {sym}: no data"); continue; }
				perSym[sym] = t; all.AddRange(t);
			}
			if (all.Count == 0) { Console.WriteLine("no data at all"); return; }

			Console.WriteLine($"\n{"arm",-30} {"n",6} {"%kept",7} {"mean%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"worst%",9}");
			foreach (var kv in perSym)
			{
				Console.WriteLine($"-- {kv.Key}  ({kv.Value.Count} sessions {kv.Value.Min(x => x.D):yyyy-MM-dd} -> {kv.Value.Max(x => x.D):yyyy-MM-dd}, " +
					$"< {Gate:0.00} on {100.0 * kv.Value.Count(x => x.Exp < Gate) / kv.Value.Count:0.0}%)");
				Table("   ungated", kv.Value, kv.Value.Count);
				Table($"   5m exp < {Gate:0.00}", kv.Value.Where(x => x.Exp < Gate).ToList(), kv.Value.Count);
				Table($"   5m exp < {Gate:0.00} & NOT 5m Bear", kv.Value.Where(x => x.Exp < Gate && x.St5 != ShortTermState.Bear).ToList(), kv.Value.Count);
				Table($"   5m exp < {Gate:0.00} & 5m Bear (ctl)", kv.Value.Where(x => x.Exp < Gate && x.St5 == ShortTermState.Bear).ToList(), kv.Value.Count);
				Table($"   5m exp >= {Gate:0.00} (control)", kv.Value.Where(x => x.Exp >= Gate).ToList(), kv.Value.Count);
			}
			Console.WriteLine($"\n-- POOLED across {perSym.Count} instruments --");
			Table("   ungated", all, all.Count);
			Table($"   NOT 5m Bear only", all.Where(x => x.St5 != ShortTermState.Bear).ToList(), all.Count);
			Table($"   5m exp < {Gate:0.00}", all.Where(x => x.Exp < Gate).ToList(), all.Count);
			Table($"   5m exp < {Gate:0.00} & NOT 5m Bear", all.Where(x => x.Exp < Gate && x.St5 != ShortTermState.Bear).ToList(), all.Count);
			Table($"   5m exp < {Gate:0.00} & 5m Bear (ctl)", all.Where(x => x.Exp < Gate && x.St5 == ShortTermState.Bear).ToList(), all.Count);
			Table($"   5m exp >= {Gate:0.00} (control)", all.Where(x => x.Exp >= Gate).ToList(), all.Count);

			// ---- DOES BearNeutral ALONE REPRODUCE THE GATE? --------------------------------------------
			// The overlap table showed BearNeutral at 47% inside the gate vs 4% outside, so the two are close
			// to COLLINEAR and "which one is the real signal" may not be answerable with this data. The 2x2
			// below is what decides it: if the off-diagonal cells are near-empty they cannot be separated,
			// and the live question becomes whether the gate's NON-BearNeutral half stands on its own.
			Console.WriteLine($"\n-- BearNeutral vs the exposure gate, 2x2 --");
			Table("   BearNeutral, any exposure", all.Where(x => x.St5 == ShortTermState.BearNeutral).ToList(), all.Count);
			Table($"   BearNeutral & exp <  {Gate:0.00}", all.Where(x => x.St5 == ShortTermState.BearNeutral && x.Exp < Gate).ToList(), all.Count);
			Table($"   BearNeutral & exp >= {Gate:0.00}", all.Where(x => x.St5 == ShortTermState.BearNeutral && x.Exp >= Gate).ToList(), all.Count);
			Table($"   NOT BearNeutral & exp <  {Gate:0.00}", all.Where(x => x.St5 != ShortTermState.BearNeutral && x.Exp < Gate).ToList(), all.Count);
			Table($"   NOT BearNeutral & exp >= {Gate:0.00}", all.Where(x => x.St5 != ShortTermState.BearNeutral && x.Exp >= Gate).ToList(), all.Count);
			Console.WriteLine($"   collinearity: of {all.Count(x => x.St5 == ShortTermState.BearNeutral)} BearNeutral sessions, " +
				$"{all.Count(x => x.St5 == ShortTermState.BearNeutral && x.Exp < Gate)} sit inside the gate; " +
				$"the gate holds {all.Count(x => x.Exp < Gate)} of which " +
				$"{all.Count(x => x.Exp < Gate && x.St5 != ShortTermState.BearNeutral)} are NOT BearNeutral");
			Console.WriteLine($"\n-- per-instrument: BearNeutral alone vs the gate (IR) --");
			foreach (var kv in perSym)
			{
				var bn = kv.Value.Where(x => x.St5 == ShortTermState.BearNeutral).ToList();
				var gt = kv.Value.Where(x => x.Exp < Gate).ToList();
				double Ir(List<Tr> t)
				{
					if (t.Count < 4) return double.NaN;
					var r = t.Select(x => Risk * x.R).ToList();
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					return sd > 1e-12 ? m / sd : double.NaN;
				}
				Console.WriteLine($"   {kv.Key,-6} BearNeutral n={bn.Count,3} IR {Ir(bn),8:0.000}    " +
					$"gate n={gt.Count,3} IR {Ir(gt),8:0.000}    ungated IR {Ir(kv.Value),8:0.000}");
			}

			// How much of the exposure gate IS the bear-candle condition? If low exposure is mostly bear
			// candles, the two gates are the same lever and stacking them cannot add anything.
			Console.WriteLine($"\n-- overlap: what the 5m ST state looks like inside the exposure gate --");
			foreach (var grp in new[] { ("exp <  " + Gate.ToString("0.00"), all.Where(x => x.Exp < Gate).ToList()),
			                            ("exp >= " + Gate.ToString("0.00"), all.Where(x => x.Exp >= Gate).ToList()) })
			{
				var parts = grp.Item2.GroupBy(x => x.St5).OrderBy(g => g.Key.ToString())
					.Select(g => $"{g.Key} {100.0 * g.Count() / Math.Max(1, grp.Item2.Count):0}%");
				Console.WriteLine($"   {grp.Item1,-12} n={grp.Item2.Count,4}   {string.Join("  ", parts)}");
			}

			// ---- SIGN AGREEMENT: the honest read when the pooled units are correlated -------------------
			Console.WriteLine($"\n-- per-instrument direction (gated mean minus ungated mean) --");
			int pos = 0;
			foreach (var kv in perSym)
			{
				var g = kv.Value.Where(x => x.Exp < Gate).ToList();
				if (g.Count < 5) { Console.WriteLine($"   {kv.Key,-6} (too few gated: {g.Count})"); continue; }
				double d = Risk * (g.Average(x => x.R) - kv.Value.Average(x => x.R));
				if (d > 0) pos++;
				Console.WriteLine($"   {kv.Key,-6} {100 * d,+9:+0.0000;-0.0000}pp   (gated n={g.Count})");
			}
			Console.WriteLine($"   agreement: {pos}/{perSym.Count} instruments improve under the gate");

			// ---- CONTINUOUS TEST: no threshold to pick ------------------------------------------------
			Console.WriteLine($"\n-- Spearman rank correlation, 5m exposure vs per-trade return --");
			Console.WriteLine($"   (gate is real only if NEGATIVE: low exposure -> high return)");
			foreach (var kv in perSym)
				Console.WriteLine($"   {kv.Key,-6} rho {Spearman(kv.Value.Select(x => x.Exp).ToList(), kv.Value.Select(x => x.R).ToList()),7:+0.000;-0.000}   n={kv.Value.Count}");
			double rhoAll = Spearman(all.Select(x => x.Exp).ToList(), all.Select(x => x.R).ToList());
			Console.WriteLine($"   POOLED rho {rhoAll,7:+0.000;-0.000}   n={all.Count}");

			// ---- PERMUTATION: could the gap arise by chance at this sample size? -----------------------
			// Shuffle exposure WITHIN each instrument, so each name's own return distribution and its own
			// exposure distribution are both preserved and only their pairing is destroyed.
			// The (exposure, ST state) PAIR is shuffled together, so each instrument keeps its own joint
			// distribution of predictors and only the link to the outcome is destroyed. Shuffling the two
			// independently would invent (exposure, state) combinations that never occurred.
			var rnd = new Random(20260813);
			int iters = 20000;
			void Permute(string lbl, Func<Tr, bool> gate)
			{
				double Observed(List<Tr> t)
				{
					var g = t.Where(gate).Select(x => x.R).ToList();
					var ng = t.Where(x => !gate(x)).Select(x => x.R).ToList();
					return (g.Count > 0 && ng.Count > 0) ? g.Average() - ng.Average() : 0;
				}
				double obs = Observed(all);
				int ge = 0;
				for (int it = 0; it < iters; it++)
				{
					var shuffled = new List<Tr>(all.Count);
					foreach (var kv in perSym)
					{
						var pairs = kv.Value.Select(x => (x.Exp, x.St5)).OrderBy(_ => rnd.Next()).ToList();
						for (int i = 0; i < kv.Value.Count; i++)
							shuffled.Add(kv.Value[i] with { Exp = pairs[i].Exp, St5 = pairs[i].St5 });
					}
					if (Observed(shuffled) >= obs) ge++;
				}
				int n = all.Count(gate);
				Console.WriteLine($"   {lbl,-34} n={n,4}  edge {100 * Risk * obs,8:+0.0000;-0.0000}pp  p = {(double)(ge + 1) / (iters + 1),6:0.0000}");
			}
			Console.WriteLine($"\n-- permutation test, {iters} shuffles of the (exposure, ST) pair within each instrument --");
			Permute($"exp < {Gate:0.00}", x => x.Exp < Gate);
			Permute("NOT 5m Bear", x => x.St5 != ShortTermState.Bear);
			Permute($"exp < {Gate:0.00} & NOT 5m Bear", x => x.Exp < Gate && x.St5 != ShortTermState.Bear);
			Permute("BearNeutral alone", x => x.St5 == ShortTermState.BearNeutral);
			Permute($"exp < {Gate:0.00} & NOT BearNeutral", x => x.Exp < Gate && x.St5 != ShortTermState.BearNeutral);
		}
	}
}
