using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// SIZE ON 5m EXPOSURE INSTEAD OF GATING ON IT. Lower exposure -> larger stake.
	//
	// WHY SIZING IS THE BETTER TEST. A gate splits the sample; sizing keeps every session and only
	// varies the stake, so the comparison against flat is PAIRED -- same sessions, same outcomes, only
	// the weights differ. That isolates cov(stake, outcome), which is the entire mechanism by which a
	// sizing rule can add value, and it does not throw away half the observations to do it.
	//
	// THE CONTROL THAT MAKES IT MEANINGFUL. Every multiplier here is normalised so its MEAN IS EXACTLY
	// 1.0 across the sample. Without that, a rule that happens to stake more on average beats flat for
	// reasons that have nothing to do with the signal -- the flat-haircut trap. With it, average risk
	// is identical by construction and any difference is pure covariance.
	//
	// GEX SIZING IS DROPPED, as asked. It is shown once for reference only, since it is what currently
	// ships, and it was already measured as inert inside the low-exposure population (paired t +0.06).
	//
	// Headline numbers are on the W23-REMOVED sample. The full-sample column is printed but this
	// family of results has already been shown to be that one week.
	// ================================================================================================
	internal static class FiveMinExposureSizing
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };

		private sealed record Tr(string Sym, DateTime D, double R, double Exp, double Cp);

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
			string path = System.IO.Path.Combine(System.IO.Path.GetFullPath(Universe.DataDir), $"gex_uw_{dataSym}.csv");
			if (!System.IO.File.Exists(path)) return map;
			var lines = System.IO.File.ReadAllLines(path);
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

		public static async Task Run()
		{
			var all = new List<Tr>();
			foreach (var (symbol, dataSym) in new[] { ("SPY", "spx"), ("QQQ", "qqq"), ("IWM", "iwm"), ("GLD", "gld") })
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
				var cp = LoadCallPut(dataSym);
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

				double T = 1.0 / 252.0;
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
					double iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					all.Add(new Tr(symbol, dTr, (cr + po) / ml, x5,
						cp.TryGetValue(dSig, out double c) ? c : double.NaN));
				}
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			var worst = all.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.R)).First().Key;
			var exW = all.Where(x => Wk(x.D) != worst).ToList();

			Console.WriteLine($"\n===== SIZE ON 5m EXPOSURE (lower exposure -> bigger stake) =====");
			Console.WriteLine($"{all.Count} sessions, {all.Select(x => Wk(x.D)).Distinct().Count()} weeks. " +
				$"Every multiplier normalised to mean 1.0, so average risk is IDENTICAL to flat and any");
			Console.WriteLine($"difference is pure cov(stake, outcome) rather than leverage.");
			Console.WriteLine($"flat baseline: {100 * Risk * all.Average(x => x.R):+0.0000;-0.0000}%  |  " +
				$"W23 removed {100 * Risk * exW.Average(x => x.R):+0.0000;-0.0000}%");

			var rnd = new Random(20260815);
			void Test(string lbl, Func<Tr, double> rawMult, List<Tr>? universe = null)
			{
				var u = universe ?? exW;
				var live = u.Where(x => !double.IsNaN(rawMult(x))).ToList();
				if (live.Count < 20) { Console.WriteLine($"{lbl,-34}   too few"); return; }
				double mean = live.Average(rawMult);
				if (Math.Abs(mean) < 1e-9) return;
				double M(Tr x) => rawMult(x) / mean;                 // normalised: mean multiplier == 1
				var sized = live.Select(x => Risk * M(x) * x.R).ToList();
				var flat = live.Select(x => Risk * x.R).ToList();
				var diff = live.Select(x => Risk * (M(x) - 1.0) * x.R).ToList();
				double md = diff.Average();
				double sdd = Math.Sqrt(diff.Sum(z => (z - md) * (z - md)) / (diff.Count - 1));
				double ms = sized.Average();
				double sds = Math.Sqrt(sized.Sum(z => (z - ms) * (z - ms)) / (sized.Count - 1));
				double mf = flat.Average();
				double sdf = Math.Sqrt(flat.Sum(z => (z - mf) * (z - mf)) / (flat.Count - 1));
				// block bootstrap over weeks on the paired difference
				var wks = live.GroupBy(x => Wk(x.D)).ToList();
				int n = 0, le = 0;
				for (int it = 0; it < 3000; it++)
				{
					var samp = new List<Tr>(live.Count);
					for (int w = 0; w < wks.Count; w++) samp.AddRange(wks[rnd.Next(wks.Count)]);
					if (samp.Count < 10) continue;
					double mm = samp.Average(rawMult);
					if (Math.Abs(mm) < 1e-9) continue;
					n++;
					if (samp.Average(x => Risk * (rawMult(x) / mm - 1.0) * x.R) <= 0) le++;
				}
				Console.WriteLine($"{lbl,-34} {live.Count,4} {100 * md,9:+0.0000;-0.0000} {md / (sdd / Math.Sqrt(diff.Count)),7:+0.00;-0.00} " +
					$"{(sds > 0 ? ms / sds : 0),8:0.000} {(sdf > 0 ? mf / sdf : 0),8:0.000} " +
					$"{(n > 0 ? (double)le / n : 1),8:0.000} {M(live.OrderBy(x => x.Exp).First()),7:0.00} {M(live.OrderByDescending(x => x.Exp).First()),7:0.00}");
			}

			Console.WriteLine($"\n-- W23 REMOVED ({exW.Count} sessions) --");
			Console.WriteLine($"{"rule",-34} {"n",4} {"diff pp",9} {"t",7} {"sizedIR",8} {"flatIR",8} {"P(<=0)",8} {"mLo",7} {"mHi",7}");
			Test("linear: 2 - 5*exp, clamp[.5,2]", x => Math.Min(2.0, Math.Max(0.5, 2.0 - 5.0 * x.Exp)));
			Test("linear: 1 - 2*exp, clamp[.25,2]", x => Math.Min(2.0, Math.Max(0.25, 1.0 - 2.0 * x.Exp)));
			Test("inverse: 1/(exp+0.10)", x => 1.0 / (x.Exp + 0.10));
			Test("inverse: 1/(exp+0.05), clamp 4", x => Math.Min(4.0, 1.0 / (x.Exp + 0.05)));
			Test("step: exp<0.10 ? 1.5 : 0.5", x => x.Exp < 0.10 ? 1.5 : 0.5);
			Test("step: exp<0.20 ? 1.5 : 0.5", x => x.Exp < 0.20 ? 1.5 : 0.5);
			Test("INVERTED linear (sign control)", x => Math.Min(2.0, Math.Max(0.5, 5.0 * x.Exp)));
			Console.WriteLine("  -- for reference only, the gex multiplier that currently ships --");
			Test("gex: min(callPut, 2)", x => double.IsNaN(x.Cp) ? double.NaN : Math.Min(2.0, x.Cp));

			Console.WriteLine($"\n-- FULL SAMPLE ({all.Count} sessions), known to contain {worst} --");
			Console.WriteLine($"{"rule",-34} {"n",4} {"diff pp",9} {"t",7} {"sizedIR",8} {"flatIR",8} {"P(<=0)",8} {"mLo",7} {"mHi",7}");
			Test("linear: 2 - 5*exp, clamp[.5,2]", x => Math.Min(2.0, Math.Max(0.5, 2.0 - 5.0 * x.Exp)), all);
			Test("inverse: 1/(exp+0.10)", x => 1.0 / (x.Exp + 0.10), all);
			Test("step: exp<0.10 ? 1.5 : 0.5", x => x.Exp < 0.10 ? 1.5 : 0.5, all);
			Console.WriteLine("\nmLo/mHi = normalised multiplier at the lowest / highest exposure session");
		}
	}
}
