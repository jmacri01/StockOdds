using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Every short/long delta pair for a BEAR CALL SPREAD, restricted to ST Bear + net gex < 0.
	//
	// Short the nearer call at delta S, long the further one at delta L < S. Net delta = -(S - L), bearish.
	// Risk = width - credit, exactly as for the put spread, so the sizing convention is identical.
	//
	// WHAT THE GRID CANNOT ESCAPE: on these sessions the underlying's mean move is +0.042% and only 46.0% close
	// down -- essentially the unconditional base rate. No choice of strikes creates a downward drift that is not
	// in the data; strikes only decide how the same distribution is sliced. So the prior is that the whole grid is
	// negative, and the interesting output is not "is any cell positive" but "is any cell positive by MORE than
	// the search itself would produce by chance".
	//
	// MULTIPLE COMPARISONS ARE THE POINT HERE. With ~20 cells over ~174 sessions, the maximum of 20 noisy
	// estimates is biased upward by roughly 2 standard errors even under a null of pure noise. The best cell's
	// t-statistic is therefore printed next to the bar it must clear, rather than left for the eye to judge.
	public static class BearCallGrid
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double Risk = 0.10;
		public static DateTime From = new DateTime(2022, 3, 30);
		public static double[] Shorts = { 0.20, 0.30, 0.40, 0.50, 0.60 };
		public static double[] Longs  = { 0.05, 0.10, 0.15, 0.20, 0.25 };

		private sealed record Day(DateTime D, double S, double ST, double Iv, double PutR, double Under);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(bars, 10_000.0);
			var ratio = LoadRatio();
			if (ratio.Count == 0) { Console.WriteLine("no UW gamma data"); return; }

			var stm = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < eng.StState.Count && k < eng.ReturnDates.Count; k++)
				stm[eng.ReturnDates[k].Date] = eng.StState[k];
			var hv = new Dictionary<DateTime, double>();
			for (int i = 1; i < bars.Count; i++)
			{
				int j0 = Math.Max(1, i - (HvWindow - 1));
				var lr = new List<double>();
				for (int j = j0; j <= i; j++)
					if (bars[j - 1].Close > 0 && bars[j].Close > 0) lr.Add(Math.Log(bars[j].Close / bars[j - 1].Close));
				if (lr.Count >= 10)
				{
					double m = lr.Average();
					hv[bars[i].Date.Date] = Math.Max(0.05, Math.Sqrt(lr.Sum(x => (x - m) * (x - m)) / (lr.Count - 1)) * Math.Sqrt(252.0));
				}
			}

			double T = 1.0 / 252.0;
			var days = new List<Day>();
			for (int i = 1; i + 1 < bars.Count; i++)
			{
				var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
				if (dTr < From) continue;
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				if (!ratio.TryGetValue(dSig, out double rt) || rt <= 1.0) continue;      // net gex < 0
				if (!stm.TryGetValue(dSig, out var st) || st != ShortTermState.Bear) continue;
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				double iv = h * VolRiskPremium;
				double pS = StrikeForPutDelta(S, iv, T, 0.35), pL = StrikeForPutDelta(S, iv, T, 0.15);
				double cr = Put(S, pS, iv, T) - Put(S, pL, iv, T);
				double ml = (pS - pL) - cr;
				double putR = (cr > 1e-9 && ml > 1e-9)
					? (cr + (-Math.Max(0, pS - ST) + Math.Max(0, pL - ST))) / ml : double.NaN;
				days.Add(new Day(dTr, S, ST, iv, putR, (ST - S) / S));
			}

			Console.WriteLine($"\n===== {symbol}: BEAR CALL SPREAD GRID -- ST Bear AND net gex < 0 =====");
			Console.WriteLine($"{days.Count} qualifying sessions {days.First().D:yyyy-MM} -> {days.Last().D:yyyy-MM} | " +
				$"underlying mean move {100 * days.Average(x => x.Under):+0.000;-0.000}%, " +
				$"down-days {100.0 * days.Count(x => x.Under < 0) / days.Count:0.0}%");
			var pv = days.Where(x => !double.IsNaN(x.PutR)).Select(x => Risk * x.PutR).ToList();
			double pm = pv.Average();
			double psd = Math.Sqrt(pv.Sum(z => (z - pm) * (z - pm)) / (pv.Count - 1));
			Console.WriteLine($"reference -- the SHIPPED PUT spread on these same days: {100 * pm:+0.0000;-0.0000}%/trade, " +
				$"IR {(psd > 0 ? pm / psd : 0):0.000}");

			(double s, double l, double ir, double mean, double t, int n) best = (0, 0, double.NegativeInfinity, 0, 0, 0);
			int cells = 0;
			Console.WriteLine($"\nmean/trade % at {100 * Risk:0.#}% risk  (rows = SHORT delta, cols = LONG delta)");
			Console.Write($"{"short\\long",12}");
			foreach (double l in Longs) Console.Write($" {l,9:0.00}");
			Console.WriteLine();
			foreach (double sh in Shorts)
			{
				Console.Write($"{sh,12:0.00}");
				foreach (double lg in Longs)
				{
					if (lg >= sh) { Console.Write($" {"-",9}"); continue; }
					var r = new List<double>();
					foreach (var d in days)
					{
						double kS = StrikeForCallDelta(d.S, d.Iv, T, sh);
						double kL = StrikeForCallDelta(d.S, d.Iv, T, lg);
						double cr = Call(d.S, kS, d.Iv, T) - Call(d.S, kL, d.Iv, T);
						double ml = (kL - kS) - cr;
						if (cr <= 1e-9 || ml <= 1e-9) continue;
						double po = -Math.Max(0, d.ST - kS) + Math.Max(0, d.ST - kL);
						r.Add(Risk * (cr + po) / ml);
					}
					if (r.Count < 30) { Console.Write($" {"(few)",9}"); continue; }
					cells++;
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					double ir = sd > 0 ? m / sd : 0;
					double t = sd > 0 ? m / (sd / Math.Sqrt(r.Count)) : 0;
					if (ir > best.ir) best = (sh, lg, ir, m, t, r.Count);
					Console.Write($" {100 * m,9:+0.0000;-0.0000}");
				}
				Console.WriteLine();
			}

			Console.WriteLine($"\nbest cell: short {best.s:0.00} / long {best.l:0.00}  ->  " +
				$"{100 * best.mean:+0.0000;-0.0000}%/trade, IR {best.ir:0.000}, n {best.n}, t = {best.t:+0.00;-0.00}");
			Console.WriteLine($"{cells} cells searched. Taking the MAX of that many noisy estimates inflates the winner by");
			Console.WriteLine($"roughly 2 standard errors even under pure noise, so the bar for the best cell is about");
			Console.WriteLine($"t > 3, not t > 2. The put-spread reference above is the number to beat, not zero.");
		}

		private static Dictionary<DateTime, double> LoadRatio()
		{
			var m = new Dictionary<DateTime, double>();
			string p = Path.Combine(Path.GetFullPath(Universe.DataDir), "gex_uw_spx.csv");
			if (!File.Exists(p)) return m;
			var lines = File.ReadAllLines(p);
			var h = lines[0].Split(',');
			int di = Array.IndexOf(h, "date"), ci = Array.IndexOf(h, "call_gex"), pi = Array.IndexOf(h, "put_gex");
			for (int i = 1; i < lines.Length; i++)
			{
				var f = lines[i].Split(',');
				if (f.Length <= Math.Max(ci, pi)) continue;
				if (DateTime.TryParse(f[di], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
					&& double.TryParse(f[ci], NumberStyles.Any, CultureInfo.InvariantCulture, out var cg)
					&& double.TryParse(f[pi], NumberStyles.Any, CultureInfo.InvariantCulture, out var pg)
					&& cg > 0)
					m[d.Date] = Math.Abs(pg) / cg;
			}
			return m;
		}

		private static double Nd(double x) => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));
		private static double Erf(double x)
		{
			double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
			double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
			return x >= 0 ? y : -y;
		}
		private static double Put(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return Math.Max(0, K - S);
			double v = iv * Math.Sqrt(T);
			double d1 = (Math.Log(S / K) + 0.5 * iv * iv * T) / v;
			return K * Nd(v - d1) - S * Nd(-d1);
		}
		private static double Call(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return Math.Max(0, S - K);
			double v = iv * Math.Sqrt(T);
			double d1 = (Math.Log(S / K) + 0.5 * iv * iv * T) / v;
			return S * Nd(d1) - K * Nd(d1 - v);
		}
		private static double PutDeltaMag(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S < K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd(-((Math.Log(S / K) + 0.5 * iv * iv * T) / v));
		}
		private static double CallDeltaOf(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S > K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd((Math.Log(S / K) + 0.5 * iv * iv * T) / v);
		}
		private static double StrikeForPutDelta(double S, double iv, double T, double mag)
		{
			double lo = S * 0.05, hi = S * 3.0;
			for (int i = 0; i < 80; i++) { double mid = 0.5 * (lo + hi); if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
		}
		private static double StrikeForCallDelta(double S, double iv, double T, double d)
		{
			double lo = S * 0.05, hi = S * 3.0;
			for (int i = 0; i < 80; i++) { double mid = 0.5 * (lo + hi); if (CallDeltaOf(S, mid, iv, T) > d) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
		}
	}
}
