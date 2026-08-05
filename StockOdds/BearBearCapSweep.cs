using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Ceiling on the traded position in the (LT Bear, ST Bear) bucket.
	//
	// The bucket's raw target is -1.0 and MinExposurePercent floors it at 0, and BearRegimeMode already sends the
	// position to cash once the raw EMA turns negative -- so this is NOT about the steady state. It is about the LAG
	// on the way in: the exposure EMA is still positive for the first bars in the bucket and the KAMA smoother then
	// blends the cash decision in gradually, so real exposure is carried through the worst cell of the map.
	//
	// SCORING. Any ceiling lowers mean exposure, and lower exposure moves return/drawdown on its own. So each cap is
	// reported two ways:
	//   Sharpe        -- exposure-invariant, so a gain here is genuine selection
	//   ret/dd vs a FLAT HAIRCUT matched to the same mean exposure -- the signal-free control. A cap only earns its
	//                    place if it beats the haircut that holds the same amount with no rule in it.
	public static class BearBearCapSweep
	{
		public static string[] Symbols =
			{ "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr", "smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };

		public static DateTime StartDate = new DateTime(2020, 1, 1);

		// percent of full exposure; -1 = off (unbounded)
		public static double[] Caps = { -1.0, 100.0, 75.0, 50.0, 25.0, 0.0 };

		private sealed class Row
		{
			public string Label = "";
			public double Sharpe, MedSharpe, Return, MaxDd, Exposure, RetPerDd;
			public double BbBarPct, BbBindPct, BbMeanRaw;
			public int SharpeWins, DdWins, Symbols;
		}

		public static async Task Run(string interval)
		{
			var barsBySymbol = new Dictionary<string, List<OhlcBar>>();
			foreach (var sym in Symbols)
			{
				try
				{
					var b = (await YahooClient.GetBarsAsync(sym, interval)).Where(x => x.Date >= StartDate).ToList();
					if (b.Count >= 120) barsBySymbol[sym] = b;
					else Console.WriteLine($"  skipping {sym}: {b.Count} bars");
				}
				catch (Exception ex) { Console.WriteLine($"  skipping {sym}: {ex.Message}"); }
			}

			double savedCap = BankrollSimulator.BearBearMaxExposure;
			double savedHair = BankrollSimulator.FlatHaircut;

			List<BankrollResult> EvalAll(double cap, double haircut)
			{
				BankrollSimulator.BearBearMaxExposure = cap;
				BankrollSimulator.FlatHaircut = haircut;
				return barsBySymbol.OrderBy(kv => kv.Key)
					.Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
			}

			try
			{
				var baseRes = EvalAll(-1.0, 1.0);
				double baseExp = MeanExposure(baseRes);

				Row Summarize(string label, List<BankrollResult> rs) => new Row
				{
					Label      = label,
					Symbols    = rs.Count,
					Sharpe     = rs.Average(r => Safe(r.SharpeRatio)),
					MedSharpe  = Median(rs.Select(r => Safe(r.SharpeRatio)).ToList()),
					Return     = rs.Average(r => Safe(r.TotalReturnPct)),
					MaxDd      = rs.Average(r => Safe(r.MaxDrawdownPct)),
					RetPerDd   = rs.Average(r => Safe(r.MaxDrawdownPct) > 0.01 ? Safe(r.TotalReturnPct) / Safe(r.MaxDrawdownPct) : 0.0),
					Exposure   = MeanExposure(rs),
					BbBarPct   = 100.0 * rs.Sum(r => r.BearBearBars) / Math.Max(1, rs.Sum(r => r.Positions.Count)),
					BbBindPct  = 100.0 * rs.Sum(r => r.BearBearCapBound) / Math.Max(1, rs.Sum(r => r.BearBearBars)),
					BbMeanRaw  = rs.Where(r => r.BearBearBars > 0).Select(r => r.BearBearMeanRaw).DefaultIfEmpty(0).Average(),
					SharpeWins = rs.Where((r, i) => Safe(r.SharpeRatio) > Safe(baseRes[i].SharpeRatio) + 1e-9).Count(),
					DdWins     = rs.Where((r, i) => Safe(r.MaxDrawdownPct) < Safe(baseRes[i].MaxDrawdownPct) - 1e-9).Count(),
				};

				var b0 = Summarize("off", baseRes);

				Console.WriteLine("\n===== (LT Bear, ST Bear) EXPOSURE CEILING =====");
				Console.WriteLine($"Symbols {barsBySymbol.Count} | from {StartDate:yyyy-MM-dd} | baseline mean exposure {baseExp:0.000}");
				Console.WriteLine($"Bear/Bear is {b0.BbBarPct:0.0}% of all bars; mean position held there at baseline: {b0.BbMeanRaw:0.000}");
				Console.WriteLine("flatRatio = ret/dd of a signal-free constant haircut matched to the SAME mean exposure");
				Console.WriteLine();
				Console.WriteLine($"{"cap%",6} {"bind%",7} {"Sharpe",8} {"dShp",8} {"MedShp",8} {"Ret%",10} {"DD%",8} {"exp",6} " +
					$"{"ret/dd",8} {"flat",8} {"excess",8} {"ShpW",6} {"DdW",5}");

				foreach (var cap in Caps)
				{
					var rs = EvalAll(cap, 1.0);
					var r = Summarize(cap < 0 ? "off" : $"{cap:0}", rs);

					// exposure-matched flat-haircut control: solve for the constant multiplier that reproduces this
					// config's mean exposure, then score ret/dd with that instead of the rule.
					double flatRatio = double.NaN;
					if (cap >= 0 && r.Exposure < baseExp - 1e-6 && baseExp > 1e-9)
					{
						double h = SolveHaircut(r.Exposure, baseExp, EvalAll);
						var ctrl = EvalAll(-1.0, h);
						flatRatio = ctrl.Average(x => Safe(x.MaxDrawdownPct) > 0.01 ? Safe(x.TotalReturnPct) / Safe(x.MaxDrawdownPct) : 0.0);
					}

					string bind = cap < 0 ? "" : $"{r.BbBindPct:0.0}";
					string dsh  = cap < 0 ? "" : $"{r.Sharpe - b0.Sharpe:+0.000;-0.000}";
					string flat = double.IsNaN(flatRatio) ? "" : $"{flatRatio:0.000}";
					string exc  = double.IsNaN(flatRatio) ? "" : $"{r.RetPerDd - flatRatio:+0.000;-0.000}";
					string wins = cap < 0 ? "" : $"{r.SharpeWins}/{r.Symbols}";
					string dws  = cap < 0 ? "" : $"{r.DdWins}/{r.Symbols}";

					Console.WriteLine($"{r.Label,6} {bind,7} {r.Sharpe,8:0.000} {dsh,8} {r.MedSharpe,8:0.000} {r.Return,10:0.0} " +
						$"{r.MaxDd,8:0.00} {r.Exposure,6:0.000} {r.RetPerDd,8:0.000} {flat,8} {exc,8} {wins,6} {dws,5}");
				}
			}
			finally
			{
				BankrollSimulator.BearBearMaxExposure = savedCap;
				BankrollSimulator.FlatHaircut = savedHair;
			}
		}

		// The mirror test: BOOST the (Bear, Bear) position instead of capping it. Scored the same way, against a
		// flat multiplier matched to the same (now higher) mean exposure.
		public static double[] Mults = { 1.25, 1.5, 2.0 };

		public static async Task Boost(string interval)
		{
			var barsBySymbol = new Dictionary<string, List<OhlcBar>>();
			foreach (var sym in Symbols)
			{
				try
				{
					var b = (await YahooClient.GetBarsAsync(sym, interval)).Where(x => x.Date >= StartDate).ToList();
					if (b.Count >= 120) barsBySymbol[sym] = b;
				}
				catch { }
			}

			double savedMult = BankrollSimulator.BearBearMult;
			double savedHair = BankrollSimulator.FlatHaircut;

			List<BankrollResult> EvalAll(double mult, double haircut)
			{
				BankrollSimulator.BearBearMult = mult;
				BankrollSimulator.FlatHaircut = haircut;
				return barsBySymbol.OrderBy(kv => kv.Key)
					.Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
			}
			double RetPerDd(List<BankrollResult> rs) =>
				rs.Average(x => Safe(x.MaxDrawdownPct) > 0.01 ? Safe(x.TotalReturnPct) / Safe(x.MaxDrawdownPct) : 0.0);

			try
			{
				var baseRes = EvalAll(1.0, 1.0);
				double baseExp = MeanExposure(baseRes);
				double baseSh = baseRes.Average(r => Safe(r.SharpeRatio));

				Console.WriteLine("\n===== (LT Bear, ST Bear) BOOST (mirror of the ceiling) =====");
				Console.WriteLine($"Symbols {barsBySymbol.Count} | baseline Sharpe {baseSh:0.000}, exposure {baseExp:0.000}, ret/dd {RetPerDd(baseRes):0.000}");
				Console.WriteLine($"{"mult",6} {"Sharpe",8} {"dShp",8} {"Ret%",10} {"DD%",8} {"exp",6} {"ret/dd",8} {"flat",8} {"excess",8} {"ShpW",6}");

				foreach (var m in Mults)
				{
					var rs = EvalAll(m, 1.0);
					double exp = MeanExposure(rs);
					double sh = rs.Average(r => Safe(r.SharpeRatio));
					double rpd = RetPerDd(rs);
					double h = SolveHaircut(exp, baseExp, (c, hair) => EvalAll(1.0, hair));
					double flat = RetPerDd(EvalAll(1.0, h));
					int wins = rs.Where((r, i) => Safe(r.SharpeRatio) > Safe(baseRes[i].SharpeRatio) + 1e-9).Count();
					Console.WriteLine($"{m,6:0.00} {sh,8:0.000} {sh - baseSh,8:+0.000;-0.000} {rs.Average(r => Safe(r.TotalReturnPct)),10:0.0} " +
						$"{rs.Average(r => Safe(r.MaxDrawdownPct)),8:0.00} {exp,6:0.000} {rpd,8:0.000} {flat,8:0.000} " +
						$"{rpd - flat,8:+0.000;-0.000} {wins + "/" + rs.Count,6}");
				}
			}
			finally
			{
				BankrollSimulator.BearBearMult = savedMult;
				BankrollSimulator.FlatHaircut = savedHair;
			}
		}

		// bisect the constant multiplier whose mean exposure matches the capped config's.
		// Bounds span both directions so it works for a cap (h < 1) and a boost (h > 1).
		private static double SolveHaircut(double targetExp, double baseExp, Func<double, double, List<BankrollResult>> eval)
		{
			double lo = 0.0, hi = targetExp > baseExp ? 4.0 : 1.0;
			for (int i = 0; i < 20; i++)
			{
				double mid = 0.5 * (lo + hi);
				double e = MeanExposure(eval(-1.0, mid));
				if (e > targetExp) hi = mid; else lo = mid;
				if (Math.Abs(e - targetExp) < 1e-5) return mid;
			}
			return 0.5 * (lo + hi);
		}

		private static double MeanExposure(List<BankrollResult> rs)
		{
			double sum = 0; int n = 0;
			foreach (var r in rs) { foreach (var p in r.Positions) { sum += Math.Abs(p); n++; } }
			return n > 0 ? sum / n : 0.0;
		}

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
