using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// PERSISTENCE INSTEAD OF LEVEL: K consecutive 5m candles under a threshold, not one reading under a
	// tighter one. The idea is that a sustained low reading is less noisy than a single closing print.
	//
	// Timing is unchanged: entry is at the next session's OPEN, so the K bars are the LAST K bars of
	// the prior session.
	//
	// ONE STRUCTURAL FACT CONSTRAINS THIS BEFORE ANY NUMBER IS READ. 5m exposure touches ~0 inside
	// every session (within-session min: median 0.000, max 0.098 over 107 sessions). So a persistence
	// condition anchored at the END of the session is asking something quite different from one that
	// could fire anywhere -- and the looser the threshold, the closer "last K bars all under" comes to
	// simply restating the closing level. The K=1 row is exactly the old level rule, so the grid shows
	// directly whether duration adds anything the closing print did not already carry.
	//
	// Every cell reports the W23-removed value alongside the full-sample one. The 5m window is 14 weeks
	// and one of them (2026-W23) has already been shown to manufacture this entire family of results.
	// ================================================================================================
	internal static class FiveMinPersistence
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
		public static int[] Bars = { 1, 3, 5, 10, 20 };
		public static double[] Thresholds = { 0.10, 0.15, 0.20, 0.25 };

		// TrailMax[k] = the maximum 5m exposure over the last (k+1) bars of the prior session.
		private sealed record Tr(string Sym, DateTime D, double R, double[] TrailMax);

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
			int maxK = Bars.Max();
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
				var iEng = BankrollSimulator.Run(intra, 10_000.0);
				var path = new Dictionary<DateTime, List<double>>();
				for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
				{
					var d = iEng.ReturnDates[k].Date;
					if (!path.TryGetValue(d, out var lst)) path[d] = lst = new List<double>();
					lst.Add(iEng.Positions[k]);
				}

				double T = 1.0 / 252.0;
				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					if (!path.TryGetValue(dSig, out var p) || p.Count < maxK) continue;
					var tm = new double[maxK];
					double run = double.NegativeInfinity;
					for (int k = 0; k < maxK; k++) { run = Math.Max(run, p[p.Count - 1 - k]); tm[k] = run; }

					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					all.Add(new Tr(symbol, dTr, (cr + po) / ml, tm));
				}
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			var worst = all.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.R)).First().Key;
			var exW = all.Where(x => Wk(x.D) != worst).ToList();
			double Base(List<Tr> s) => Risk * s.Average(x => x.R);

			Console.WriteLine($"\n===== PERSISTENCE: K consecutive 5m candles under a threshold =====");
			Console.WriteLine($"{all.Count} sessions, {all.Select(x => Wk(x.D)).Distinct().Count()} weeks, shipped daily filters ON");
			Console.WriteLine($"baseline (no 5m filter): {100 * Base(all):+0.0000;-0.0000}%   " +
				$"| worst week {worst} removed: {100 * Base(exW):+0.0000;-0.0000}% over {exW.Count} sessions");
			Console.WriteLine($"K=1 reproduces the old LEVEL rule, so any value of duration must show up as K>1 beating it.");

			var rnd = new Random(20260814);
			var weeksAll = all.GroupBy(x => Wk(x.D)).ToList();
			Console.WriteLine($"\n{"K bars",7} {"thresh",7} | {"n",4} {"mean%",9} {"IR",7} | {"n",4} {"mean%",9} {"IR",7} | {"edge -W23",10} {"P(<=0)",7}");
			foreach (int K in Bars)
				foreach (double th in Thresholds)
				{
					bool Gate(Tr x) => x.TrailMax[K - 1] < th;
					string Cell(List<Tr> src)
					{
						var t = src.Where(Gate).ToList();
						if (t.Count < MinN) return $"{t.Count,4}    too few    ";
						var r = t.Select(x => Risk * x.R).ToList();
						double m = r.Average();
						double sd = r.Count > 1 ? Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1)) : 0;
						return $"{t.Count,4} {100 * m,9:+0.0000;-0.0000} {(sd > 1e-12 ? m / sd : 0),7:0.000}";
					}
					// Edge and its block-bootstrap p are computed on the W23-REMOVED sample, because the
					// full-sample version of this family is already known to be that one week.
					double edge = exW.Count(Gate) >= MinN ? Risk * (exW.Where(Gate).Average(x => x.R) - exW.Average(x => x.R)) : double.NaN;
					string pstr = "   --";
					if (!double.IsNaN(edge))
					{
						var wks = exW.GroupBy(x => Wk(x.D)).ToList();
						int n = 0, le = 0;
						for (int it = 0; it < 3000; it++)
						{
							var samp = new List<Tr>(exW.Count);
							for (int w = 0; w < wks.Count; w++) samp.AddRange(wks[rnd.Next(wks.Count)]);
							if (samp.Count(Gate) < 5) continue;
							n++;
							if (Risk * (samp.Where(Gate).Average(x => x.R) - samp.Average(x => x.R)) <= 0) le++;
						}
						pstr = n > 0 ? $"{(double)le / n,7:0.000}" : "   --";
					}
					string mark = K == 5 && Math.Abs(th - 0.20) < 1e-9 ? "  <== asked" : (K == 1 && Math.Abs(th - 0.10) < 1e-9 ? "  <== old rule" : "");
					Console.WriteLine($"{K,7} {th,7:0.00} | {Cell(all)} | {Cell(exW)} | " +
						$"{(double.IsNaN(edge) ? "      --" : $"{100 * edge,10:+0.0000;-0.0000}")} {pstr}{mark}");
				}
			Console.WriteLine($"\nleft block = full sample, middle = W23 removed, right = edge vs baseline on the W23-removed sample");
		}
	}
}
