using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Gate the 1-day put credit spread: don't sell premium when dealer gamma is negative.
	//
	// The obvious trap: GEX < 0 is exactly the bucket that lost, so removing it improves things BY CONSTRUCTION.
	// Three controls make the test mean something, all removing the SAME NUMBER of days so the comparison is
	// apples to apples:
	//   MATCHED PRIOR-RETURN GATE  skip the N worst prior-day returns. Negative-GEX days are mostly big down days,
	//                              so if price alone gates as well, GEX adds nothing.
	//   NULL GATE                  skip every k-th day (same count, no information). Shows what dropping 12.5% of
	//                              days does mechanically to a compounding series.
	//   INVERTED GATE              skip only POSITIVE-gamma days. If the GEX gate is real, its inverse must be bad.
	// VRP is swept on the gated version too, since the whole structure's level is VRP-dependent.
	public static class GatedCreditSpreadTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double ShortDelta = 0.50;

		private sealed record Day(DateTime D, double Ret, double Stock, double Under, double PriorGex, double PriorRet, bool HasGex);

		public static async Task Run(string symbol = "SPY")
		{
			var rows = await Build(symbol, VolRiskPremium);
			var usable = rows.Where(r => r.HasGex).ToList();
			int nNeg = usable.Count(r => r.PriorGex < 0);

			Console.WriteLine($"\n===== {symbol}: GATING THE 1-DAY PUT CREDIT SPREAD ON DEALER GAMMA =====");
			Console.WriteLine($"{usable.Count} days with a GEX reading | negative-gamma days {nNeg} ({100.0 * nNeg / usable.Count:0.0}%)");
			Console.WriteLine($"IV = HV(60) x {VolRiskPremium:0.00} | gated days sit in cash (return 0)\n");

			// matched prior-return threshold: exclude the nNeg worst prior-day returns
			double retThresh = usable.Select(r => r.PriorRet).OrderBy(x => x).Skip(Math.Max(0, nNeg - 1)).First();
			// null gate: every k-th day, same count
			int k = Math.Max(2, (int)Math.Round((double)usable.Count / nNeg));

			Console.WriteLine($"{"variant",30} {"days in",8} {"ret%",10} {"maxDD%",9} {"Sharpe",9} {"mean/day%",10} {"worst%",8}");
			void Show(string label, Func<Day, bool> keep)
			{
				var series = usable.Select(r => keep(r) ? r.Ret : 0.0).ToList();
				int inTrade = usable.Count(keep);
				Console.WriteLine($"{label,30} {inTrade,8} {Cmp(series),10:0.0} {Dd(series),9:0.00} {Shp(series),9:0.000} " +
					$"{100 * series.Average(),10:+0.000;-0.000} {100 * series.Min(),8:0.00}");
			}

			Show("ungated", _ => true);
			Show("GATE: skip GEX < 0", r => r.PriorGex >= 0);
			Show($"CTRL matched priorRet < {retThresh * 100:0.00}%", r => r.PriorRet >= retThresh);
			Show($"CTRL null gate (skip every {k}th)", r => usable.IndexOf(r) % k != 0);
			Show("CTRL inverted (skip GEX >= 0)", r => r.PriorGex < 0);
			Console.WriteLine();
			// reference rows without the gate machinery
			Console.WriteLine($"{"stock (shipped)",30} {usable.Count,8} {Cmp(usable.Select(r => r.Stock).ToList()),10:0.0} " +
				$"{Dd(usable.Select(r => r.Stock).ToList()),9:0.00} {Shp(usable.Select(r => r.Stock).ToList()),9:0.000} " +
				$"{100 * usable.Average(r => r.Stock),10:+0.000;-0.000} {100 * usable.Min(r => r.Stock),8:0.00}");
			Console.WriteLine($"{"buy & hold",30} {usable.Count,8} {Cmp(usable.Select(r => r.Under).ToList()),10:0.0} " +
				$"{Dd(usable.Select(r => r.Under).ToList()),9:0.00} {Shp(usable.Select(r => r.Under).ToList()),9:0.000} " +
				$"{100 * usable.Average(r => r.Under),10:+0.000;-0.000} {100 * usable.Min(r => r.Under),8:0.00}");

			// does the gate survive at fair vol?
			Console.WriteLine($"\n----- VRP sensitivity of the GATED version -----");
			Console.WriteLine($"{"VRP",6} {"ungatedShp",11} {"gatedShp",10} {"gatedRet%",11} {"gain",8}");
			foreach (double vrp in new[] { 0.85, 0.95, 1.00, 1.10, 1.20 })
			{
				var rr = (await Build(symbol, vrp)).Where(r => r.HasGex).ToList();
				var un = rr.Select(r => r.Ret).ToList();
				var ga = rr.Select(r => r.PriorGex >= 0 ? r.Ret : 0.0).ToList();
				Console.WriteLine($"{vrp,6:0.00} {Shp(un),11:0.000} {Shp(ga),10:0.000} {Cmp(ga),11:0.0} {Shp(ga) - Shp(un),8:+0.000;-0.000}");
			}
		}

		private static async Task<List<Day>> Build(string symbol, double vrp)
		{
			var bars = (await YahooClient.GetBarsAsync(symbol, "1d")).Where(b => b.Date >= new DateTime(2020, 1, 1)).ToList();
			var gex = await GexClient.ByDateAsync();
			var eng = BankrollSimulator.Run(bars, 10_000.0);

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
			var closeBy = bars.ToDictionary(b => b.Date, b => b.Close);
			double T = 1.0 / 252.0;
			var outp = new List<Day>();

			for (int idx = 0; idx < eng.Positions.Count && idx < eng.ReturnDates.Count; idx++)
			{
				var dEnd = eng.ReturnDates[idx];
				int iEnd = bars.FindIndex(b => b.Date == dEnd);
				if (iEnd < 2) continue;
				var dStart = bars[iEnd - 1].Date;
				if (!closeBy.TryGetValue(dStart, out double S) || S <= 0) continue;
				if (!hv.TryGetValue(dStart, out double sig)) continue;

				double ST = bars[iEnd].Close, target = eng.Positions[idx];
				double under = (ST - S) / S;
				double priorRet = bars[iEnd - 2].Close > 0 ? (S - bars[iEnd - 2].Close) / bars[iEnd - 2].Close : 0;
				bool hasGex = gex.TryGetValue(dStart.Date, out var g);
				double pg = hasGex ? g!.Gex : double.NaN;

				double ret = 0;
				if (target > 1e-6)
				{
					double iv = sig * vrp;
					double kShort = StrikeForPutDelta(S, iv, T, ShortDelta);
					double protDelta = ShortDelta - target;
					double netD, credit, payoff;
					if (protDelta > 1e-4)
					{
						double kLong = StrikeForPutDelta(S, iv, T, protDelta);
						netD = ShortDelta - protDelta;
						credit = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
						payoff = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
					}
					else
					{
						netD = ShortDelta;
						credit = Put(S, kShort, iv, T);
						payoff = -Math.Max(0, kShort - ST);
					}
					if (credit > 1e-9 && netD > 1e-9) ret = (1.0 / S) * (target / netD) * (credit + payoff);
				}
				outp.Add(new Day(dEnd, ret, target * under, under, pg, priorRet, hasGex));
			}
			return outp;
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
			double lo = S * 0.5, hi = S * 2.0;
			for (int i = 0; i < 60; i++)
			{
				double mid = 0.5 * (lo + hi);
				if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid;
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
