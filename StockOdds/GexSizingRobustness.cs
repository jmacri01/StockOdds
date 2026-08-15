using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// RE-EXAMINE THE SHIPPED GEX SIZING UNDER THE ROBUSTNESS STANDARD THAT KILLED THE 5m GATE.
	//
	// `risk = base x min(callPut, 2)` was validated on a paired t (+2.40 on SPY, +3.26 on QQQ) and a
	// matched-risk control. It was never subjected to a BLOCK BOOTSTRAP over weeks or a
	// drop-the-worst-period re-run -- and the 5m exposure gate passed a 20k permutation test at
	// p = 0.033 while being entirely one week. Permutation asks whether the pairing is random; it does
	// not ask whether one episode is carrying the result. So the same scrutiny is applied here.
	//
	// The sample is far larger (2022-03+, ~4 years, four instruments), which is exactly why this is
	// worth doing properly rather than assuming the earlier verdict transfers.
	//
	// TWO VERSIONS ARE REPORTED, because they answer different questions:
	//   AS SHIPPED   - multiplier min(cp,2), which averages ~1.31x. Part of any gain is simply more
	//                  capital at risk, so this arm is NOT a clean test of the signal.
	//   MEAN-1       - the same multiplier rescaled so average risk equals flat exactly. Only this
	//                  version isolates cov(stake, outcome), which is the whole mechanism claimed.
	// ================================================================================================
	internal static class GexSizingRobustness
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static double Cap = 2.0;
		public static DateTime From = new DateTime(2022, 3, 30);
		public static (string sym, string dat)[] Symbols =
			{ ("SPY", "spx"), ("QQQ", "qqq"), ("IWM", "iwm"), ("GLD", "gld") };

		private sealed record Tr(string Sym, DateTime D, double R, double Cp);

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

		private static string Wk(DateTime d) => $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";
		private static string Mo(DateTime d) => $"{d:yyyy-MM}";

		public static async Task Run()
		{
			var all = new List<Tr>();
			foreach (var (symbol, dat) in Symbols)
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
				var cp = LoadCallPut(dat);
				if (cp.Count == 0) continue;
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
				double T = 1.0 / 252.0;
				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (dTr < From) continue;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					if (!cp.TryGetValue(dSig, out double c)) continue;
					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					all.Add(new Tr(symbol, dTr, (cr + po) / ml, c));
				}
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			Console.WriteLine($"\n===== IS GEX SIZING STILL VALID? robustness re-run =====");
			Console.WriteLine($"{all.Count} sessions {all.Min(x => x.D):yyyy-MM-dd} -> {all.Max(x => x.D):yyyy-MM-dd}, " +
				$"{all.Select(x => Wk(x.D)).Distinct().Count()} weeks, {all.Select(x => Mo(x.D)).Distinct().Count()} months");
			Console.WriteLine($"multiplier min(callPut, {Cap:0.0}); mean-1 arm rescales it so average risk equals flat");

			var rnd = new Random(20260815);
			void Assess(string lbl, List<Tr> src)
			{
				if (src.Count < 40) { Console.WriteLine($"{lbl,-22}   n={src.Count}, too few"); return; }
				double Mult(Tr x) => Math.Min(Cap, x.Cp);
				double meanMult = src.Average(Mult);
				double avgRisk = Risk * meanMult;
				// paired difference AS SHIPPED (includes the extra stake)
				var dShip = src.Select(x => Risk * (Mult(x) - 1.0) * x.R).ToList();
				// paired difference MEAN-1 (pure covariance, average risk identical to flat)
				var dNorm = src.Select(x => Risk * (Mult(x) / meanMult - 1.0) * x.R).ToList();
				double T2(List<double> d)
				{
					double m = d.Average();
					double sd = Math.Sqrt(d.Sum(z => (z - m) * (z - m)) / (d.Count - 1));
					return m / (sd / Math.Sqrt(d.Count));
				}
				// block bootstrap over WEEKS on the mean-1 difference
				var wks = src.GroupBy(x => Wk(x.D)).ToList();
				int n = 0, le = 0;
				for (int it = 0; it < 4000; it++)
				{
					var samp = new List<Tr>(src.Count);
					for (int w = 0; w < wks.Count; w++) samp.AddRange(wks[rnd.Next(wks.Count)]);
					if (samp.Count < 40) continue;
					double mm = samp.Average(Mult);
					if (mm < 1e-9) continue;
					n++;
					if (samp.Average(x => Risk * (Mult(x) / mm - 1.0) * x.R) <= 0) le++;
				}
				// drop the worst WEEK and the worst MONTH, re-measure the mean-1 difference
				string wWorst = src.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.R)).First().Key;
				string mWorst = src.GroupBy(x => Mo(x.D)).OrderBy(g => g.Average(x => x.R)).First().Key;
				double Re(IEnumerable<Tr> s2)
				{
					var t = s2.ToList();
					double mm = t.Average(Mult);
					return 100 * t.Average(x => Risk * (Mult(x) / mm - 1.0) * x.R);
				}
				Console.WriteLine($"{lbl,-22} {src.Count,5} {100 * avgRisk,7:0.0}% {100 * dShip.Average(),10:+0.0000;-0.0000} {T2(dShip),7:+0.00;-0.00} " +
					$"{100 * dNorm.Average(),10:+0.0000;-0.0000} {T2(dNorm),7:+0.00;-0.00} {(n > 0 ? (double)le / n : 1),8:0.000} " +
					$"{Re(src.Where(x => Wk(x.D) != wWorst)),10:+0.0000;-0.0000} {Re(src.Where(x => Mo(x.D) != mWorst)),11:+0.0000;-0.0000}");
			}

			Console.WriteLine($"\n{"scope",-22} {"n",5} {"avgRisk",8} {"shipΔ pp",10} {"t",7} {"mean1Δ",10} {"t",7} {"P(<=0)",8} {"-worst wk",10} {"-worst mo",11}");
			Assess("SPY+QQQ (the scope)", all.Where(x => x.Sym is "SPY" or "QQQ").ToList());
			foreach (var (sym, _) in Symbols)
				Assess($"  {sym}", all.Where(x => x.Sym == sym).ToList());
			Assess("all four pooled", all);

			// Split-half in TIME: fit nothing, just check the second half agrees with the first.
			Console.WriteLine($"\n-- split-half in time, SPY+QQQ, mean-1 difference --");
			var sq = all.Where(x => x.Sym is "SPY" or "QQQ").OrderBy(x => x.D).ToList();
			int mid = sq.Count / 2;
			foreach (var (lbl, part) in new[] { ("first half", sq.Take(mid).ToList()), ("second half", sq.Skip(mid).ToList()) })
			{
				double mm = part.Average(x => Math.Min(Cap, x.Cp));
				var d = part.Select(x => Risk * (Math.Min(Cap, x.Cp) / mm - 1.0) * x.R).ToList();
				double m = d.Average();
				double sd = Math.Sqrt(d.Sum(z => (z - m) * (z - m)) / (d.Count - 1));
				Console.WriteLine($"   {lbl,-12} {part.First().D:yyyy-MM} -> {part.Last().D:yyyy-MM}  n={part.Count,4}  " +
					$"{100 * m,9:+0.0000;-0.0000}pp  t {m / (sd / Math.Sqrt(d.Count)),6:+0.00;-0.00}");
			}
		}
	}
}
