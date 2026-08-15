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
		public static int MinNSweep = 15;

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
			// ---- THRESHOLD SWEEP -----------------------------------------------------------------------
			// The Spearman above already measures the underlying relationship without any cutoff, so a
			// threshold sweep only carves that same relationship into pieces. What it can legitimately
			// answer is the SHAPE: a real effect should degrade smoothly away from its best cut, and the
			// per-instrument signs should hold across neighbouring thresholds. A lone spike at one value
			// with neighbours that disagree is a split artifact, not a tuned parameter.
			Console.WriteLine($"\n-- threshold sweep (shipped daily filters ON; Spearman is the cutoff-free reference) --");
			Console.WriteLine($"{"gate",8} {"n",5} {"%kept",7} {"mean%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"perm p",8}  per-instrument IR");
			foreach (double gv in new[] { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.40 })
			{
				var t = all.Where(x => x.Exp < gv).ToList();
				if (t.Count < MinNSweep) { Console.WriteLine($"{gv,8:0.00} {t.Count,5}   (too few)"); continue; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var z in r) { e *= 1 + z; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				// permutation for THIS threshold, same construction as above
				double Obs(List<Tr> src)
				{
					var g2 = src.Where(x => x.Exp < gv).Select(x => x.R).ToList();
					var ng = src.Where(x => x.Exp >= gv).Select(x => x.R).ToList();
					return (g2.Count > 0 && ng.Count > 0) ? g2.Average() - ng.Average() : 0;
				}
				double ob = Obs(all);
				int ge2 = 0, it2 = 5000;
				var rnd2 = new Random(20260814);
				for (int it = 0; it < it2; it++)
				{
					var sh = new List<Tr>(all.Count);
					foreach (var kv in perSym)
					{
						var pr = kv.Value.Select(x => (x.Exp, x.St5)).OrderBy(_ => rnd2.Next()).ToList();
						for (int i = 0; i < kv.Value.Count; i++) sh.Add(kv.Value[i] with { Exp = pr[i].Exp, St5 = pr[i].St5 });
					}
					if (Obs(sh) >= ob) ge2++;
				}
				string per = string.Join("  ", perSym.Select(kv =>
				{
					var q = kv.Value.Where(x => x.Exp < gv).ToList();
					if (q.Count < 8) return $"{kv.Key}:--";
					var rr = q.Select(x => Risk * x.R).ToList();
					double mm = rr.Average();
					double ss = Math.Sqrt(rr.Sum(z => (z - mm) * (z - mm)) / (rr.Count - 1));
					return $"{kv.Key}:{(ss > 1e-12 ? mm / ss : 0):0.00}";
				}));
				// The EXCLUDED side matters as much as the kept side. A flat kept-curve with a collapsing
				// complement means the gate works by dropping bad sessions, not by finding good ones --
				// a different mechanism with a different implication for where to set the threshold.
				var ex = all.Where(x => x.Exp >= gv).ToList();
				string exs = "--";
				if (ex.Count >= 5)
				{
					var er = ex.Select(x => Risk * x.R).ToList();
					double em = er.Average();
					double esd = er.Count > 1 ? Math.Sqrt(er.Sum(z => (z - em) * (z - em)) / (er.Count - 1)) : 0;
					exs = $"n={ex.Count,3} {100 * em,8:+0.0000;-0.0000} IR {(esd > 1e-12 ? em / esd : 0),6:0.00}";
				}
				Console.WriteLine($"{gv,8:0.00} {t.Count,5} {100.0 * t.Count / all.Count,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 1e-12 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(double)(ge2 + 1) / (it2 + 1),8:0.0000} | EXCLUDED {exs}");
			}

			// ---- ROBUSTNESS: re-run the whole sweep with the single worst week removed -----------------
			// If one week drives the result, every threshold that depends on it should move. Thresholds
			// that survive its removal are measuring something distributed across the sample.
			string Wk(DateTime d) => $"{System.Globalization.ISOWeek.GetYear(d)}-W{System.Globalization.ISOWeek.GetWeekOfYear(d):00}";
			var worstWeek = all.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.R)).First().Key;
			var exW = all.Where(x => Wk(x.D) != worstWeek).ToList();
			Console.WriteLine($"\n-- sweep with the worst week ({worstWeek}, {all.Count - exW.Count} sessions) REMOVED --");
			Console.WriteLine($"{"gate",8} {"n",5} {"kept mean%",11} {"kept IR",9} | {"excl n",7} {"excl mean%",11} {"excl IR",9}");
			foreach (double gv in new[] { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.40 })
			{
				var k = exW.Where(x => x.Exp < gv).Select(x => Risk * x.R).ToList();
				var e2 = exW.Where(x => x.Exp >= gv).Select(x => Risk * x.R).ToList();
				if (k.Count < 10 || e2.Count < 5) { Console.WriteLine($"{gv,8:0.00}   (too few)"); continue; }
				double km = k.Average(), em = e2.Average();
				double ks = Math.Sqrt(k.Sum(z => (z - km) * (z - km)) / (k.Count - 1));
				double es = Math.Sqrt(e2.Sum(z => (z - em) * (z - em)) / (e2.Count - 1));
				Console.WriteLine($"{gv,8:0.00} {k.Count,5} {100 * km,11:+0.0000;-0.0000} {(ks > 1e-12 ? km / ks : 0),9:0.000} | " +
					$"{e2.Count,7} {100 * em,11:+0.0000;-0.0000} {(es > 1e-12 ? em / es : 0),9:0.000}");
			}

			// ---- IS THE TOXIC TAIL ONE EVENT? ----------------------------------------------------------
			// The reframed gate rests entirely on a small high-exposure set being bad. With only 13-29
			// sessions in it, the whole finding collapses if they are one selloff week or one instrument.
			// Concentration is therefore a harder constraint here than any p-value.
			foreach (double gv in new[] { 0.25, 0.30 })
			{
				var tail = all.Where(x => x.Exp >= gv).OrderBy(x => x.D).ToList();
				if (tail.Count < 3) continue;
				var byWeek = tail.GroupBy(x => $"{System.Globalization.ISOWeek.GetYear(x.D)}-W{System.Globalization.ISOWeek.GetWeekOfYear(x.D):00}")
					.OrderByDescending(g => g.Count()).ToList();
				var bySym = tail.GroupBy(x => x.Sym).OrderByDescending(g => g.Count()).ToList();
				var losers = tail.Where(x => x.R <= 0).ToList();
				Console.WriteLine($"\n-- the EXCLUDED tail at exp >= {gv:0.00}: {tail.Count} sessions, is it one event? --");
				Console.WriteLine($"   spans {tail.First().D:yyyy-MM-dd} -> {tail.Last().D:yyyy-MM-dd} over {byWeek.Count} distinct weeks; " +
					$"biggest week holds {byWeek[0].Count()} ({100.0 * byWeek[0].Count() / tail.Count:0.0}%)");
				Console.WriteLine($"   by instrument: {string.Join(", ", bySym.Select(g => $"{g.Key} {g.Count()}"))}");
				Console.WriteLine($"   losing sessions: {losers.Count} of {tail.Count}; " +
					$"weeks holding a loser: {string.Join(", ", losers.GroupBy(x => $"{System.Globalization.ISOWeek.GetYear(x.D)}-W{System.Globalization.ISOWeek.GetWeekOfYear(x.D):00}").OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key}x{g.Count()}"))}");
				// Drop the single worst week and re-measure: if the tail is still bad without it, the
				// effect is distributed rather than being one episode wearing a threshold as a disguise.
				var wk = byWeek[0].Key;
				var exWorst = tail.Where(x => $"{System.Globalization.ISOWeek.GetYear(x.D)}-W{System.Globalization.ISOWeek.GetWeekOfYear(x.D):00}" != wk).ToList();
				if (exWorst.Count >= 5)
				{
					var r0 = tail.Select(x => Risk * x.R).ToList();
					var r1 = exWorst.Select(x => Risk * x.R).ToList();
					Console.WriteLine($"   tail mean {100 * r0.Average(),8:+0.0000;-0.0000}  ->  " +
						$"{100 * r1.Average(),8:+0.0000;-0.0000} after dropping its biggest week ({wk}, n={tail.Count - exWorst.Count})");
				}
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
