using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// RELATIVE GATE: exposure below its OWN trailing average, instead of below a fixed 0.10.
	//
	// This addresses a genuine defect in the absolute rule. 0.10 is an arbitrary number, and the 5m
	// exposure distribution is demonstrably instrument-specific -- the closing-exposure median across
	// the pooled set is ~0.11, but the fraction of sessions under 0.10 ran 48.5% on SPY, 48.7% on QQQ
	// and 58.6% on IWM. A self-normalising cut ("is exposure low FOR THIS NAME, RIGHT NOW") is the
	// scale-free version of the same intuition and needs no per-instrument calibration.
	//
	// THE MOVING AVERAGE IS BUILT FROM EVERY 5m SESSION, not just tradeable ones. Exposure exists on
	// days the shipped filters reject, so restricting the MA to qualifying sessions would both shorten
	// the history and make the average conditional on the filter -- warmup would then eat most of a
	// 60-day window. Using the full series keeps ~60 observations per instrument behind each average.
	//
	// Everything is scored against BOTH the no-filter baseline and the absolute < 0.10 rule, on the
	// W23-removed sample with a week-block bootstrap, since the full-sample version of this family is
	// known to be one week.
	// ================================================================================================
	internal static class FiveMinRelativeGate
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static int MinN = 12;
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };
		public static int[] MaWindows = { 3, 4, 5, 6, 7, 8, 10, 12, 15, 20 };
		public static double[] Ratios = { 1.00 };

		private sealed record Tr(string Sym, DateTime D, double R, double Exp, Dictionary<int, double> Ma);

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

				// Closing 5m exposure for EVERY 5m session, in date order -- the MA's raw material.
				var iEng = BankrollSimulator.Run(intra, 10_000.0);
				var lastOf = new Dictionary<DateTime, double>();
				for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
					lastOf[iEng.ReturnDates[k].Date] = iEng.Positions[k];
				var ordered = lastOf.OrderBy(kv => kv.Key).ToList();
				var idxOf = new Dictionary<DateTime, int>();
				for (int k = 0; k < ordered.Count; k++) idxOf[ordered[k].Key] = k;

				double T = 1.0 / 252.0;
				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					if (!idxOf.TryGetValue(dSig, out int ix)) continue;
					var ma = new Dictionary<int, double>();
					foreach (int w in MaWindows)
					{
						if (ix + 1 < w) continue;                       // not enough history yet
						double s2 = 0;
						for (int k = 0; k < w; k++) s2 += ordered[ix - k].Value;
						ma[w] = s2 / w;
					}
					if (ma.Count == 0) continue;
					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					all.Add(new Tr(symbol, dTr, (cr + po) / ml, ordered[ix].Value, ma));
				}
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			var worst = all.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.R)).First().Key;
			var exW = all.Where(x => Wk(x.D) != worst).ToList();
			var weeks = exW.GroupBy(x => Wk(x.D)).ToList();
			var rnd = new Random(20260814);

			Console.WriteLine($"\n===== RELATIVE GATE: exposure below its OWN trailing average =====");
			Console.WriteLine($"{all.Count} sessions, {all.Select(x => Wk(x.D)).Distinct().Count()} weeks; " +
				$"MA built from every 5m session, not just tradeable ones");
			Console.WriteLine($"baseline {100 * Risk * all.Average(x => x.R):+0.0000;-0.0000}%  |  " +
				$"worst week {worst} removed: {100 * Risk * exW.Average(x => x.R):+0.0000;-0.0000}% over {exW.Count}");

			Console.WriteLine($"\n{"rule",-30} {"n",4} {"mean%",9} {"IR",7} | {"n",4} {"mean%",9} {"IR",7} {"edge",9} {"P(<=0)",7}");
			void Show(string lbl, Func<Tr, bool> f)
			{
				string Fmt(List<Tr> src)
				{
					var t = src.Where(f).ToList();
					if (t.Count < MinN) return $"{t.Count,4}   too few     ";
					var r = t.Select(x => Risk * x.R).ToList();
					double m = r.Average();
					double sd = r.Count > 1 ? Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1)) : 0;
					return $"{t.Count,4} {100 * m,9:+0.0000;-0.0000} {(sd > 1e-12 ? m / sd : 0),7:0.000}";
				}
				string ed = "       --", ps = "     --";
				if (exW.Count(f) >= MinN)
				{
					double edge = Risk * (exW.Where(f).Average(x => x.R) - exW.Average(x => x.R));
					ed = $"{100 * edge,9:+0.0000;-0.0000}";
					int n = 0, le = 0;
					for (int it = 0; it < 3000; it++)
					{
						var samp = new List<Tr>(exW.Count);
						for (int w = 0; w < weeks.Count; w++) samp.AddRange(weeks[rnd.Next(weeks.Count)]);
						if (samp.Count(f) < 5) continue;
						n++;
						if (Risk * (samp.Where(f).Average(x => x.R) - samp.Average(x => x.R)) <= 0) le++;
					}
					ps = n > 0 ? $"{(double)le / n,7:0.000}" : "     --";
				}
				Console.WriteLine($"{lbl,-30} {Fmt(all)} | {Fmt(exW)} {ed} {ps}");
			}
			Show("baseline (no filter)", _ => true);
			Show("ABSOLUTE exp < 0.10", x => x.Exp < 0.10);
			foreach (int w in MaWindows)
				foreach (double q in Ratios)
					Show($"exp < {q:0.00} x MA{w}", x => x.Ma.TryGetValue(w, out double m) && x.Exp < q * m);
			Console.WriteLine("  -- the complement of the headline relative rule, as a sign control --");
			foreach (int w in MaWindows)
				Show($"exp >= MA{w} (control)", x => x.Ma.TryGetValue(w, out double m) && x.Exp >= m);

			Console.WriteLine($"\n-- how often does each rule fire, and does it agree with the absolute one? --");
			foreach (int w in MaWindows)
			{
				var have = all.Where(x => x.Ma.ContainsKey(w)).ToList();
				if (have.Count < MinN) continue;
				int rel = have.Count(x => x.Exp < x.Ma[w]);
				int abs = have.Count(x => x.Exp < 0.10);
				int both = have.Count(x => x.Exp < x.Ma[w] && x.Exp < 0.10);
				Console.WriteLine($"   MA{w,-3} fires {100.0 * rel / have.Count,5:0.0}% vs absolute {100.0 * abs / have.Count,5:0.0}%; " +
					$"overlap {both} of {rel} relative / {abs} absolute  (agreement {100.0 * (have.Count - rel - abs + 2.0 * both) / have.Count,5:0.0}%)");
			}
		}
	}
}
