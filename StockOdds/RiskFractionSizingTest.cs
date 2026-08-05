using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Size the 0-DTE put credit spread as a FIXED PERCENTAGE OF ACCOUNT AT RISK instead of by delta.
	//
	// For a defined-risk spread held to expiry the worst case is exactly (width - credit), so sizing to risk A
	// means every trade returns A * R where R is the trade's R-multiple:
	//     R = (credit + payoff) / (width - credit)      in [-1, +credit/(width-credit)]
	// Max loss is therefore EXACTLY -A per trade -- no gap can exceed it, because it is 0 DTE and the long wing
	// caps the downside. The equity curve is a pure Kelly product over R, so there is a real growth optimum.
	//
	// WHAT CHANGES CONCEPTUALLY: under delta-matched sizing the engine's target set the POSITION SIZE. Under
	// risk-fraction sizing it only sets the SHORT STRIKE -- every trade risks the same amount whatever the signal
	// says. That is a materially different strategy, so the R-multiple is also broken out BY TARGET BUCKET to see
	// whether the signal still earns anything once size is decoupled from it.
	//
	// Gated on GEX > 0 throughout, matching the previous run.
	public static class RiskFractionSizingTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double MaxShortDelta = 0.95;
		public static int    YearsBack = 21;
		public static double[] RiskFractions = { 0.005, 0.01, 0.02, 0.03, 0.05, 0.075, 0.10, 0.15, 0.20, 0.25, 0.33, 0.50 };

		private sealed record Tr(DateTime D, double R, double Target, double Credit, double Width,
			double NetDelta, double Spot, double Gex, bool HasGex, bool Capped);

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
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0 || target <= 1e-6) continue;

				double iv = sig * VolRiskPremium;
				double shortMag = Math.Min(MaxShortDelta, target + WingDelta);
				bool capped = target + WingDelta > MaxShortDelta;
				double netD = shortMag - WingDelta;
				if (netD <= 1e-9) continue;

				double kShort = StrikeForPutDelta(S, iv, T, shortMag);
				double kLong = StrikeForPutDelta(S, iv, T, WingDelta);
				double width = kShort - kLong;
				double cr = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
				double risk = width - cr;
				if (cr <= 1e-9 || risk <= 1e-9) continue;

				double payoff = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
				bool hasGex = gex.TryGetValue(dSig.Date, out var g);
				tr.Add(new Tr(bars[i + 1].Date, (cr + payoff) / risk, target, cr, width, netD, S,
					hasGex ? g!.Gex : double.NaN, hasGex, capped));
			}

			var gated = tr.Where(t => t.HasGex && t.Gex > 0).ToList();

			Console.WriteLine($"\n===== {symbol}: RISK-FRACTION SIZING, 0-DTE PUT CREDIT SPREAD ({WingDelta:0.00} wing), GEX > 0 =====");
			Console.WriteLine($"{gated.Count} gated trades of {tr.Count} total | {gated.First().D:yyyy-MM-dd} -> {gated.Last().D:yyyy-MM-dd}");
			Console.WriteLine($"every trade risks exactly the chosen % of account; max loss per trade = that %, by construction");

			// R-multiple distribution -- the whole strategy in one line
			var R = gated.Select(t => t.R).ToList();
			double rewardRisk = gated.Average(t => t.Credit / (t.Width - t.Credit));
			Console.WriteLine($"\nR-multiple: mean {R.Average():+0.0000;-0.0000}  median {Median(R):+0.0000;-0.0000}  " +
				$"win {100.0 * R.Count(x => x > 0) / R.Count:0.0}%  best {R.Max():+0.000}  worst {R.Min():+0.000}");
			Console.WriteLine($"mean reward:risk per trade = credit/(width-credit) = {rewardRisk:0.000}" +
				$"   (so a full loss costs {1 / Math.Max(1e-9, rewardRisk):0.0}x a full win)");
			Console.WriteLine($"full-loss trades (R = -1): {R.Count(x => x <= -0.999)} ({100.0 * R.Count(x => x <= -0.999) / R.Count:0.00}%)  |  " +
				$"short-delta cap hit on {100.0 * gated.Count(t => t.Capped) / gated.Count:0.0}%");
			Console.WriteLine($"mean implied net delta at 1% risk: {100 * gated.Average(t => 0.01 * t.NetDelta * t.Spot / (t.Width - t.Credit)):0.00}% of account");

			Console.WriteLine($"\n{"risk/trade",11} {"mean ret/tr%",13} {"final%",16} {"maxDD%",9} {"worstTr%",9} {"impliedDelta%",14} {"verdict",11}");
			double bestLog = double.NegativeInfinity, bestA = 0;
			foreach (double A in RiskFractions)
			{
				var (final, dd, ruin, logW) = Curve(R, A);
				double impDelta = 100 * gated.Average(t => A * t.NetDelta * t.Spot / (t.Width - t.Credit));
				string verdict = ruin ? "RUIN" : dd > 90 ? "near-ruin" : "survives";
				if (!ruin && logW > bestLog) { bestLog = logW; bestA = A; }
				Console.WriteLine($"{100 * A,10:0.0}% {100 * A * R.Average(),13:+0.0000;-0.0000} {final,16:0.0} {dd,9:0.00} " +
					$"{100 * A * R.Min(),9:0.00} {impDelta,14:0.0} {verdict,11}");
			}
			if (bestA > 0)
			{
				var (f, d, _, _) = Curve(R, bestA);
				Console.WriteLine($"  growth-optimal risk per trade: {100 * bestA:0.0}%  (final {f:0.0}%, maxDD {d:0.0}%, " +
					$"mean {100 * bestA * R.Average():0.0000}%/trade)");
			}

			// does the SIGNAL still earn anything once size is decoupled from it?
			Console.WriteLine($"\n----- R by engine target bucket (size no longer depends on target) -----");
			Console.WriteLine($"{"target",12} {"trades",7} {"meanR",9} {"medianR",9} {"win%",7} {"rewardRisk",11}");
			(string L, Func<double, bool> P)[] tb =
			{
				("0.0-0.1", x => x < 0.10), ("0.1-0.25", x => x >= 0.10 && x < 0.25),
				("0.25-0.5", x => x >= 0.25 && x < 0.50), ("0.5-0.8", x => x >= 0.50 && x < 0.80),
				("0.8+", x => x >= 0.80),
			};
			foreach (var (L, P) in tb)
			{
				var g = gated.Where(t => P(t.Target)).ToList();
				if (g.Count < 20) { Console.WriteLine($"{L,12} {g.Count,7}  (too few)"); continue; }
				var rr = g.Select(t => t.R).ToList();
				Console.WriteLine($"{L,12} {g.Count,7} {rr.Average(),9:+0.0000;-0.0000} {Median(rr),9:+0.0000;-0.0000} " +
					$"{100.0 * rr.Count(x => x > 0) / rr.Count,7:0.0} {g.Average(t => t.Credit / (t.Width - t.Credit)),11:0.000}");
			}

			// credit haircut, at the growth-optimal and at a conservative size
			Console.WriteLine($"\n----- credit haircut sensitivity -----");
			Console.WriteLine($"{"haircut",9} {"meanR",9} {"win%",7} {"@2% risk final%",17} {"maxDD%",9} {"optimalRisk%",13}");
			foreach (double h in new[] { 1.00, 0.90, 0.80, 0.70 })
			{
				var rh = gated.Select(t =>
				{
					double c = t.Credit * h, risk = t.Width - c;
					double payoff = (t.R * (t.Width - t.Credit)) - t.Credit;   // recover payoff from R
					return risk > 1e-9 ? (c + payoff) / risk : 0;
				}).ToList();
				var (f2, d2, _, _) = Curve(rh, 0.02);
				double bl = double.NegativeInfinity, ba = 0;
				foreach (double A in RiskFractions)
				{
					var (_, _, ru, lw) = Curve(rh, A);
					if (!ru && lw > bl) { bl = lw; ba = A; }
				}
				Console.WriteLine($"{h * 100,8:0}% {rh.Average(),9:+0.0000;-0.0000} {100.0 * rh.Count(x => x > 0) / rh.Count,7:0.0} " +
					$"{f2,17:0.0} {d2,9:0.00} {100 * ba,13:0.0}");
			}
		}

		private static (double Final, double MaxDd, bool Ruin, double LogW) Curve(List<double> r, double A)
		{
			double e = 1, peak = 1, dd = 0; bool ruin = false;
			foreach (var x in r)
			{
				e *= 1 + A * x;
				if (e <= 1e-12) { ruin = true; e = 0; break; }
				if (e > peak) peak = e;
				double q = (peak - e) / peak * 100;
				if (q > dd) dd = q;
			}
			return ((e - 1) * 100, dd, ruin, ruin ? double.NegativeInfinity : Math.Log(Math.Max(1e-12, e)));
		}

		private static double Median(List<double> xs)
		{ var s = xs.OrderBy(x => x).ToList(); int m = s.Count / 2; return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0; }
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
