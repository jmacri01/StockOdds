using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// How much of the account can the 0-DTE put credit spread carry before it blows up?
	//
	// Structure fixed: long put at 0.15 delta, short put at (target + 0.15) delta capped at 0.95, opened at the
	// OPEN and expiring at that same CLOSE (no overnight gap), non-overlapping, 21 years of SPY.
	//
	// Two distinct limits, and they are not the same number:
	//   MARGIN LIMIT  a defined-risk credit spread ties up (width - credit) per unit. Once that exceeds the
	//                 account the position simply cannot be funded, regardless of whether it would have survived.
	//   RUIN LIMIT    equity <= 0. With compounding this needs either one trade worse than -100% or a losing
	//                 streak that grinds the account out.
	// Between them sits the GROWTH-OPTIMAL allocation (max terminal log wealth), which is the number actually
	// worth knowing. Reported alongside the drawdown you have to sit through to get it.
	//
	// Swept at three credit levels, because leverage and pricing fragility multiply: at a credit level where the
	// edge is negative, EVERY allocation is ruinous and the optimum is zero.
	public static class AllocationRuinTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double MaxShortDelta = 0.95;
		public static int    YearsBack = 21;
		public static double[] Leverages = { 0.5, 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 25, 30, 40 };

		private sealed record Tr(DateTime D, double Ret, double Credit, double MaxLoss, double Gex, bool HasGex);

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
				double netD = shortMag - WingDelta;
				if (netD <= 1e-9) continue;

				double kShort = StrikeForPutDelta(S, iv, T, shortMag);
				double kLong = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
				if (cr <= 1e-9) continue;

				double qty = (1.0 / S) * (target / netD);
				double width = kShort - kLong;
				bool hasGex = gex.TryGetValue(dSig.Date, out var g);
				tr.Add(new Tr(bars[i + 1].Date,
					qty * (cr + (-Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST))),
					qty * cr,
					qty * Math.Max(0, width - cr),          // capital tied up / worst case per unit of bankroll
					hasGex ? g!.Gex : double.NaN, hasGex));
			}

			Console.WriteLine($"\n===== {symbol}: ALLOCATION AND RUIN, 0-DTE PUT CREDIT SPREAD ({WingDelta:0.00} wing) =====");
			Console.WriteLine($"{tr.Count} trades | {tr.First().D:yyyy-MM-dd} -> {tr.Last().D:yyyy-MM-dd} | non-overlapping, no overnight");
			Console.WriteLine($"at 1x delta-matched sizing: mean capital at risk {100 * tr.Average(t => t.MaxLoss):0.00}% of account, " +
				$"max {100 * tr.Max(t => t.MaxLoss):0.00}%");
			Console.WriteLine($"worst single trade at 1x: {100 * tr.Min(t => t.Ret):0.00}% | mean credit {100 * tr.Average(t => t.Credit):0.000}%");

			// PRIMARY: gated on GEX > 0
			var gatedTr = tr.Where(t => t.HasGex && t.Gex > 0).ToList();
			Console.WriteLine($"\nGATED on GEX > 0: {gatedTr.Count} of {tr.Count} trades ({tr.Count(t => t.HasGex)} in the GEX era)");
			Console.WriteLine($"gated: mean capital at risk at 1x {100 * gatedTr.Average(t => t.MaxLoss):0.00}%, max {100 * gatedTr.Max(t => t.MaxLoss):0.00}%, worst trade {100 * gatedTr.Min(t => t.Ret):0.00}%");

			foreach (double h in new[] { 1.00, 0.90, 0.80 })
			{
				var r = gatedTr.Select(t => t.Ret - t.Credit * (1 - h)).ToList();
				var risk = gatedTr.Select(t => t.MaxLoss).ToList();
				Console.WriteLine($"\n----- credit at {h * 100:0}% of model  (edge {100 * r.Average():+0.0000;-0.0000}%/trade) -----");
				Console.WriteLine($"{"lev",5} {"meanRisk%",10} {"maxRisk%",9} {"final%",14} {"maxDD%",9} {"worstTr%",9} {"minEquity",10} {"verdict",12}");
				double bestLog = double.NegativeInfinity, bestLev = 0;
				foreach (double L in Leverages)
				{
					var (final, dd, minEq, ruin, logW) = Curve(r, L);
					double meanRisk = 100 * L * risk.Average(), maxRisk = 100 * L * risk.Max();
					string verdict = ruin ? "RUIN" : maxRisk > 100 ? "unfundable" : dd > 90 ? "near-ruin" : "survives";
					if (!ruin && maxRisk <= 100 && logW > bestLog) { bestLog = logW; bestLev = L; }
					Console.WriteLine($"{L,5:0.#} {meanRisk,10:0.0} {maxRisk,9:0.0} {final,14:0.0} {dd,9:0.00} " +
						$"{100 * L * r.Min(),9:0.00} {minEq,10:0.000} {verdict,12}");
				}
				if (bestLev > 0)
				{
					var (f, d, _, _, _) = Curve(r, bestLev);
					Console.WriteLine($"  growth-optimal fundable leverage: {bestLev:0.#}x  " +
						$"(mean risk {100 * bestLev * risk.Average():0.0}% of account, final {f:0.0}%, maxDD {d:0.0}%)");
				}
				else Console.WriteLine("  no fundable leverage produces growth -- the edge is non-positive at this credit level");
			}

			// reference: same window, no gate
			var gated = tr.Where(t => t.HasGex).ToList();
			if (gated.Count > 200)
			{
				Console.WriteLine($"\n----- GEX > 0 gated ({gated.Count} trades), credit at 100% -----");
				var rg = gated.Select(t => t.Ret).ToList();
				var riskg = gated.Select(t => t.MaxLoss).ToList();
				Console.WriteLine($"{"lev",5} {"meanRisk%",10} {"maxRisk%",9} {"final%",14} {"maxDD%",9} {"worstTr%",9} {"verdict",12}");
				double bestLog = double.NegativeInfinity, bestLev = 0;
				foreach (double L in Leverages)
				{
					var (final, dd, minEq, ruin, logW) = Curve(rg, L);
					double maxRisk = 100 * L * riskg.Max();
					string verdict = ruin ? "RUIN" : maxRisk > 100 ? "unfundable" : dd > 90 ? "near-ruin" : "survives";
					if (!ruin && maxRisk <= 100 && logW > bestLog) { bestLog = logW; bestLev = L; }
					Console.WriteLine($"{L,5:0.#} {100 * L * riskg.Average(),10:0.0} {maxRisk,9:0.0} {final,14:0.0} {dd,9:0.00} " +
						$"{100 * L * rg.Min(),9:0.00} {verdict,12}");
				}
				if (bestLev > 0) Console.WriteLine($"  growth-optimal fundable leverage (ungated): {bestLev:0.#}x");
			}
		}

		private static (double Final, double MaxDd, double MinEq, bool Ruin, double LogW) Curve(List<double> r, double L)
		{
			double e = 1, peak = 1, dd = 0, minEq = 1;
			bool ruin = false;
			foreach (var x in r)
			{
				e *= 1 + L * x;
				if (e <= 0) { ruin = true; e = 0; break; }
				if (e > peak) peak = e;
				double q = (peak - e) / peak * 100;
				if (q > dd) dd = q;
				if (e < minEq) minEq = e;
			}
			return ((e - 1) * 100, dd, minEq, ruin, ruin ? double.NegativeInfinity : Math.Log(Math.Max(1e-12, e)));
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
