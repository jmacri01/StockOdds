using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Express the shipped engine's target exposure as a SHORT-DATED CALL SPREAD instead of stock:
	//     long  call at delta 0.50
	//     short call at delta (0.50 - target)      ->  net delta at entry = target
	// held to expiry, re-opened every day, bucketed by dealer-gamma regime.
	//
	// TIMING (matters, and is not strictly "0 DTE"): the engine's target is known at the CLOSE of day t and
	// governs the move into t+1. So the spread is bought at that close and expires at the next close -- one
	// trading day, T = 1/252. A true same-day 0DTE (open -> close) would need an intraday signal; the daily
	// engine cannot produce one without look-ahead.
	//
	// SIZING: quantity = (bankroll / S) * (target / netDeltaPerSpread), so initial net delta equals target x
	// account, matching the engine's own semantics. When target > 0.50 the short leg cannot exist (its delta
	// would be negative), so the structure degenerates to a long call at 0.50 delta and quantity scales up.
	// That is a real limitation of the expression, not a modelling shortcut -- it is reported.
	//
	// The point of the comparison: net delta matches the engine at ENTRY, but a call spread's payoff is capped
	// above the short strike and floored at the premium, so it cannot track a stock position. This measures how
	// much that convexity costs or earns, and whether it differs by gamma regime.
	public static class ZeroDteSpreadTest
	{
		public static double VolRiskPremium = 1.10;   // IV = trailing HV * this, same convention as the overlay
		public static int    HvWindow = 60;
		public static double[] CostsPctOfPremium = { 0.0, 2.0, 5.0 };
		public static double LongDelta = 0.50;

		public static async Task Run(string symbol = "SPY")
		{
			var bars = (await YahooClient.GetBarsAsync(symbol, "1d")).Where(b => b.Date >= new DateTime(2020, 1, 1)).ToList();
			if (bars.Count < 300) { Console.WriteLine($"{symbol}: only {bars.Count} bars"); return; }

			var gex = await GexClient.ByDateAsync();
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			// trailing HV as of each decision bar
			var hv = new Dictionary<DateTime, double>();
			for (int i = 1; i < bars.Count; i++)
			{
				int j0 = Math.Max(1, i - (HvWindow - 1));
				var lr = new List<double>();
				for (int j = j0; j <= i; j++) if (bars[j - 1].Close > 0 && bars[j].Close > 0) lr.Add(Math.Log(bars[j].Close / bars[j - 1].Close));
				if (lr.Count >= 10)
				{
					double m = lr.Average();
					hv[bars[i].Date] = Math.Max(0.05, Math.Sqrt(lr.Sum(x => (x - m) * (x - m)) / (lr.Count - 1)) * Math.Sqrt(252.0));
				}
			}

			var closeBy = bars.ToDictionary(b => b.Date, b => b.Close);
			double T = 1.0 / 252.0;

			// walk the engine's aligned series: position[k] applies to the move into ReturnDates[k]
			var rows = new List<(DateTime D, double Target, double SpreadRet, double StockRet, double Under, double Prem, bool Degenerate)>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
			{
				var dEnd = eng.ReturnDates[k];
				int iEnd = bars.FindIndex(b => b.Date == dEnd);
				if (iEnd < 1) continue;
				var dStart = bars[iEnd - 1].Date;
				if (!closeBy.TryGetValue(dStart, out double S) || S <= 0) continue;
				if (!hv.TryGetValue(dStart, out double sig)) continue;
				double ST = bars[iEnd].Close;
				double target = eng.Positions[k];
				double under = (ST - S) / S;
				if (target <= 1e-6) { rows.Add((dEnd, target, 0, 0, under, 0, false)); continue; }

				double iv = sig * VolRiskPremium;
				double kLong = StrikeForDelta(S, iv, T, LongDelta);
				double shortDelta = LongDelta - target;
				bool degenerate = shortDelta <= 1e-4;

				double netDeltaPerSpread, premPerSpread, payoffPerSpread;
				if (!degenerate)
				{
					double kShort = StrikeForDelta(S, iv, T, shortDelta);
					netDeltaPerSpread = LongDelta - shortDelta;                       // == target
					premPerSpread = Call(S, kLong, iv, T) - Call(S, kShort, iv, T);
					payoffPerSpread = Math.Max(0, ST - kLong) - Math.Max(0, ST - kShort);
				}
				else
				{
					netDeltaPerSpread = LongDelta;                                    // long call alone
					premPerSpread = Call(S, kLong, iv, T);
					payoffPerSpread = Math.Max(0, ST - kLong);
				}
				if (premPerSpread <= 1e-9 || netDeltaPerSpread <= 1e-9) { rows.Add((dEnd, target, 0, 0, under, 0, degenerate)); continue; }

				// account-scaled so initial net delta = target * account
				double qty = (1.0 / S) * (target / netDeltaPerSpread);
				double prem = qty * premPerSpread;                                    // fraction of bankroll paid
				double spreadRet = qty * payoffPerSpread - prem;                      // fraction of bankroll
				rows.Add((dEnd, target, spreadRet, target * under, under, prem, degenerate));
			}

			Console.WriteLine($"\n===== {symbol}: SHIPPED TARGET AS A 1-DAY CALL SPREAD (long 0.50d / short 0.50-target) =====");
			Console.WriteLine($"{rows.Count} days | {rows.First().D:yyyy-MM-dd} -> {rows.Last().D:yyyy-MM-dd} | IV = HV(60) x {VolRiskPremium:0.00}, T = 1/252");
			Console.WriteLine($"target > 0.50 on {100.0 * rows.Count(r => r.Degenerate) / rows.Count:0.0}% of days " +
				$"(short leg impossible -> long call only) | mean premium paid {100 * rows.Average(r => r.Prem):0.000}% of bankroll/day");

			Console.WriteLine($"\n{"expression",22} {"ret%",11} {"maxDD%",9} {"Sharpe",8} {"mean/day%",10}");
			Show("1-day call spread", rows.Select(r => r.SpreadRet).ToList());
			Show("stock (shipped)", rows.Select(r => r.StockRet).ToList());
			Show("buy & hold", rows.Select(r => r.Under).ToList());

			foreach (double c in CostsPctOfPremium.Where(x => x > 0))
				Show($"spread @ {c:0}% of prem", rows.Select(r => r.SpreadRet - r.Prem * c / 100.0).ToList());

			// ---- by dealer-gamma regime, using the PRIOR day's GEX (the tradeable version) ----
			var gexList = rows.Select(r =>
			{
				int i = bars.FindIndex(b => b.Date == r.D);
				var prior = i >= 1 ? bars[i - 1].Date.Date : r.D.Date;
				return gex.TryGetValue(prior, out var g) ? g.Gex : double.NaN;
			}).ToList();

			var withGex = Enumerable.Range(0, rows.Count).Where(i => !double.IsNaN(gexList[i])).ToList();
			var posG = withGex.Select(i => gexList[i]).Where(g => g >= 0).OrderBy(x => x).ToList();
			double q1 = posG[(int)(posG.Count * 0.25)], q2 = posG[(int)(posG.Count * 0.50)], q3 = posG[(int)(posG.Count * 0.75)];
			(string L, Func<double, bool> P)[] buckets =
			{
				("GEX < 0", g => g < 0),
				($"0..{q1/1e9:0.0}B", g => g >= 0 && g < q1),
				($"{q1/1e9:0.0}..{q2/1e9:0.0}B", g => g >= q1 && g < q2),
				($"{q2/1e9:0.0}..{q3/1e9:0.0}B", g => g >= q2 && g < q3),
				($">{q3/1e9:0.0}B", g => g >= q3),
			};

			Console.WriteLine($"\n===== BY DEALER-GAMMA REGIME (prior day's GEX) =====");
			Console.WriteLine($"{"regime(t-1)",13} {"days",6} {"sprdRet%",10} {"stockRet%",10} {"bhRet%",9} " +
				$"{"sprdShp",8} {"stockShp",9} {"bhShp",7} {"sprdMean%",10}");
			foreach (var (L, P) in buckets)
			{
				var idx = withGex.Where(i => P(gexList[i])).ToList();
				if (idx.Count < 20) { Console.WriteLine($"{L,13} {idx.Count,6}  (too few)"); continue; }
				var sp = idx.Select(i => rows[i].SpreadRet).ToList();
				var stk = idx.Select(i => rows[i].StockRet).ToList();
				var bh = idx.Select(i => rows[i].Under).ToList();
				Console.WriteLine($"{L,13} {idx.Count,6} {Cmp(sp),10:0.0} {Cmp(stk),10:0.0} {Cmp(bh),9:0.0} " +
					$"{Shp(sp),8:0.000} {Shp(stk),9:0.000} {Shp(bh),7:0.000} {100 * sp.Average(),10:+0.000;-0.000}");
			}

			void Show(string label, List<double> r) =>
				Console.WriteLine($"{label,22} {Cmp(r),11:0.0} {Dd(r),9:0.00} {Shp(r),8:0.000} {100 * r.Average(),10:+0.000;-0.000}");
		}

		// ---- Black-Scholes, zero rate/carry (consistent with the existing overlay model) ----
		private static double Nd(double x) => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));
		private static double Erf(double x)
		{
			double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
			double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
			return x >= 0 ? y : -y;
		}
		private static double Call(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return Math.Max(0, S - K);
			double v = iv * Math.Sqrt(T);
			double d1 = (Math.Log(S / K) + 0.5 * iv * iv * T) / v;
			return S * Nd(d1) - K * Nd(d1 - v);
		}
		private static double CallDelta(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S > K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd((Math.Log(S / K) + 0.5 * iv * iv * T) / v);
		}
		private static double StrikeForDelta(double S, double iv, double T, double delta)
		{
			double lo = S * 0.5, hi = S * 2.0;
			for (int i = 0; i < 60; i++)
			{
				double mid = 0.5 * (lo + hi);
				if (CallDelta(S, mid, iv, T) > delta) lo = mid; else hi = mid;   // delta falls as K rises
			}
			return 0.5 * (lo + hi);
		}

		private static double Cmp(List<double> r) { double e = 1; foreach (var x in r) e *= 1 + x; return (e - 1) * 100; }
		private static double Dd(List<double> r)
		{ double e = 1, p = 1, d = 0; foreach (var x in r) { e *= 1 + x; if (e > p) p = e; double q = (p - e) / p; if (q > d) d = q; } return d * 100; }
		private static double Shp(List<double> r)
		{
			if (r.Count < 2) return 0;
			double m = r.Average(), v = r.Sum(x => (x - m) * (x - m)) / (r.Count - 1), sd = Math.Sqrt(v);
			return sd > 0 ? m / sd * Math.Sqrt(252.0) : 0;
		}
	}
}
