using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// SHORT-PREMIUM MIRROR of the call-spread test: express the engine's target by SELLING a put spread.
	//     short put at delta magnitude 0.50
	//     long  put at delta magnitude (0.50 - target)     ->  net delta at entry = target
	//
	// This is a CREDIT structure, so the 1.10 vol risk premium works FOR the position rather than against it --
	// the reason the long call spread bled 0.245%/day. Same timing convention: the target is known at the close
	// of day t, so the spread is opened there and expires at the next close (T = 1/252).
	//
	// Tail risk IS the story for a credit spread, so worst single day is reported everywhere. When target > 0.50
	// the protective put cannot exist (its delta would be negative) and the structure degenerates to a NAKED
	// short put -- bounded by the strike but not by a spread width, and in reality it would need cash collateral
	// (see the cash-secured invariant in the overlay work). That share of days is reported.
	public static class PutCreditSpreadTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double ShortDelta = 0.50;
		public static double[] CostsPctOfPremium = { 0.0, 2.0, 5.0 };

		public static async Task Run(string symbol = "SPY")
		{
			var bars = (await YahooClient.GetBarsAsync(symbol, "1d")).Where(b => b.Date >= new DateTime(2020, 1, 1)).ToList();
			if (bars.Count < 300) { Console.WriteLine($"{symbol}: only {bars.Count} bars"); return; }

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

			var rows = new List<(DateTime D, double Ret, double Stock, double Under, double Credit, bool Naked)>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
			{
				var dEnd = eng.ReturnDates[k];
				int iEnd = bars.FindIndex(b => b.Date == dEnd);
				if (iEnd < 1) continue;
				var dStart = bars[iEnd - 1].Date;
				if (!closeBy.TryGetValue(dStart, out double S) || S <= 0) continue;
				if (!hv.TryGetValue(dStart, out double sig)) continue;

				double ST = bars[iEnd].Close, target = eng.Positions[k], under = (ST - S) / S;
				if (target <= 1e-6) { rows.Add((dEnd, 0, 0, under, 0, false)); continue; }

				double iv = sig * VolRiskPremium;
				double kShort = StrikeForPutDelta(S, iv, T, ShortDelta);
				double protDelta = ShortDelta - target;
				bool naked = protDelta <= 1e-4;

				double netD, credit, payoff;
				if (!naked)
				{
					double kLong = StrikeForPutDelta(S, iv, T, protDelta);
					netD = ShortDelta - protDelta;                       // == target
					credit = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
					payoff = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
				}
				else
				{
					netD = ShortDelta;
					credit = Put(S, kShort, iv, T);
					payoff = -Math.Max(0, kShort - ST);
				}
				if (credit <= 1e-9 || netD <= 1e-9) { rows.Add((dEnd, 0, 0, under, 0, naked)); continue; }

				double qty = (1.0 / S) * (target / netD);
				rows.Add((dEnd, qty * (credit + payoff), target * under, under, qty * credit, naked));
			}

			Console.WriteLine($"\n===== {symbol}: SHIPPED TARGET AS A 1-DAY PUT CREDIT SPREAD =====");
			Console.WriteLine($"short 0.50d put / long (0.50 - target)d put -> net delta = target");
			Console.WriteLine($"{rows.Count} days | {rows.First().D:yyyy-MM-dd} -> {rows.Last().D:yyyy-MM-dd} | IV = HV(60) x {VolRiskPremium:0.00}, T = 1/252");
			Console.WriteLine($"NAKED (target > 0.50, no protective put) on {100.0 * rows.Count(r => r.Naked) / rows.Count:0.0}% of days | " +
				$"mean credit {100 * rows.Average(r => r.Credit):0.000}% of bankroll/day");

			Console.WriteLine($"\n{"expression",26} {"ret%",11} {"maxDD%",9} {"Sharpe",8} {"mean/day%",10} {"worstDay%",10}");
			void Show(string l, List<double> r) => Console.WriteLine($"{l,26} {Cmp(r),11:0.0} {Dd(r),9:0.00} {Shp(r),8:0.000} " +
				$"{100 * r.Average(),10:+0.000;-0.000} {100 * r.Min(),10:0.00}");
			Show("1-day put credit spread", rows.Select(r => r.Ret).ToList());
			Show("stock (shipped)", rows.Select(r => r.Stock).ToList());
			Show("buy & hold", rows.Select(r => r.Under).ToList());
			foreach (double c in CostsPctOfPremium.Where(x => x > 0))
				Show($"credit @ {c:0}% of prem", rows.Select(r => r.Ret - r.Credit * c / 100.0).ToList());

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
				($"0..{q1 / 1e9:0.0}B", g => g >= 0 && g < q1),
				($"{q1 / 1e9:0.0}..{q2 / 1e9:0.0}B", g => g >= q1 && g < q2),
				($"{q2 / 1e9:0.0}..{q3 / 1e9:0.0}B", g => g >= q2 && g < q3),
				($">{q3 / 1e9:0.0}B", g => g >= q3),
			};

			Console.WriteLine($"\n===== BY DEALER-GAMMA REGIME (prior day's GEX) =====");
			Console.WriteLine($"{"regime(t-1)",13} {"days",6} {"credRet%",10} {"stockRet%",10} {"bhRet%",9} " +
				$"{"credShp",8} {"stockShp",9} {"bhShp",7} {"credDD%",8} {"worstDay%",10}");
			foreach (var (L, P) in buckets)
			{
				var idx = withGex.Where(i => P(gexList[i])).ToList();
				if (idx.Count < 20) { Console.WriteLine($"{L,13} {idx.Count,6}  (too few)"); continue; }
				var cr = idx.Select(i => rows[i].Ret).ToList();
				var stk = idx.Select(i => rows[i].Stock).ToList();
				var bh = idx.Select(i => rows[i].Under).ToList();
				Console.WriteLine($"{L,13} {idx.Count,6} {Cmp(cr),10:0.0} {Cmp(stk),10:0.0} {Cmp(bh),9:0.0} " +
					$"{Shp(cr),8:0.000} {Shp(stk),9:0.000} {Shp(bh),7:0.000} {Dd(cr),8:0.00} {100 * cr.Min(),10:0.00}");
			}
		}

		// THE CONTROL THAT DECIDES IT: sweep the vol risk premium. The overlay research established that the
		// options structure ranking is a monotone function of VRP and inverts below ~0.93 -- so a credit structure
		// scoring Sharpe 2.7 at VRP 1.10 may be reporting the assumption, not a market fact. If the edge survives
		// VRP = 1.00 (options priced at realised vol, no premium at all) it is a real structural edge; if it dies
		// there, the whole result is the input.
		public static async Task VrpSweep(string symbol = "SPY")
		{
			Console.WriteLine($"\n===== {symbol}: VRP SENSITIVITY OF THE PUT CREDIT SPREAD =====");
			Console.WriteLine($"{"VRP",6} {"ret%",11} {"maxDD%",9} {"Sharpe",9} {"mean/day%",10} {"worstDay%",10} {"negGexShp",10}");
			double saved = VolRiskPremium;
			try
			{
				foreach (double vrp in new[] { 0.85, 0.90, 0.95, 1.00, 1.05, 1.10, 1.20 })
				{
					VolRiskPremium = vrp;
					var (ret, negGex) = Measure(symbol);
					if (ret.Count == 0) continue;
					Console.WriteLine($"{vrp,6:0.00} {Cmp(ret),11:0.0} {Dd(ret),9:0.00} {Shp(ret),9:0.000} " +
						$"{100 * ret.Average(),10:+0.000;-0.000} {100 * ret.Min(),10:0.00} {Shp(negGex),10:0.000}");
				}
			}
			finally { VolRiskPremium = saved; }
		}

		private static (List<double> All, List<double> NegGex) MeasureCache = (new(), new());
		private static (List<double>, List<double>) Measure(string symbol)
		{
			var bars = YahooClient.GetBarsAsync(symbol, "1d").GetAwaiter().GetResult()
				.Where(b => b.Date >= new DateTime(2020, 1, 1)).ToList();
			var gex = GexClient.ByDateAsync().GetAwaiter().GetResult();
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
			var all = new List<double>(); var neg = new List<double>();

			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
			{
				var dEnd = eng.ReturnDates[k];
				int iEnd = bars.FindIndex(b => b.Date == dEnd);
				if (iEnd < 1) continue;
				var dStart = bars[iEnd - 1].Date;
				if (!closeBy.TryGetValue(dStart, out double S) || S <= 0) continue;
				if (!hv.TryGetValue(dStart, out double sig)) continue;
				double ST = bars[iEnd].Close, target = eng.Positions[k];
				double r = 0;
				if (target > 1e-6)
				{
					double iv = sig * VolRiskPremium;
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
					if (credit > 1e-9 && netD > 1e-9)
					{
						double qty = (1.0 / S) * (target / netD);
						r = qty * (credit + payoff);
					}
				}
				all.Add(r);
				var prior = bars[iEnd - 1].Date.Date;
				if (gex.TryGetValue(prior, out var g) && g.Gex < 0) neg.Add(r);
			}
			return (all, neg);
		}

		// ---- Black-Scholes, zero rate/carry ----
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
				if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid;   // magnitude rises with strike
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
