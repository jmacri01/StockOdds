using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Kelly sizing per engine-target bucket for the 0-DTE put credit spread (0.15 wing, GEX > 0).
	//
	// Under risk-fraction sizing every trade returns f * R, so the growth-optimal f for a bucket is just Kelly on
	// that bucket's R distribution. Two estimates are reported because they disagree in an informative way:
	//   BINARY KELLY   collapses the bucket to "win +b with prob p, lose -a with prob 1-p":  f = (p*b - q*a)/(a*b).
	//                  This is the "probability and expected return" framing, and it OVERSTATES f because it
	//                  discards the shape of the loss tail.
	//   NUMERIC KELLY  argmax over f of sum log(1 + f*R) on the empirical R's. Uses the whole distribution and is
	//                  the number to trust of the two.
	//
	// THREE THINGS THAT MAKE FULL KELLY UNUSABLE HERE, all reported alongside:
	//   1. Kelly is an IN-SAMPLE optimum on a fat-left-tail distribution; the observed 7% full-loss rate is an
	//      estimate, and Kelly is extremely sensitive to it. Half-Kelly is shown throughout.
	//   2. Kelly on R is blind to DELTA. Risking 1% carries ~48% net delta, so f = 20% implies ~960% delta --
	//      unfundable regardless of what the growth calculus says. A delta-capped variant is included.
	//   3. The 0.8+ bucket has ~49 trades. Kelly is not estimable there and its number should be ignored.
	//
	// The decision-relevant output is the last table: does per-bucket sizing actually beat one flat fraction?
	public static class KellyByTargetTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double MaxShortDelta = 0.95;
		public static int    YearsBack = 21;
		public static double DeltaCapPctOfAccount = 150.0;   // fundability ceiling on implied net delta

		private sealed record Tr(DateTime D, double R, double Target, double DeltaPerUnitRisk);

		public static async Task Run(string symbol = "SPY")
		{
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", YearsBack);
			var gex = await GexClient.ByDateAsync();
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			var posByDate = new Dictionary<DateTime, double>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
				posByDate[eng.ReturnDates[k].Date] = eng.Positions[k];

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
					hv[bars[i].Date] = Math.Max(0.05, Math.Sqrt(lr.Sum(x => (x - m) * (x - m)) / (lr.Count - 1)) * Math.Sqrt(252.0));
				}
			}

			double T = 1.0 / 252.0;
			var tr = new List<Tr>();
			for (int i = 1; i + 1 < bars.Count; i++)
			{
				var dSig = bars[i].Date;
				if (!hv.TryGetValue(dSig, out double sig)) continue;
				if (!posByDate.TryGetValue(dSig.Date, out double target)) continue;
				if (!gex.TryGetValue(dSig.Date, out var g) || g.Gex <= 0) continue;      // GEX > 0 gate
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0 || target <= 1e-6) continue;

				double iv = sig * VolRiskPremium;
				double shortMag = Math.Min(MaxShortDelta, target + WingDelta);
				double netD = shortMag - WingDelta;
				if (netD <= 1e-9) continue;
				double kShort = StrikeForPutDelta(S, iv, T, shortMag);
				double kLong = StrikeForPutDelta(S, iv, T, WingDelta);
				double width = kShort - kLong;
				double cr = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
				double risk = width - cr;
				if (cr <= 1e-9 || risk <= 1e-9) continue;
				double payoff = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
				tr.Add(new Tr(bars[i + 1].Date, (cr + payoff) / risk, target, netD * S / risk));
			}

			(string L, Func<double, bool> P)[] buckets =
			{
				("0.00-0.10", x => x < 0.10), ("0.10-0.25", x => x >= 0.10 && x < 0.25),
				("0.25-0.50", x => x >= 0.25 && x < 0.50), ("0.50-0.80", x => x >= 0.50 && x < 0.80),
				("0.80+", x => x >= 0.80),
			};

			Console.WriteLine($"\n===== {symbol}: KELLY SIZING BY ENGINE-TARGET BUCKET (0-DTE, {WingDelta:0.00} wing, GEX > 0) =====");
			Console.WriteLine($"{tr.Count} trades | {tr.First().D:yyyy-MM-dd} -> {tr.Last().D:yyyy-MM-dd}");
			Console.WriteLine($"\n{"target",11} {"n",6} {"p(win)",8} {"avgWin",8} {"avgLoss",8} {"E[R]",9} " +
				$"{"binKelly",9} {"numKelly",9} {"halfK",7} {"deltaCap",9} {"used",7}");

			var kelly = new Dictionary<string, double>();
			foreach (var (L, P) in buckets)
			{
				var g = tr.Where(t => P(t.Target)).ToList();
				if (g.Count < 10) { Console.WriteLine($"{L,11} {g.Count,6}  (not estimable)"); kelly[L] = 0; continue; }
				var R = g.Select(t => t.R).ToList();
				double p = (double)R.Count(x => x > 0) / R.Count;
				double avgWin = R.Where(x => x > 0).DefaultIfEmpty(0).Average();
				double avgLoss = -R.Where(x => x <= 0).DefaultIfEmpty(0).Average();     // positive magnitude
				double eR = R.Average();
				double binK = (avgLoss > 1e-9 && avgWin > 1e-9) ? (p * avgWin - (1 - p) * avgLoss) / (avgLoss * avgWin) : 0;
				double numK = NumericKelly(R);
				double half = numK / 2.0;
				// fundability: implied delta at fraction f is f * meanDeltaPerUnitRisk
				double dpr = g.Average(t => t.DeltaPerUnitRisk);
				double capF = DeltaCapPctOfAccount / 100.0 / Math.Max(1e-9, dpr);
				double used = Math.Min(half, capF);
				kelly[L] = used;
				Console.WriteLine($"{L,11} {g.Count,6} {100 * p,7:0.0}% {avgWin,8:0.000} {avgLoss,8:0.000} {eR,9:+0.0000;-0.0000} " +
					$"{100 * binK,8:0.0}% {100 * numK,8:0.0}% {100 * half,6:0.0}% {100 * capF,8:0.0}% {100 * used,6:0.0}%");
			}

			var allR = tr.Select(t => t.R).ToList();
			double poolNum = NumericKelly(allR);
			double poolDpr = tr.Average(t => t.DeltaPerUnitRisk);
			double poolCap = DeltaCapPctOfAccount / 100.0 / poolDpr;
			double poolUsed = Math.Min(poolNum / 2, poolCap);
			Console.WriteLine($"{"POOLED",11} {tr.Count,6} {100.0 * allR.Count(x => x > 0) / allR.Count,7:0.0}% " +
				$"{allR.Where(x => x > 0).Average(),8:0.000} {-allR.Where(x => x <= 0).Average(),8:0.000} {allR.Average(),9:+0.0000} " +
				$"{"",9} {100 * poolNum,8:0.0}% {100 * poolNum / 2,6:0.0}% {100 * poolCap,8:0.0}% {100 * poolUsed,6:0.0}%");
			Console.WriteLine($"\nimplied net delta per 1% risked: {100 * poolDpr / 100:0.0}% of account " +
				$"-> the {DeltaCapPctOfAccount:0}% delta cap binds at {100 * poolCap:0.0}% risk per trade");

			// ---- does bucket-conditioned sizing beat one flat fraction? ----
			Console.WriteLine($"\n----- sizing rules on the SAME trade sequence -----");
			Console.WriteLine($"{"rule",34} {"final%",16} {"maxDD%",9} {"mean/tr%",10} {"worstTr%",9} {"meanDelta%",11}");
			void Score(string label, Func<Tr, double> f)
			{
				double e = 1, peak = 1, dd = 0; var rets = new List<double>(); var deltas = new List<double>();
				foreach (var t in tr)
				{
					double frac = f(t);
					double x = frac * t.R;
					rets.Add(x); deltas.Add(frac * t.DeltaPerUnitRisk);
					e *= 1 + x;
					if (e <= 1e-12) { e = 0; break; }
					if (e > peak) peak = e;
					double q = (peak - e) / peak * 100; if (q > dd) dd = q;
				}
				Console.WriteLine($"{label,34} {(e - 1) * 100,16:0.0} {dd,9:0.00} {100 * rets.Average(),10:+0.0000;-0.0000} " +
					$"{100 * rets.Min(),9:0.00} {100 * deltas.Average(),11:0.0}");
			}

			string BucketOf(double x) => buckets.First(b => b.P(x)).L;
			Score("flat 2%", _ => 0.02);
			Score($"flat pooled half-Kelly ({100 * poolNum / 2:0.0}%)", _ => poolNum / 2);
			Score($"flat pooled, delta-capped ({100 * poolUsed:0.0}%)", _ => poolUsed);
			Score("per-bucket half-Kelly, delta-capped", t => kelly[BucketOf(t.Target)]);
			Score("per-bucket, quarter-Kelly capped", t => kelly[BucketOf(t.Target)] / 2);
			Score("per-bucket, skip target > 0.5", t => t.Target >= 0.50 ? 0 : kelly[BucketOf(t.Target)]);

			// ---- how stable are the Kelly fractions under a credit haircut? ----
			Console.WriteLine($"\n----- Kelly fraction by bucket under credit haircut (numeric, full Kelly) -----");
			Console.WriteLine($"{"target",11} {"100%",8} {"90%",8} {"80%",8} {"70%",8}");
			foreach (var (L, P) in buckets)
			{
				var g = tr.Where(t => P(t.Target)).ToList();
				if (g.Count < 10) continue;
				var cells = new List<string>();
				foreach (double h in new[] { 1.00, 0.90, 0.80, 0.70 })
				{
					// R scales as credit changes: R = (c + payoff)/(width - c); recover payoff from the stored R
					var rh = g.Select(t => t.R * h).ToList();   // first-order: credit and net both scale ~linearly
					cells.Add($"{100 * NumericKelly(rh),7:0.0}%");
				}
				Console.WriteLine($"{L,11} {string.Join(" ", cells)}");
			}
		}

		// argmax_f sum log(1 + f R), f in [0, 1); golden-section on a concave objective
		private static double NumericKelly(List<double> R)
		{
			double worst = R.Min();
			double hi = worst < -1e-9 ? Math.Min(0.999, 0.999 / -worst) : 0.999;
			double lo = 0.0;
			Func<double, double> obj = f => R.Sum(x => Math.Log(Math.Max(1e-12, 1 + f * x)));
			for (int i = 0; i < 200; i++)
			{
				double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
				if (obj(m1) < obj(m2)) lo = m1; else hi = m2;
			}
			double f0 = 0.5 * (lo + hi);
			return obj(f0) > obj(0) ? f0 : 0.0;
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
		private static double PutDeltaMag(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S < K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd(-((Math.Log(S / K) + 0.5 * iv * iv * T) / v));
		}
		private static double StrikeForPutDelta(double S, double iv, double T, double mag)
		{
			double lo = S * 0.3, hi = S * 3.0;
			for (int i = 0; i < 60; i++)
			{
				double mid = 0.5 * (lo + hi);
				if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
		}
	}
}
