using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Benchmark context for every other harness in this folder: what the shipped engine actually returns and
	// draws down over the same 18 names/window, against buy & hold on the same bars, plus how much of the time
	// the capital is actually deployed.
	//
	// TIME IN TRADE = % of bars with position > 0.05 (the same threshold the options overlay uses), and
	// MEAN EXPOSURE = average |position| across all bars. The pair matters because B&H is by definition
	// 100% TiT at exposure 1.0: a strategy at exposure 0.39 is using well under half the capital, so a
	// return below B&H is not automatically worse per dollar deployed.
	public static class ReferenceStats
	{
		public static string[] Symbols =
			{ "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr", "smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };

		public static DateTime StartDate = new DateTime(2020, 1, 1);

		public static async Task Run(string interval)
		{
			var rows = new List<(string Sym, BankrollResult R, double Tit, double Exp)>();
			foreach (var sym in Symbols)
			{
				try
				{
					var b = (await YahooClient.GetBarsAsync(sym, interval)).Where(x => x.Date >= StartDate).ToList();
					if (b.Count < 120) { Console.WriteLine($"  skipping {sym}: {b.Count} bars"); continue; }
					var r = BankrollSimulator.Run(b, 10_000.0);
					int n = r.Positions.Count;
					double tit = n > 0 ? 100.0 * r.Positions.Count(p => Math.Abs(p) > 0.05) / n : 0.0;
					double exp = n > 0 ? r.Positions.Average(p => Math.Abs(p)) : 0.0;
					rows.Add((sym, r, tit, exp));
				}
				catch (Exception ex) { Console.WriteLine($"  skipping {sym}: {ex.Message}"); }
			}

			Console.WriteLine("\n===== REFERENCE: SHIPPED ENGINE vs BUY & HOLD =====");
			Console.WriteLine($"{rows.Count} symbols | from {StartDate:yyyy-MM-dd} | TiT = % bars with position > 0.05");
			Console.WriteLine();
			Console.WriteLine($"{"sym",6} {"bars",6} {"stRet%",10} {"bhRet%",10} {"stDD%",8} {"bhDD%",8} " +
				$"{"stShp",7} {"bhShp",7} {"st r/dd",8} {"bh r/dd",8} {"TiT%",7} {"exp",6}");

			foreach (var (sym, r, tit, exp) in rows.OrderBy(x => x.Sym))
				Console.WriteLine($"{sym,6} {r.Positions.Count,6} {Safe(r.TotalReturnPct),10:0.0} {Safe(r.BuyHoldReturnPct),10:0.0} " +
					$"{Safe(r.MaxDrawdownPct),8:0.00} {Safe(r.BuyHoldMaxDrawdownPct),8:0.00} " +
					$"{Safe(r.SharpeRatio),7:0.000} {Safe(r.BuyHoldSharpeRatio),7:0.000} " +
					$"{Rpd(r.TotalReturnPct, r.MaxDrawdownPct),8:0.000} {Rpd(r.BuyHoldReturnPct, r.BuyHoldMaxDrawdownPct),8:0.000} " +
					$"{tit,7:0.0} {exp,6:0.000}");

			void Agg(string label, Func<IEnumerable<double>, double> f)
			{
				Console.WriteLine($"{label,6} {"",6} {f(rows.Select(x => Safe(x.R.TotalReturnPct))),10:0.0} " +
					$"{f(rows.Select(x => Safe(x.R.BuyHoldReturnPct))),10:0.0} " +
					$"{f(rows.Select(x => Safe(x.R.MaxDrawdownPct))),8:0.00} {f(rows.Select(x => Safe(x.R.BuyHoldMaxDrawdownPct))),8:0.00} " +
					$"{f(rows.Select(x => Safe(x.R.SharpeRatio))),7:0.000} {f(rows.Select(x => Safe(x.R.BuyHoldSharpeRatio))),7:0.000} " +
					$"{f(rows.Select(x => Rpd(x.R.TotalReturnPct, x.R.MaxDrawdownPct))),8:0.000} " +
					$"{f(rows.Select(x => Rpd(x.R.BuyHoldReturnPct, x.R.BuyHoldMaxDrawdownPct))),8:0.000} " +
					$"{f(rows.Select(x => x.Tit)),7:0.0} {f(rows.Select(x => x.Exp)),6:0.000}");
			}

			Console.WriteLine();
			Agg("MEAN", xs => xs.Average());
			Agg("MEDIAN", xs => Median(xs.ToList()));

			// per-dollar-deployed view: the engine is only ~40% invested, so scale its return to a matched
			// capital base. Crude (ignores compounding path) but it frames the return gap correctly.
			double meanExp = rows.Average(x => x.Exp);
			Console.WriteLine();
			Console.WriteLine($"Engine mean exposure {meanExp:0.000} vs B&H 1.000 -- the engine is ~{100 * meanExp:0}% invested on average.");
			Console.WriteLine($"Beat B&H on return: {rows.Count(x => x.R.TotalReturnPct > x.R.BuyHoldReturnPct)}/{rows.Count} | " +
				$"on drawdown: {rows.Count(x => x.R.MaxDrawdownPct < x.R.BuyHoldMaxDrawdownPct)}/{rows.Count} | " +
				$"on Sharpe: {rows.Count(x => Safe(x.R.SharpeRatio) > Safe(x.R.BuyHoldSharpeRatio))}/{rows.Count} | " +
				$"on ret/dd: {rows.Count(x => Rpd(x.R.TotalReturnPct, x.R.MaxDrawdownPct) > Rpd(x.R.BuyHoldReturnPct, x.R.BuyHoldMaxDrawdownPct))}/{rows.Count}");
		}

		private static double Rpd(double ret, double dd) => dd > 0.01 ? Safe(ret) / dd : 0.0;
		private static double Safe(double x) => double.IsNaN(x) || double.IsInfinity(x) ? 0.0 : x;

		private static double Median(List<double> xs)
		{
			if (xs.Count == 0) return 0.0;
			var s = xs.OrderBy(x => x).ToList();
			int m = s.Count / 2;
			return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
		}
	}
}
