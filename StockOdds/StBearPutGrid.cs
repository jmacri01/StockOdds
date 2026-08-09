using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ST Bear is currently SKIPPED because the shipped 0.35/0.15 put spread earned almost nothing there (IR ~0.11)
	// while carrying heavy drawdown. That is a verdict on ONE strike pair, not on the state. This sweeps the whole
	// short/long delta grid on ST Bear sessions to ask whether any pair makes the state worth trading again.
	//
	// THE BAR IS NOT ZERO. A cell that is merely positive still hurts if it is worse than the book it joins:
	// adding low-quality trades dilutes risk-adjusted return even while adding compounding. So the grid is scored
	// two ways -- ST Bear alone, and then the FULL BOOK with ST Bear re-admitted at its own best strikes, against
	// the shipped book that skips it. Only the second answers the question that matters.
	//
	// Full 21-year history rather than the gamma-data window, because this is a question about the state machine,
	// not about gamma, and the extra years are the only defence against reading one regime.
	public static class StBearPutGrid
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static double ShipShort = 0.35, ShipLong = 0.15;
		public static double[] Shorts = { 0.20, 0.25, 0.30, 0.35, 0.40, 0.50 };
		public static double[] Longs  = { 0.03, 0.05, 0.10, 0.15, 0.20 };

		private sealed record Day(DateTime D, double S, double ST, double Iv, bool IsBear);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			var pos = new Dictionary<DateTime, double>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
				pos[eng.ReturnDates[k].Date] = eng.Positions[k];
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
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				stm.TryGetValue(dSig, out var st);
				days.Add(new Day(dTr, S, ST, h * VolRiskPremium, st == ShortTermState.Bear));
			}

			// P&L per unit of max loss for one strike pair on one day; NaN when the pair is unusable
			double R(Day d, double sh, double lg)
			{
				double kS = StrikeForPutDelta(d.S, d.Iv, T, sh);
				double kL = StrikeForPutDelta(d.S, d.Iv, T, lg);
				double cr = Put(d.S, kS, d.Iv, T) - Put(d.S, kL, d.Iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) return double.NaN;
				return (cr + (-Math.Max(0, kS - d.ST) + Math.Max(0, kL - d.ST))) / ml;
			}
			(double m, double ir, double dd, double t, int n) Stat(IEnumerable<double> src)
			{
				var r = src.Where(x => !double.IsNaN(x)).Select(x => Risk * x).ToList();
				if (r.Count < 30) return (0, double.NaN, 0, 0, r.Count);
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				return (m, sd > 0 ? m / sd : 0, dd, sd > 0 ? m / (sd / Math.Sqrt(r.Count)) : 0, r.Count);
			}

			var bear = days.Where(x => x.IsBear).ToList();
			var rest = days.Where(x => !x.IsBear).ToList();
			var shipRest = Stat(rest.Select(x => R(x, ShipShort, ShipLong)));
			var shipBear = Stat(bear.Select(x => R(x, ShipShort, ShipLong)));

			Console.WriteLine($"\n===== {symbol}: PUT-SPREAD DELTA GRID ON ST BEAR SESSIONS =====");
			Console.WriteLine($"{days.Count} sessions total, {bear.Count} ST Bear ({100.0 * bear.Count / days.Count:0.0}%) | " +
				$"{days.First().D:yyyy-MM} -> {days.Last().D:yyyy-MM}");
			Console.WriteLine($"shipped {ShipShort:0.00}/{ShipLong:0.00}:  non-Bear book IR {shipRest.ir:0.000} (n {shipRest.n})   " +
				$"ST Bear IR {shipBear.ir:0.000}, mean {100 * shipBear.m:+0.0000;-0.0000}%, maxDD {shipBear.dd:0.0} (n {shipBear.n})");

			Console.WriteLine($"\nST BEAR ONLY -- IR by strike pair (rows SHORT delta, cols LONG delta)");
			Console.Write($"{"short\\long",12}");
			foreach (double l in Longs) Console.Write($" {l,8:0.00}");
			Console.WriteLine();
			(double sh, double lg, double ir, double m, double dd, double t, int n) best =
				(0, 0, double.NegativeInfinity, 0, 0, 0, 0);
			int cells = 0;
			foreach (double sh in Shorts)
			{
				Console.Write($"{sh,12:0.00}");
				foreach (double lg in Longs)
				{
					if (lg >= sh) { Console.Write($" {"-",8}"); continue; }
					var st = Stat(bear.Select(x => R(x, sh, lg)));
					if (double.IsNaN(st.ir)) { Console.Write($" {"(few)",8}"); continue; }
					cells++;
					if (st.ir > best.ir) best = (sh, lg, st.ir, st.m, st.dd, st.t, st.n);
					Console.Write($" {st.ir,8:+0.000;-0.000}");
				}
				Console.WriteLine();
			}
			Console.WriteLine($"\nbest ST Bear cell: short {best.sh:0.00} / long {best.lg:0.00} -> IR {best.ir:0.000}, " +
				$"mean {100 * best.m:+0.0000;-0.0000}%, maxDD {best.dd:0.0}, n {best.n}, t {best.t:+0.00;-0.00} " +
				$"({cells} cells searched)");

			// The decision: does re-admitting ST Bear at its own best strikes beat skipping it?
			Console.WriteLine($"\n--- FULL BOOK: skip ST Bear, vs re-admit it at its best strikes ---");
			Console.WriteLine($"{"book",44} {"trades",7} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10}");
			void Book(string lbl, IEnumerable<(DateTime D, double R)> src)
			{
				var l = src.Where(x => !double.IsNaN(x.R)).OrderBy(x => x.D).ToList();
				var r = l.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (l.Last().D - l.First().D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,44} {r.Count,7} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0}");
			}
			var restRows = rest.Select(x => (x.D, R: R(x, ShipShort, ShipLong)));
			Book("skip ST Bear [SHIPPED]", restRows);
			Book($"re-admit ST Bear at {ShipShort:0.00}/{ShipLong:0.00}",
				restRows.Concat(bear.Select(x => (x.D, R: R(x, ShipShort, ShipLong)))));
			Book($"re-admit ST Bear at its BEST {best.sh:0.00}/{best.lg:0.00}",
				restRows.Concat(bear.Select(x => (x.D, R: R(x, best.sh, best.lg)))));
			Console.WriteLine("The best-cell row is optimistic by construction -- those strikes were chosen ON this data.");
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
			double lo = S * 0.02, hi = S * 3.0;
			for (int i = 0; i < 90; i++) { double mid = 0.5 * (lo + hi); if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
		}
	}
}
