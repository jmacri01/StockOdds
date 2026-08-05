using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Put credit spread with a FIXED 0.15-delta protective wing.
	//     long  put at delta magnitude 0.15   (fixed wing)
	//     short put at delta magnitude (target + 0.15)  ->  net delta = target
	// Capped at 0.95 short delta; above that the quantity scales instead, since the engine target runs to 1.5.
	//
	// Contrast with the earlier version, which fixed the SHORT leg at 0.50 and floated the wing. Fixing the wing
	// instead means the spread WIDTH grows with target: a small target puts the short leg just above the wing
	// (narrow, small credit, small risk); a large target pushes the short leg deep ITM (wide, large credit, large
	// risk). So this variant deliberately couples position risk to signal strength.
	//
	// ENTRY/EXIT so that "0 DTE" is real rather than nominal: the engine target is known at the close of day t, so
	// the spread is opened at the OPEN of day t+1 and expires at a CLOSE N sessions later.
	//     N = 1 -> opened at the open, expires that same close  = genuine 0 DTE, NO overnight gap
	//     N = 7 -> 7 sessions, one overnight gap per session held
	// One trading session is 1/252 of a year, so T = N/252 in both cases.
	//
	// Credit is haircut across a range because the tail test showed the whole result turns on pricing accuracy,
	// and a flat-IV Gaussian cannot resolve that on its own.
	public static class PutSpreadWingTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double MaxShortDelta = 0.95;
		public static int    YearsBack = 21;
		public static double[] Haircuts = { 1.00, 0.80, 0.60 };

		private sealed record Tr(DateTime Entry, DateTime Exit, double Ret, double Credit, double Target,
			double Stock, double Under, double Gex, bool HasGex, bool Capped);

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

			foreach (int N in new[] { 1, 7 })
				Report(symbol, bars, gex, posByDate, hv, N);
		}

		private static void Report(string symbol, List<OhlcBar> bars, Dictionary<DateTime, GexDay> gex,
			Dictionary<DateTime, double> posByDate, Dictionary<DateTime, double> hv, int N)
		{
			double T = N / 252.0;
			var trades = new List<Tr>();

			// signal from the close of bar i, position opened at the OPEN of bar i+1, expiring at the CLOSE of i+N
			for (int i = 1; i + N < bars.Count; i += N)
			{
				var dSig = bars[i].Date;
				if (!hv.TryGetValue(dSig, out double sig)) continue;
				if (!posByDate.TryGetValue(dSig.Date, out double target)) continue;

				double S = bars[i + 1].Open;
				double ST = bars[i + N].Close;
				if (S <= 0 || ST <= 0) continue;
				double under = (ST - S) / S;
				bool hasGex = gex.TryGetValue(dSig.Date, out var g);
				double gv = hasGex ? g!.Gex : double.NaN;

				double ret = 0, credit = 0; bool capped = false;
				if (target > 1e-6)
				{
					double iv = sig * VolRiskPremium;
					double shortMag = target + WingDelta;
					if (shortMag > MaxShortDelta) { shortMag = MaxShortDelta; capped = true; }
					double netD = shortMag - WingDelta;
					if (netD > 1e-9)
					{
						double kShort = StrikeForPutDelta(S, iv, T, shortMag);
						double kLong = StrikeForPutDelta(S, iv, T, WingDelta);
						double cr = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
						double payoff = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
						if (cr > 1e-9)
						{
							double qty = (1.0 / S) * (target / netD);
							credit = qty * cr;
							ret = qty * (cr + payoff);
						}
					}
				}
				trades.Add(new Tr(bars[i + 1].Date, bars[i + N].Date, ret, credit, target, target * under, under, gv, hasGex, capped));
			}

			double perYear = 252.0 / N;
			var withGex = trades.Where(t => t.HasGex).ToList();

			Console.WriteLine($"\n===== {symbol}: PUT CREDIT SPREAD, {WingDelta:0.00}-DELTA WING, {(N == 1 ? "0 DTE (open->same close, no overnight)" : N + " sessions")} =====");
			Console.WriteLine($"short put at (target + {WingDelta:0.00})d capped at {MaxShortDelta:0.00} | {trades.Count} non-overlapping trades | " +
				$"{trades.First().Entry:yyyy-MM-dd} -> {trades.Last().Exit:yyyy-MM-dd}");
			Console.WriteLine($"short-delta cap hit on {100.0 * trades.Count(t => t.Capped) / trades.Count:0.0}% of trades | " +
				$"mean credit {100 * trades.Average(t => t.Credit):0.000}% of bankroll per trade | T = {N}/252");

			Console.WriteLine($"\n{"arm",30} {"trades",7} {"ret%",12} {"maxDD%",9} {"Sharpe",9} {"mean/tr%",10} {"win%",7} {"worst%",8}");
			void Show(string label, List<Tr> t, double h = 1.0)
			{
				if (t.Count < 5) { Console.WriteLine($"{label,30} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => x.Ret - x.Credit * (1 - h)).ToList();
				Console.WriteLine($"{label,30} {t.Count,7} {Cmp(r),12:0.0} {Dd(r),9:0.00} {Shp(r, perYear),9:0.000} " +
					$"{100 * r.Average(),10:+0.000;-0.000} {100.0 * r.Count(x => x > 0) / r.Count,7:0.0} {100 * r.Min(),8:0.00}");
			}

			foreach (double h in Haircuts) Show($"ALL 21y @ {h * 100:0}% credit", trades, h);
			Show("stock (shipped, same days)", trades.Select(t => t with { Ret = t.Stock, Credit = 0 }).ToList());
			Show("buy & hold (same days)", trades.Select(t => t with { Ret = t.Under, Credit = 0 }).ToList());
			Console.WriteLine();
			Show("GEX era: ungated", withGex);
			Show("GEX era: GATE gex > 0", withGex.Where(t => t.Gex > 0).ToList());
			Show("GEX era: gex < 0 only", withGex.Where(t => t.Gex < 0).ToList());
			Show("GEX era: gated @ 80% credit", withGex.Where(t => t.Gex > 0).ToList(), 0.80);

			(string L, DateTime A, DateTime B)[] windows =
			{
				("2008 GFC", new DateTime(2008, 8, 1), new DateTime(2009, 3, 31)),
				("2018 Feb VIX", new DateTime(2018, 1, 20), new DateTime(2018, 3, 1)),
				("2020 COVID", new DateTime(2020, 2, 15), new DateTime(2020, 4, 15)),
				("2022 bear", new DateTime(2022, 1, 1), new DateTime(2022, 12, 31)),
			};
			Console.WriteLine($"\n----- crash windows @ 100% credit -----");
			Console.WriteLine($"{"window",14} {"trades",7} {"credit ret%",12} {"stock ret%",11} {"B&H ret%",10} {"worst%",8}");
			foreach (var (L, A, B) in windows)
			{
				var w = trades.Where(t => t.Entry >= A && t.Exit <= B).ToList();
				if (w.Count < 3) { Console.WriteLine($"{L,14} {w.Count,7}  (too few)"); continue; }
				var r = w.Select(x => x.Ret).ToList();
				Console.WriteLine($"{L,14} {w.Count,7} {Cmp(r),12:0.0} {Cmp(w.Select(x => x.Stock).ToList()),11:0.0} " +
					$"{Cmp(w.Select(x => x.Under).ToList()),10:0.0} {100 * r.Min(),8:0.00}");
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
			double lo = S * 0.3, hi = S * 3.0;
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
		private static double Shp(List<double> r, double perYear)
		{
			if (r.Count < 2) return 0;
			double m = r.Average(), v = r.Sum(x => (x - m) * (x - m)) / (r.Count - 1), sd = Math.Sqrt(v);
			return sd > 0 ? m / sd * Math.Sqrt(perYear) : 0;
		}
	}
}
