using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// TAIL TEST of the 1-day put credit spread expression of the engine target.
	//
	// The 2021-2026 result (Sharpe 2.7 ungated, 3.2 GEX-gated, worst day -2.0%) came from a window with NO crash.
	// This re-runs it over 21 years (2005+), which contains 2008, the 2010 flash crash, Aug 2015, Feb 2018,
	// March 2020 and Aug 2024 -- 18 days worse than -5% and a worst of -10.94%.
	//
	// Second stress, independent of the sample: the model prices with a GAUSSIAN, which understates the tails you
	// are SELLING, so the credit collected is overstated in exactly the states that hurt. There is no clean way to
	// fix that without a real options history, so the credit is HAIRCUT by a fixed fraction and the result is read
	// across the haircut. If the edge only exists at 100% of modelled credit it is a pricing artefact.
	//
	// The GEX gate can only be applied from 2011-05 (start of the series), so gated rows are reported over that
	// sub-window against their own ungated comparison.
	public static class CreditSpreadTailTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double ShortDelta = 0.50;
		public static int    YearsBack = 21;
		public static double[] CreditHaircuts = { 1.00, 0.80, 0.60, 0.40 };

		private sealed record Day(DateTime D, double Credit, double Payoff, double Stock, double Under, double PriorGex, bool HasGex);

		public static async Task Run(string symbol = "SPY")
		{
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", YearsBack);
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

			var rows = new List<Day>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
			{
				var dEnd = eng.ReturnDates[k];
				int iEnd = bars.FindIndex(b => b.Date == dEnd);
				if (iEnd < 1) continue;
				var dStart = bars[iEnd - 1].Date;
				if (!closeBy.TryGetValue(dStart, out double S) || S <= 0) continue;
				if (!hv.TryGetValue(dStart, out double sig)) continue;

				double ST = bars[iEnd].Close, target = eng.Positions[k], under = (ST - S) / S;
				bool hasGex = gex.TryGetValue(dStart.Date, out var g);
				double pg = hasGex ? g!.Gex : double.NaN;

				double credit = 0, payoff = 0;
				if (target > 1e-6)
				{
					double iv = sig * VolRiskPremium;
					double kShort = StrikeForPutDelta(S, iv, T, ShortDelta);
					double protDelta = ShortDelta - target;
					double netD, cr, pay;
					if (protDelta > 1e-4)
					{
						double kLong = StrikeForPutDelta(S, iv, T, protDelta);
						netD = ShortDelta - protDelta;
						cr = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
						pay = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
					}
					else
					{
						netD = ShortDelta;
						cr = Put(S, kShort, iv, T);
						pay = -Math.Max(0, kShort - ST);
					}
					if (cr > 1e-9 && netD > 1e-9)
					{
						double qty = (1.0 / S) * (target / netD);
						credit = qty * cr; payoff = qty * pay;
					}
				}
				rows.Add(new Day(dEnd, credit, payoff, target * under, under, pg, hasGex));
			}

			Console.WriteLine($"\n===== {symbol}: CREDIT-SPREAD TAIL TEST OVER {YearsBack} YEARS =====");
			Console.WriteLine($"{rows.Count} days | {rows.First().D:yyyy-MM-dd} -> {rows.Last().D:yyyy-MM-dd} | IV = HV(60) x {VolRiskPremium:0.00}");
			Console.WriteLine($"underlying days worse than -5%: {rows.Count(r => r.Under < -0.05)} | worst underlying day {100 * rows.Min(r => r.Under):0.00}%\n");

			Console.WriteLine($"{"credit haircut",16} {"ret%",12} {"maxDD%",9} {"Sharpe",9} {"mean/day%",10} {"worstDay%",10} {"days<-2%",9}");
			foreach (double h in CreditHaircuts)
			{
				var s = rows.Select(r => r.Credit * h + r.Payoff).ToList();
				Console.WriteLine($"{h * 100,15:0}% {Cmp(s),12:0.0} {Dd(s),9:0.00} {Shp(s),9:0.000} {100 * s.Average(),10:+0.000;-0.000} " +
					$"{100 * s.Min(),10:0.00} {s.Count(x => x < -0.02),9}");
			}
			var stock = rows.Select(r => r.Stock).ToList();
			var bh = rows.Select(r => r.Under).ToList();
			Console.WriteLine($"{"stock (shipped)",16} {Cmp(stock),12:0.0} {Dd(stock),9:0.00} {Shp(stock),9:0.000} " +
				$"{100 * stock.Average(),10:+0.000;-0.000} {100 * stock.Min(),10:0.00} {stock.Count(x => x < -0.02),9}");
			Console.WriteLine($"{"buy & hold",16} {Cmp(bh),12:0.0} {Dd(bh),9:0.00} {Shp(bh),9:0.000} " +
				$"{100 * bh.Average(),10:+0.000;-0.000} {100 * bh.Min(),10:0.00} {bh.Count(x => x < -0.02),9}");

			// ---- the crash windows, at full modelled credit ----
			(string L, DateTime A, DateTime B)[] windows =
			{
				("2008 GFC",      new DateTime(2008, 8, 1),  new DateTime(2009, 3, 31)),
				("2010 flash",    new DateTime(2010, 4, 15), new DateTime(2010, 7, 15)),
				("2011 debt",     new DateTime(2011, 7, 15), new DateTime(2011, 10, 15)),
				("2015 Aug",      new DateTime(2015, 8, 1),  new DateTime(2015, 10, 1)),
				("2018 Feb VIX",  new DateTime(2018, 1, 20), new DateTime(2018, 3, 1)),
				("2018 Q4",       new DateTime(2018, 10, 1), new DateTime(2019, 1, 15)),
				("2020 COVID",    new DateTime(2020, 2, 15), new DateTime(2020, 4, 15)),
				("2022 bear",     new DateTime(2022, 1, 1),  new DateTime(2022, 12, 31)),
				("2024 Aug",      new DateTime(2024, 7, 25), new DateTime(2024, 8, 20)),
			};
			Console.WriteLine($"\n----- crash windows (100% modelled credit) -----");
			Console.WriteLine($"{"window",14} {"days",6} {"credit ret%",12} {"stock ret%",11} {"B&H ret%",10} {"credWorst%",11} {"credDD%",9}");
			foreach (var (L, A, B) in windows)
			{
				var w = rows.Where(r => r.D >= A && r.D <= B).ToList();
				if (w.Count < 5) { Console.WriteLine($"{L,14} {w.Count,6}  (no data)"); continue; }
				var s = w.Select(r => r.Credit + r.Payoff).ToList();
				Console.WriteLine($"{L,14} {w.Count,6} {Cmp(s),12:0.0} {Cmp(w.Select(r => r.Stock).ToList()),11:0.0} " +
					$"{Cmp(w.Select(r => r.Under).ToList()),10:0.0} {100 * s.Min(),11:0.00} {Dd(s),9:0.00}");
			}

			// ---- GEX gate over the sub-window where GEX exists ----
			var withGex = rows.Where(r => r.HasGex).ToList();
			if (withGex.Count > 200)
			{
				Console.WriteLine($"\n----- GEX gate, {withGex.First().D:yyyy-MM-dd} -> {withGex.Last().D:yyyy-MM-dd} ({withGex.Count} days) -----");
				Console.WriteLine($"{"haircut",9} {"ungatedShp",11} {"gatedShp",10} {"ungatedDD%",11} {"gatedDD%",10} {"gatedWorst%",12}");
				foreach (double h in CreditHaircuts)
				{
					var un = withGex.Select(r => r.Credit * h + r.Payoff).ToList();
					var ga = withGex.Select(r => r.PriorGex >= 0 ? r.Credit * h + r.Payoff : 0.0).ToList();
					Console.WriteLine($"{h * 100,8:0}% {Shp(un),11:0.000} {Shp(ga),10:0.000} {Dd(un),11:0.00} {Dd(ga),10:0.00} {100 * ga.Min(),12:0.00}");
				}
			}
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
