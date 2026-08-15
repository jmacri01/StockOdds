using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// LEVEL vs TRANSITION: is it better to enter WHILE 5m exposure is low, or AFTER it recovers?
	//
	// The shipped-plus-5m rule is a LEVEL rule: enter when 5m exposure sits below 0.10. The alternative
	// is a TRANSITION rule -- wait for exposure to fall below 0.10, then enter once it climbs back
	// above. Same underlying observation, opposite theory of what it means:
	//
	//   LEVEL      = fade the washout. Buy premium risk while the fast tape is still flushed.
	//   TRANSITION = confirm the turn. Only sell premium once the tape has actually recovered.
	//
	// TIMING CONSTRAINT. The spread opens at the session OPEN, so any trigger must be complete by the
	// PRIOR close. That rules out "cross back above 0.10 intraday and enter then" -- by the time the
	// crossing happens the entry has passed. The two implementable forms are:
	//
	//   INTRA-SESSION rebound: during the prior session, exposure dipped below 0.10 and ENDED above it.
	//   DAY-OVER-DAY  rebound: the session before last closed below 0.10 and the prior session closed
	//                          above it.
	//
	// Both are checked, plus a "never dipped, just high" control -- without it, any rebound result could
	// simply be the high-exposure state, which the 2x2 already showed is the WORSE half.
	// ================================================================================================
	internal static class FiveMinReboundEntry
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;      // shipped daily filters stay ON
		public static double Gate = 0.10;
		public static int MinN = 15;
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };

		// LastExp/MinExp describe the PRIOR session's 5m exposure path; PrevLastExp is the session before.
		private sealed record Tr(string Sym, DateTime D, double R, double LastExp, double MinExp, double PrevLastExp);

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
			catch { return outp; }
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

			// The FULL 5m exposure path per session, not just its closing value -- the transition rule
			// needs to know whether exposure dipped at any point inside the session.
			var iEng = BankrollSimulator.Run(intra, 10_000.0);
			var path = new Dictionary<DateTime, List<double>>();
			for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
			{
				var d = iEng.ReturnDates[k].Date;
				if (!path.TryGetValue(d, out var lst)) path[d] = lst = new List<double>();
				lst.Add(iEng.Positions[k]);
			}
			var sessDates = path.Keys.OrderBy(d => d).ToList();
			var prevSess = new Dictionary<DateTime, DateTime>();
			for (int i = 1; i < sessDates.Count; i++) prevSess[sessDates[i]] = sessDates[i - 1];

			double T = 1.0 / 252.0;
			for (int i = 1; i + 1 < daily.Count; i++)
			{
				var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
				if (!path.TryGetValue(dSig, out var p) || p.Count < 4) continue;
				double lastExp = p[^1], minExp = p.Min();
				double prevLast = prevSess.TryGetValue(dSig, out var dPrev) && path.TryGetValue(dPrev, out var pp) && pp.Count > 0
					? pp[^1] : double.NaN;

				double S = daily[i + 1].Open, ST = daily[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				double iv = h * VolRiskPremium;
				double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) continue;
				double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
				outp.Add(new Tr(symbol, dTr, (cr + po) / ml, lastExp, minExp, prevLast));
			}
			return outp;
		}

		public static async Task Run()
		{
			var all = new List<Tr>();
			var perSym = new Dictionary<string, List<Tr>>();
			foreach (var s in Symbols)
			{
				var t = await Build(s);
				if (t.Count > 0) { perSym[s] = t; all.AddRange(t); }
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			int denom = all.Count;
			void Table(string lbl, List<Tr> t)
			{
				if (t.Count < MinN) { Console.WriteLine($"{lbl,-44} {t.Count,5}   REFUSED (n < {MinN})"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double se = sd / Math.Sqrt(r.Count);
				double e = 1, pk = 1, dd = 0;
				foreach (var z in r) { e *= 1 + z; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				Console.WriteLine($"{lbl,-44} {t.Count,5} {100.0 * t.Count / denom,6:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 1e-12 ? m / sd : 0),8:0.000} " +
					$"{(se > 0 ? m / se : 0),7:+0.00;-0.00} {dd,8:0.00} {100 * r.Min(),8:+0.00;-0.00}");
			}

			Console.WriteLine($"\n===== LEVEL vs TRANSITION on 5m exposure (shipped daily filters ON) =====");
			Console.WriteLine($"{all.Count} sessions {all.Min(x => x.D):yyyy-MM-dd} -> {all.Max(x => x.D):yyyy-MM-dd}, " +
				$"{perSym.Count} instruments");
			Console.WriteLine($"prior session DIPPED below {Gate:0.00} at some point: {100.0 * all.Count(x => x.MinExp < Gate) / all.Count:0.0}%;  " +
				$"ENDED below: {100.0 * all.Count(x => x.LastExp < Gate) / all.Count:0.0}%");
			// A 100% dip rate would make the transition rule vacuous, so verify it is the exposure path and
			// not a construction bug: print the actual within-session minima and the ranges they come from.
			var mins = all.Select(x => x.MinExp).OrderBy(v => v).ToList();
			var lasts = all.Select(x => x.LastExp).OrderBy(v => v).ToList();
			Console.WriteLine($"   within-session MIN exposure: p10 {mins[mins.Count / 10]:0.000}, median {mins[mins.Count / 2]:0.000}, " +
				$"p90 {mins[9 * mins.Count / 10]:0.000}, max {mins[^1]:0.000}");
			Console.WriteLine($"   session-CLOSING exposure:    p10 {lasts[lasts.Count / 10]:0.000}, median {lasts[lasts.Count / 2]:0.000}, " +
				$"p90 {lasts[9 * lasts.Count / 10]:0.000}, max {lasts[^1]:0.000}");
			Console.WriteLine($"   sessions whose min is EXACTLY 0: {100.0 * all.Count(x => x.MinExp <= 1e-9) / all.Count:0.0}%" +
				$"  -> if near 100%, the 5m engine resets/floors each session and 'dipped below' carries no information");

			Console.WriteLine($"\n{"arm",-44} {"n",5} {"%all",6} {"mean%",10} {"win%",7} {"IR",8} {"t",7} {"maxDD%",8} {"worst%",8}");
			Table("no 5m gate (shipped only)", all);
			Table($"LEVEL: ended below {Gate:0.00}  [the earlier rule]", all.Where(x => x.LastExp < Gate).ToList());
			Table($"TRANSITION: dipped below, ended above", all.Where(x => x.MinExp < Gate && x.LastExp >= Gate).ToList());
			Table($"  control: never dipped, ended above", all.Where(x => x.MinExp >= Gate && x.LastExp >= Gate).ToList());
			Table($"DAY-OVER-DAY: prev close < {Gate:0.00}, last >= ", all.Where(x => !double.IsNaN(x.PrevLastExp) && x.PrevLastExp < Gate && x.LastExp >= Gate).ToList());
			Table($"  and its complement: prev >= , last >= ", all.Where(x => !double.IsNaN(x.PrevLastExp) && x.PrevLastExp >= Gate && x.LastExp >= Gate).ToList());

			// The transition rule can only be judged against the state it transitions INTO. Everything with
			// lastExp >= gate is the high-exposure half, which the earlier 2x2 showed is the weaker one --
			// so the question is whether "dipped first" rescues any of it.
			var high = all.Where(x => x.LastExp >= Gate).ToList();
			Console.WriteLine($"\n-- within the high-exposure half only ({high.Count} sessions), does 'dipped first' help? --");
			denom = Math.Max(1, high.Count);
			Table("  dipped below earlier in the session", high.Where(x => x.MinExp < Gate).ToList());
			Table("  never dipped", high.Where(x => x.MinExp >= Gate).ToList());
			denom = all.Count;

			Console.WriteLine($"\n-- per-instrument: LEVEL vs TRANSITION (IR) --");
			foreach (var kv in perSym)
			{
				double Ir(List<Tr> t)
				{
					if (t.Count < 8) return double.NaN;
					var r = t.Select(x => Risk * x.R).ToList();
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					return sd > 1e-12 ? m / sd : double.NaN;
				}
				var lvl = kv.Value.Where(x => x.LastExp < Gate).ToList();
				var trn = kv.Value.Where(x => x.MinExp < Gate && x.LastExp >= Gate).ToList();
				Console.WriteLine($"   {kv.Key,-6} ungated n={kv.Value.Count,3} IR {Ir(kv.Value),7:0.000}   " +
					$"LEVEL n={lvl.Count,3} IR {Ir(lvl),7:0.000}   TRANSITION n={trn.Count,3} IR {Ir(trn),7:0.000}");
			}
		}
	}
}
