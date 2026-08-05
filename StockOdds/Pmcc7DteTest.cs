using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// 7-DTE call spread expressing the engine target, entered only when dealer gamma is positive.
	//     long  call at delta 0.50, 7 DTE
	//     short call at delta (0.50 - target), SAME expiry  ->  net delta at entry = target
	// held to expiry, non-overlapping (one position at a time), so ~36 trades/year.
	//
	// NOTE ON THE NAME: with both legs on the same expiry this is a VERTICAL call spread, not a diagonal -- a PMCC
	// is normally a long-dated LEAP against a short-dated call, and matching the DTEs collapses the calendar leg.
	// Running it exactly as specified, but that is why the theta profile below looks like a debit spread's.
	//
	// PREDICTION WORTH STATING BEFORE THE NUMBERS: this is a LONG-PREMIUM (debit) structure, the same family as
	// the 1-day call spread that lost 19.1%. And the earlier regime split showed the long call spread's WORST
	// bucket was the highest-gamma one (Sharpe -3.683), because high dealer gamma suppresses realised vol, which
	// is what a debit spread needs. So gating to GEX > 0 may select against this structure rather than for it --
	// the opposite of what the gate did for the credit spread. The ungated arm is carried to measure that.
	public static class Pmcc7DteTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double LongDelta = 0.50;
		public static int    DteBars = 7;              // 7 trading days to expiry, both legs
		public static int    YearsBack = 21;
		public static double[] CostsPctOfPremium = { 0.0, 2.0, 5.0 };

		private sealed record Trade(DateTime Entry, DateTime Exit, double Ret, double Debit, double Target,
			double StockRet, double Under, double Gex, bool HasGex, bool Degenerate);

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

			double T = DteBars / 252.0;
			var trades = new List<Trade>();
			int i0 = bars.FindIndex(b => hv.ContainsKey(b.Date) && posByDate.ContainsKey(b.Date.Date));
			if (i0 < 0) { Console.WriteLine("no usable bars"); return; }

			for (int i = Math.Max(i0, 1); i + DteBars < bars.Count; i += DteBars)   // non-overlapping
			{
				var dEntry = bars[i].Date;
				if (!hv.TryGetValue(dEntry, out double sig)) continue;
				if (!posByDate.TryGetValue(dEntry.Date, out double target)) continue;
				double S = bars[i].Close, ST = bars[i + DteBars].Close;
				if (S <= 0) continue;

				double under = (ST - S) / S;
				bool hasGex = gex.TryGetValue(dEntry.Date, out var g);
				double gv = hasGex ? g!.Gex : double.NaN;

				double ret = 0, debit = 0; bool degen = false;
				if (target > 1e-6)
				{
					double iv = sig * VolRiskPremium;
					double kLong = StrikeForCallDelta(S, iv, T, LongDelta);
					double shortDelta = LongDelta - target;
					degen = shortDelta <= 1e-4;

					double netD, prem, payoff;
					if (!degen)
					{
						double kShort = StrikeForCallDelta(S, iv, T, shortDelta);
						netD = LongDelta - shortDelta;                      // == target
						prem = Call(S, kLong, iv, T) - Call(S, kShort, iv, T);
						payoff = Math.Max(0, ST - kLong) - Math.Max(0, ST - kShort);
					}
					else
					{
						netD = LongDelta;
						prem = Call(S, kLong, iv, T);
						payoff = Math.Max(0, ST - kLong);
					}
					if (prem > 1e-9 && netD > 1e-9)
					{
						double qty = (1.0 / S) * (target / netD);
						debit = qty * prem;
						ret = qty * payoff - debit;
					}
				}
				trades.Add(new Trade(dEntry, bars[i + DteBars].Date, ret, debit, target, target * under, under, gv, hasGex, degen));
			}

			Console.WriteLine($"\n===== {symbol}: {DteBars}-DTE CALL SPREAD (long 0.50d / short 0.50-target, matched expiry) =====");
			Console.WriteLine($"{trades.Count} non-overlapping trades | {trades.First().Entry:yyyy-MM-dd} -> {trades.Last().Exit:yyyy-MM-dd} | " +
				$"IV = HV(60) x {VolRiskPremium:0.00}, T = {DteBars}/252");
			Console.WriteLine($"target > 0.50 (no short leg) on {100.0 * trades.Count(t => t.Degenerate) / trades.Count:0.0}% of trades | " +
				$"mean debit paid {100 * trades.Average(t => t.Debit):0.000}% of bankroll per trade");

			double perYear = 252.0 / DteBars;
			Console.WriteLine($"\n{"arm",28} {"trades",7} {"ret%",11} {"maxDD%",9} {"Sharpe",9} {"mean/trade%",12} {"win%",7} {"worst%",8}");
			void Show(string label, List<Trade> t, double costPct = 0)
			{
				if (t.Count < 5) { Console.WriteLine($"{label,28} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => x.Ret - x.Debit * costPct / 100.0).ToList();
				Console.WriteLine($"{label,28} {t.Count,7} {Cmp(r),11:0.0} {Dd(r),9:0.00} {Shp(r, perYear),9:0.000} " +
					$"{100 * r.Average(),12:+0.000;-0.000} {100.0 * r.Count(x => x > 0) / r.Count,7:0.0} {100 * r.Min(),8:0.00}");
			}

			var withGex = trades.Where(t => t.HasGex).ToList();
			Show("ALL trades (21y)", trades);
			Show("stock (shipped, same days)", trades.Select(t => t with { Ret = t.StockRet, Debit = 0 }).ToList());
			Show("buy & hold (same days)", trades.Select(t => t with { Ret = t.Under, Debit = 0 }).ToList());
			Console.WriteLine();
			Show("GEX era: ungated", withGex);
			Show("GEX era: GATE gex > 0", withGex.Where(t => t.Gex > 0).ToList());
			Show("GEX era: inverted gex < 0", withGex.Where(t => t.Gex < 0).ToList());
			Show("GEX era: stock", withGex.Select(t => t with { Ret = t.StockRet, Debit = 0 }).ToList());
			Console.WriteLine();
			foreach (double c in CostsPctOfPremium.Where(x => x > 0))
				Show($"gated @ {c:0}% of premium", withGex.Where(t => t.Gex > 0).ToList(), c);

			// gamma quartiles within the positive-gamma trades
			var pos = withGex.Where(t => t.Gex >= 0).Select(t => t.Gex).OrderBy(x => x).ToList();
			if (pos.Count > 40)
			{
				double q1 = pos[(int)(pos.Count * 0.25)], q2 = pos[(int)(pos.Count * 0.50)], q3 = pos[(int)(pos.Count * 0.75)];
				Console.WriteLine($"\n----- within positive gamma, by quartile -----");
				Console.WriteLine($"{"bucket",16} {"trades",7} {"ret%",11} {"Sharpe",9} {"mean/trade%",12} {"win%",7}");
				(string L, Func<double, bool> P)[] qs =
				{
					($"0..{q1/1e9:0.0}B", g => g >= 0 && g < q1),
					($"{q1/1e9:0.0}..{q2/1e9:0.0}B", g => g >= q1 && g < q2),
					($"{q2/1e9:0.0}..{q3/1e9:0.0}B", g => g >= q2 && g < q3),
					($">{q3/1e9:0.0}B", g => g >= q3),
				};
				foreach (var (L, P) in qs)
				{
					var t = withGex.Where(x => P(x.Gex)).ToList();
					if (t.Count < 10) { Console.WriteLine($"{L,16} {t.Count,7}  (too few)"); continue; }
					var r = t.Select(x => x.Ret).ToList();
					Console.WriteLine($"{L,16} {t.Count,7} {Cmp(r),11:0.0} {Shp(r, perYear),9:0.000} " +
						$"{100 * r.Average(),12:+0.000;-0.000} {100.0 * r.Count(x => x > 0) / r.Count,7:0.0}");
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
		private static double StrikeForCallDelta(double S, double iv, double T, double delta)
		{
			double lo = S * 0.3, hi = S * 3.0;
			for (int i = 0; i < 60; i++)
			{
				double mid = 0.5 * (lo + hi);
				if (CallDelta(S, mid, iv, T) > delta) lo = mid; else hi = mid;
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
