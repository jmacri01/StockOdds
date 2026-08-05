using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// A/B harness for ShortCallMin: the minimum delta at which a short CALL is worth selling.
	// Two arms per floor, because they miss the delta target in opposite directions:
	//   floor -> sell AT the minimum (more delta shed than the target asked, more premium taken)
	//   skip  -> sell nothing        (less delta shed, no premium, no cost)
	// Run at two spread assumptions: the model default (mid, 0%) where a tiny call is free to trade,
	// and 2% where the "all friction, no premium" argument for a floor actually has teeth.
	public static class MinCallSweep
	{
		public static string[] Symbols =
			{ "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr", "smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };

		public static DateTime StartDate = new DateTime(2020, 1, 1);

		public static OverlayStrategy[] Structures =
			{ OverlayStrategy.Pmcc, OverlayStrategy.PmccShortPut, OverlayStrategy.CoveredStock, OverlayStrategy.SplitStockPut };

		// (minimum delta, skip-below-min?)
		public static (double Min, bool Skip)[] Arms =
			{ (0.0, false), (0.10, false), (0.20, false), (0.10, true), (0.20, true) };

		public static double[] Spreads = { 0.00, 0.02 };

		private sealed class Agg
		{
			public double Sharpe, MedSharpe, Return, MaxDd, Rolls, ShortPrem, Exposure;
			public int Symbols, SharpeWins, DdWins;
		}

		public static async Task Run(string interval)
		{
			// ---- data + engine (engine knobs come from Program.cs, already applied) ----
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
			var engines = barsBySymbol.ToDictionary(kv => kv.Key, kv => BankrollSimulator.Run(kv.Value, 10_000.0));

			double savedMin = OptionsOverlaySimulator.ShortCallMin;
			bool   savedSkip = OptionsOverlaySimulator.ShortCallMinSkip;
			var    savedStrat = OptionsOverlaySimulator.Strategy;
			double savedSpread = OptionsOverlaySimulator.SpreadFraction;

			Console.WriteLine("\n===== MIN SHORT-CALL DELTA SWEEP =====");
			Console.WriteLine($"Symbols {barsBySymbol.Count} | window from {StartDate:yyyy-MM-dd} | VRP {OptionsOverlaySimulator.VolRiskPremium:0.00} | DTE {OptionsOverlaySimulator.ShortDteDays:0}");
			Console.WriteLine("floor = sell AT the min (over-trims delta) | skip = sell nothing below the min (under-trims)");

			try
			{
				foreach (var spread in Spreads)
				{
					OptionsOverlaySimulator.SpreadFraction = spread;
					Console.WriteLine($"\n---------- option spread {spread * 100:0.#}% ----------");

					foreach (var strat in Structures)
					{
						OptionsOverlaySimulator.Strategy = strat;

						List<OverlayResult> EvalAll(double min, bool skip)
						{
							OptionsOverlaySimulator.ShortCallMin = min;
							OptionsOverlaySimulator.ShortCallMinSkip = skip;
							return barsBySymbol.OrderBy(kv => kv.Key)
								.Select(kv => OptionsOverlaySimulator.Run(kv.Value, engines[kv.Key], StartDate)).ToList();
						}

						var baseRes = EvalAll(0.0, false);
						Agg Summarize(List<OverlayResult> rs) => new Agg
						{
							Symbols    = rs.Count,
							Sharpe     = rs.Average(r => Safe(r.SharpeRatio)),
							MedSharpe  = Median(rs.Select(r => Safe(r.SharpeRatio)).ToList()),
							Return     = rs.Average(r => Safe(r.TotalReturnPct)),
							MaxDd      = rs.Average(r => Safe(r.MaxDrawdownPct)),
							Rolls      = rs.Average(r => (double)r.Rolls),
							ShortPrem  = rs.Average(r => Safe(r.ShortPremiumTaken)),
							Exposure   = rs.Average(r => Safe(r.MeanExposure)),
							SharpeWins = rs.Where((r, i) => Safe(r.SharpeRatio) > Safe(baseRes[i].SharpeRatio) + 1e-9).Count(),
							DdWins     = rs.Where((r, i) => Safe(r.MaxDrawdownPct) < Safe(baseRes[i].MaxDrawdownPct) - 1e-9).Count(),
						};

						var b = Summarize(baseRes);
						Console.WriteLine($"\n{strat}");
						Console.WriteLine($"{"arm",12} {"Sharpe",8} {"dShp",8} {"MedShp",8} {"Ret%",11} {"DD%",8} {"rolls",7} {"prem",7} {"netD",6} {"ShpW",6} {"DdW",5}");
						Console.WriteLine($"{"off",12} {b.Sharpe,8:0.000} {"",8} {b.MedSharpe,8:0.000} {b.Return,11:0.0} {b.MaxDd,8:0.00} {b.Rolls,7:0} {b.ShortPrem,7:0.00} {b.Exposure,6:0.00}");

						foreach (var (min, skip) in Arms)
						{
							if (min <= 0) continue;
							var a = Summarize(EvalAll(min, skip));
							string name = $"{(skip ? "skip" : "floor")} {min:0.00}";
							Console.WriteLine($"{name,12} {a.Sharpe,8:0.000} {a.Sharpe - b.Sharpe,8:+0.000;-0.000} {a.MedSharpe,8:0.000} " +
								$"{a.Return,11:0.0} {a.MaxDd,8:0.00} {a.Rolls,7:0} {a.ShortPrem,7:0.00} {a.Exposure,6:0.00} " +
								$"{a.SharpeWins + "/" + a.Symbols,6} {a.DdWins + "/" + a.Symbols,5}");
						}
					}
				}
			}
			finally
			{
				OptionsOverlaySimulator.ShortCallMin = savedMin;
				OptionsOverlaySimulator.ShortCallMinSkip = savedSkip;
				OptionsOverlaySimulator.Strategy = savedStrat;
				OptionsOverlaySimulator.SpreadFraction = savedSpread;
			}
		}

		// Per-symbol detail for one structure at one spread: baseline vs a single arm. Used to check whether an
		// aggregate win is broad or is one name being rescued from a blow-up.
		public static async Task Detail(string interval, OverlayStrategy strat, double spread, double min, bool skip)
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
			var engines = barsBySymbol.ToDictionary(kv => kv.Key, kv => BankrollSimulator.Run(kv.Value, 10_000.0));

			var savedStrat = OptionsOverlaySimulator.Strategy;
			double savedSpread = OptionsOverlaySimulator.SpreadFraction;
			double savedMin = OptionsOverlaySimulator.ShortCallMin;
			bool savedSkip = OptionsOverlaySimulator.ShortCallMinSkip;

			OptionsOverlaySimulator.Strategy = strat;
			OptionsOverlaySimulator.SpreadFraction = spread;

			Console.WriteLine($"\n===== PER-SYMBOL: {strat} @ {spread * 100:0.#}% spread — off vs {(skip ? "skip" : "floor")} {min:0.00} =====");
			Console.WriteLine($"{"sym",6} {"offShp",8} {"armShp",8} {"dShp",8} {"offRet%",11} {"armRet%",11} {"offDD%",8} {"armDD%",8} {"offPrem",10}");

			try
			{
				foreach (var (sym, bars) in barsBySymbol.OrderBy(kv => kv.Key))
				{
					OptionsOverlaySimulator.ShortCallMin = 0.0; OptionsOverlaySimulator.ShortCallMinSkip = false;
					var o = OptionsOverlaySimulator.Run(bars, engines[sym], StartDate);
					OptionsOverlaySimulator.ShortCallMin = min; OptionsOverlaySimulator.ShortCallMinSkip = skip;
					var a = OptionsOverlaySimulator.Run(bars, engines[sym], StartDate);
					Console.WriteLine($"{sym,6} {Safe(o.SharpeRatio),8:0.000} {Safe(a.SharpeRatio),8:0.000} " +
						$"{Safe(a.SharpeRatio) - Safe(o.SharpeRatio),8:+0.000;-0.000} " +
						$"{Safe(o.TotalReturnPct),11:0.0} {Safe(a.TotalReturnPct),11:0.0} " +
						$"{Safe(o.MaxDrawdownPct),8:0.00} {Safe(a.MaxDrawdownPct),8:0.00} {Safe(o.ShortPremiumTaken),10:0.0}");
				}
			}
			finally
			{
				OptionsOverlaySimulator.Strategy = savedStrat;
				OptionsOverlaySimulator.SpreadFraction = savedSpread;
				OptionsOverlaySimulator.ShortCallMin = savedMin;
				OptionsOverlaySimulator.ShortCallMinSkip = savedSkip;
			}
		}

		// SKIP-ONLY threshold ladder: "don't sell the call at all unless its delta reaches the minimum"
		// (min 0.10 on a delta-1 stock core = only sell when target <= 0.90). Reports the BIND RATE --
		// the share of would-be call sales the rule suppresses -- so a flat result can be read correctly.
		public static double[] SkipThresholds = { 0.05, 0.10, 0.15, 0.20, 0.30, 0.50 };

		public static async Task SkipLadder(string interval, bool excludeOpen)
		{
			var barsBySymbol = new Dictionary<string, List<OhlcBar>>();
			foreach (var sym in Symbols)
			{
				if (excludeOpen && sym == "open") continue;
				try
				{
					var b = (await YahooClient.GetBarsAsync(sym, interval)).Where(x => x.Date >= StartDate).ToList();
					if (b.Count >= 120) barsBySymbol[sym] = b;
				}
				catch { }
			}
			var engines = barsBySymbol.ToDictionary(kv => kv.Key, kv => BankrollSimulator.Run(kv.Value, 10_000.0));

			var savedStrat = OptionsOverlaySimulator.Strategy;
			double savedSpread = OptionsOverlaySimulator.SpreadFraction;
			double savedMin = OptionsOverlaySimulator.ShortCallMin;
			bool savedSkip = OptionsOverlaySimulator.ShortCallMinSkip;
			OptionsOverlaySimulator.ShortCallMinSkip = true;

			Console.WriteLine($"\n===== SKIP-ONLY THRESHOLD LADDER{(excludeOpen ? " (ex-OPEN)" : "")} =====");
			Console.WriteLine($"Symbols {barsBySymbol.Count} | rule: sell the call only if its delta >= min");

			try
			{
				foreach (var spread in Spreads)
				{
					OptionsOverlaySimulator.SpreadFraction = spread;
					Console.WriteLine($"\n---------- option spread {spread * 100:0.#}% ----------");
					foreach (var strat in Structures)
					{
						OptionsOverlaySimulator.Strategy = strat;

						List<OverlayResult> EvalAll(double min)
						{
							OptionsOverlaySimulator.ShortCallMin = min;
							return barsBySymbol.OrderBy(kv => kv.Key)
								.Select(kv => OptionsOverlaySimulator.Run(kv.Value, engines[kv.Key], StartDate)).ToList();
						}

						var b = EvalAll(0.0);
						double bSh = b.Average(r => Safe(r.SharpeRatio));
						Console.WriteLine($"\n{strat}   (base Sharpe {bSh:0.000}, ret {b.Average(r => Safe(r.TotalReturnPct)):0.0}%, dd {b.Average(r => Safe(r.MaxDrawdownPct)):0.00}%)");
						Console.WriteLine($"{"min",6} {"bind%",7} {"Sharpe",8} {"dShp",8} {"Ret%",10} {"DD%",8} {"netD",6} {"ShpW",6}");

						foreach (var min in SkipThresholds)
						{
							var a = EvalAll(min);
							double wanted = a.Sum(r => r.ShortCallWanted);
							double bound  = a.Sum(r => r.ShortCallBound);
							double sh = a.Average(r => Safe(r.SharpeRatio));
							int wins = a.Where((r, i) => Safe(r.SharpeRatio) > Safe(b[i].SharpeRatio) + 1e-9).Count();
							Console.WriteLine($"{min,6:0.00} {(wanted > 0 ? 100.0 * bound / wanted : 0),7:0.0} {sh,8:0.000} {sh - bSh,8:+0.000;-0.000} " +
								$"{a.Average(r => Safe(r.TotalReturnPct)),10:0.0} {a.Average(r => Safe(r.MaxDrawdownPct)),8:0.00} " +
								$"{a.Average(r => Safe(r.MeanExposure)),6:0.00} {wins + "/" + a.Count,6}");
						}
					}
				}
			}
			finally
			{
				OptionsOverlaySimulator.Strategy = savedStrat;
				OptionsOverlaySimulator.SpreadFraction = savedSpread;
				OptionsOverlaySimulator.ShortCallMin = savedMin;
				OptionsOverlaySimulator.ShortCallMinSkip = savedSkip;
			}
		}

		// One symbol across spreads and arms, with cumulative friction, to test whether a blow-up is
		// friction-driven (which is what a min-delta floor is supposed to fix) or something else.
		public static async Task OneSymbol(string interval, string sym, OverlayStrategy strat)
		{
			var bars = (await YahooClient.GetBarsAsync(sym, interval)).Where(x => x.Date >= StartDate).ToList();
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			var savedStrat = OptionsOverlaySimulator.Strategy;
			double savedSpread = OptionsOverlaySimulator.SpreadFraction;
			double savedMin = OptionsOverlaySimulator.ShortCallMin;
			bool savedSkip = OptionsOverlaySimulator.ShortCallMinSkip;
			OptionsOverlaySimulator.Strategy = strat;

			Console.WriteLine($"\n===== {sym.ToUpper()} — {strat} across spreads =====");
			Console.WriteLine($"engine: bars {bars.Count}, mean target {eng.Positions.DefaultIfEmpty(0).Average():0.000}");
			Console.WriteLine($"{"spread",7} {"arm",12} {"Sharpe",8} {"Ret%",10} {"DD%",8} {"frict",8} {"rolls",7} {"netD",6}");

			try
			{
				foreach (var sp in new[] { 0.0, 0.005, 0.01, 0.02 })
				{
					OptionsOverlaySimulator.SpreadFraction = sp;
					foreach (var (min, skip) in Arms)
					{
						OptionsOverlaySimulator.ShortCallMin = min;
						OptionsOverlaySimulator.ShortCallMinSkip = skip;
						var r = OptionsOverlaySimulator.Run(bars, eng, StartDate);
						string name = min <= 0 ? "off" : $"{(skip ? "skip" : "floor")} {min:0.00}";
						// cumulative friction as a multiple of the account (each bar's friction / that bar's bankroll)
						double frict = r.FrictionFrac.Sum();
						Console.WriteLine($"{sp * 100,6:0.#}% {name,12} {Safe(r.SharpeRatio),8:0.000} {Safe(r.TotalReturnPct),10:0.0} " +
							$"{Safe(r.MaxDrawdownPct),8:0.00} {frict,8:0.00} {r.Rolls,7} {Safe(r.MeanExposure),6:0.00}");
					}
					Console.WriteLine();
				}
			}
			finally
			{
				OptionsOverlaySimulator.Strategy = savedStrat;
				OptionsOverlaySimulator.SpreadFraction = savedSpread;
				OptionsOverlaySimulator.ShortCallMin = savedMin;
				OptionsOverlaySimulator.ShortCallMinSkip = savedSkip;
			}
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
