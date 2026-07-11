using System;
using System.Collections.Generic;
using System.Linq;

namespace StockOdds
{
	public static class GridSearchPrinter
	{
		// Prints the top-N combinations by Sharpe, plus the single best knob values so
		// they can be pasted straight back into Program.cs.
		public static void Print(List<GridPoint> results, int top = 20)
		{
			Console.WriteLine("\n===== GRID SEARCH (top by Sharpe) =====");
			Console.WriteLine($"Combinations evaluated : {results.Count}");
			Console.WriteLine();
			Console.WriteLine(
				$"{"EMA",4} {"BiasP",6} {"LBias",6} {"BiasEMA",8} {"Drift%",7} " +
				$"{"Sharpe",8} {"Return%",10} {"MaxDD%",9}");

			foreach (var p in results.Take(top))
			{
				Console.WriteLine(
					$"{p.ExposureEmaPeriod,4} {p.BiasPeriod,6} {p.LongBias,6:0.##} {p.BiasEmaPeriod,8} " +
					$"{p.RebalanceDriftPercent,7:0.#} {p.Sharpe,8:0.000} {Signed(p.TotalReturnPct),10} " +
					$"-{p.MaxDrawdownPct,7:0.00}%");
			}

			var best = results.FirstOrDefault();
			if (best != null)
			{
				Console.WriteLine();
				Console.WriteLine("Best (paste into Program.cs):");
				Console.WriteLine($"  BankrollSimulator.ExposureEmaPeriod     = {best.ExposureEmaPeriod};");
				Console.WriteLine($"  BankrollSimulator.BiasPeriod            = {best.BiasPeriod};");
				Console.WriteLine($"  BankrollSimulator.LongBias              = {best.LongBias};");
				Console.WriteLine($"  BankrollSimulator.BiasEmaPeriod         = {best.BiasEmaPeriod};");
				Console.WriteLine($"  BankrollSimulator.RebalanceDriftPercent = {best.RebalanceDriftPercent};");
			}
		}

		// Multi-symbol version: ranked by mean Sharpe across the basket, with the worst
		// symbol (MinSharpe) shown so combos that only work on one name stand out. Then
		// the per-symbol Sharpe breakdown for the winning combination.
		public static void PrintMulti(List<MultiGridPoint> results, IEnumerable<string> symbols, int top = 20)
		{
			var syms = symbols.ToList();

			Console.WriteLine("\n===== GRID SEARCH — MULTI-SYMBOL (top by mean Sharpe) =====");
			Console.WriteLine($"Symbols ({syms.Count})          : {string.Join(", ", syms)}");
			Console.WriteLine($"Combinations evaluated : {results.Count}");
			Console.WriteLine();
			Console.WriteLine(
				$"{"EMA",4} {"BiasP",6} {"LBias",6} {"BiasEMA",8} {"Drift%",7} " +
				$"{"MeanShp",8} {"MinShp",8} {"MeanRet%",10} {"MeanDD%",9}");

			foreach (var p in results.Take(top))
			{
				Console.WriteLine(
					$"{p.ExposureEmaPeriod,4} {p.BiasPeriod,6} {p.LongBias,6:0.##} {p.BiasEmaPeriod,8} " +
					$"{p.RebalanceDriftPercent,7:0.#} {p.MeanSharpe,8:0.000} {p.MinSharpe,8:0.000} " +
					$"{Signed(p.MeanReturnPct),10} -{p.MeanMaxDrawdownPct,7:0.00}%");
			}

			var best = results.FirstOrDefault();
			if (best != null)
			{
				Console.WriteLine();
				Console.WriteLine("Best combo — per-symbol Sharpe:");
				foreach (var s in syms)
				{
					double sh = best.SharpeBySymbol.TryGetValue(s, out var v) ? v : double.NaN;
					Console.WriteLine($"  {s,-10} {sh,8:0.000}");
				}

				Console.WriteLine();
				Console.WriteLine("Best (paste into Program.cs):");
				Console.WriteLine($"  BankrollSimulator.ExposureEmaPeriod     = {best.ExposureEmaPeriod};");
				Console.WriteLine($"  BankrollSimulator.BiasPeriod            = {best.BiasPeriod};");
				Console.WriteLine($"  BankrollSimulator.LongBias              = {best.LongBias};");
				Console.WriteLine($"  BankrollSimulator.BiasEmaPeriod         = {best.BiasEmaPeriod};");
				Console.WriteLine($"  BankrollSimulator.RebalanceDriftPercent = {best.RebalanceDriftPercent};");
			}
		}

		// Volatility study: one row per symbol, sorted by HV. Knobs are AVERAGED over each
		// symbol's top-region combos (robust), and Pearson correlations quantify how each
		// knob tracks HV rather than leaving it to the eye.
		public static void PrintPerSymbolOptima(List<SymbolOptimum> optima)
		{
			Console.WriteLine("\n===== VOLATILITY -> OPTIMAL PARAMETERS (robust: avg of top region) =====");
			int region = optima.FirstOrDefault()?.RegionCount ?? 0;
			Console.WriteLine($"Symbols : {optima.Count}   (each knob = mean over that symbol's top {region} combos)");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",7} {"Bars",5}   " +
				$"{"EMA",5} {"BiasP",6} {"LBias",6} {"BiasEMA",8} {"Drift%",7} " +
				$"{"TopShp",7} {"RegRet%",10} {"RegDD%",9}");

			foreach (var o in optima)
			{
				Console.WriteLine(
					$"{o.Symbol,-8} {o.HistoricalVolatilityPct,7:0.0} {o.Bars,5}   " +
					$"{o.EmaPeriod,5:0.0} {o.BiasPeriod,6:0.0} {o.LongBias,6:0.##} {o.BiasEmaPeriod,8:0.0} " +
					$"{o.DriftPercent,7:0.0} {o.TopSharpe,7:0.00} {Signed(o.MeanRegionReturnPct),10} " +
					$"-{o.MeanRegionMaxDdPct,7:0.00}%");
			}

			// correlation of HV against each averaged knob (needs >= 2 symbols)
			if (optima.Count >= 2)
			{
				var hv = optima.Select(o => o.HistoricalVolatilityPct).ToList();
				Console.WriteLine();
				Console.WriteLine("Correlation of HV vs. optimal knob (Pearson r, -1..+1):");
				Console.WriteLine($"  EMA period   : {Corr(hv, optima.Select(o => o.EmaPeriod)):+0.000;-0.000}");
				Console.WriteLine($"  Bias period  : {Corr(hv, optima.Select(o => o.BiasPeriod)):+0.000;-0.000}");
				Console.WriteLine($"  Long bias    : {Corr(hv, optima.Select(o => o.LongBias)):+0.000;-0.000}");
				Console.WriteLine($"  Bias EMA     : {Corr(hv, optima.Select(o => o.BiasEmaPeriod)):+0.000;-0.000}");
				Console.WriteLine($"  Drift %      : {Corr(hv, optima.Select(o => o.DriftPercent)):+0.000;-0.000}");
				Console.WriteLine("(r near +1 => that knob rises with volatility; near 0 => no linear relationship.)");
			}

			// ---- Do the knobs even matter? Spread of Sharpe across the WHOLE grid. ----
			Console.WriteLine();
			Console.WriteLine("Knob impact — Sharpe spread across all combos per symbol:");
			Console.WriteLine($"{"Symbol",-8} {"HV%",7}   {"Best",7} {"Median",7} {"Worst",7} {"Best-Med",9}");
			foreach (var o in optima)
			{
				Console.WriteLine(
					$"{o.Symbol,-8} {o.HistoricalVolatilityPct,7:0.0}   " +
					$"{o.TopSharpe,7:0.00} {o.GridMedianSharpe,7:0.00} {o.GridWorstSharpe,7:0.00} " +
					$"{o.SharpeSpread,9:0.00}");
			}
			if (optima.Count > 0)
			{
				double avgSpread = optima.Average(o => o.SharpeSpread);
				Console.WriteLine();
				Console.WriteLine($"Mean best-vs-median Sharpe uplift from tuning : {avgSpread:0.00}");
				Console.WriteLine("(Large => knob choice matters a lot; small => smoothing is second-order noise.)");
			}
		}

		// Walk-forward: per-symbol tuned-on-train vs. one global default, both scored on the
		// held-out test slice. The decisive question is whether TunedTest beats DefaultTest
		// out-of-sample (TuningEdge > 0) or whether the in-sample edge was just overfitting.
		public static void PrintWalkForward(List<WalkForwardRow> rows)
		{
			Console.WriteLine("\n===== WALK-FORWARD VALIDATION (tune on train, score on test) =====");
			var gd = rows.FirstOrDefault()?.GlobalDefault;
			if (gd != null)
				Console.WriteLine(
					$"Global default (best mean train Sharpe): EMA={gd.ExposureEmaPeriod} " +
					$"BiasP={gd.BiasPeriod} LBias={gd.LongBias:0.##} BiasEMA={gd.BiasEmaPeriod} " +
					$"Drift={gd.RebalanceDriftPercent:0.#}%");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",7} {"Tr/Te",9}   " +
				$"{"TunedTrn",9} {"TunedTst",9} {"DfltTst",9} {"Edge",7} {"Decay",7}");

			foreach (var r in rows)
			{
				Console.WriteLine(
					$"{r.Symbol,-8} {r.HistoricalVolatilityPct,7:0.0} {$"{r.TrainBars}/{r.TestBars}",9}   " +
					$"{r.TunedTrainSharpe,9:0.00} {r.TunedTestSharpe,9:0.00} {r.DefaultTestSharpe,9:0.00} " +
					$"{r.TuningEdge,7:+0.00;-0.00} {r.OverfitDecay,7:+0.00;-0.00}");
			}

			if (rows.Count > 0)
			{
				double meanTunedTest = rows.Average(r => r.TunedTestSharpe);
				double meanDefaultTest = rows.Average(r => r.DefaultTestSharpe);
				double meanEdge = rows.Average(r => r.TuningEdge);
				double meanDecay = rows.Average(r => r.OverfitDecay);
				int tunedWins = rows.Count(r => r.TuningEdge > 0);

				Console.WriteLine();
				Console.WriteLine($"Mean test Sharpe — per-symbol tuned : {meanTunedTest,6:0.00}");
				Console.WriteLine($"Mean test Sharpe — global default   : {meanDefaultTest,6:0.00}");
				Console.WriteLine($"Mean tuning edge (tuned - default)  : {meanEdge,6:+0.00;-0.00}   " +
				                  $"[tuned wins {tunedWins}/{rows.Count}]");
				Console.WriteLine($"Mean overfit decay (train - test)   : {meanDecay,6:+0.00;-0.00}");
				Console.WriteLine();
				Console.WriteLine(meanEdge > 0.10
					? "=> Per-symbol tuning ADDS value out-of-sample. Worth a selection method."
					: "=> Per-symbol tuning does NOT beat a fixed default out-of-sample => overfitting. Lock in one default.");
			}
		}

		// Rolling walk-forward: one row per fold, showing the re-tuned global default and its
		// out-of-sample result on the following test window. Consistently positive mean test
		// Sharpe => real edge; noise around zero => the strategy isn't clearing the bar.
		public static void PrintRolling(List<RollingFold> folds)
		{
			Console.WriteLine("\n===== ROLLING WALK-FORWARD (re-tune default each fold, score next window) =====");
			if (folds.Count == 0)
			{
				Console.WriteLine("No folds — not enough history for the configured train/test window.");
				return;
			}

			Console.WriteLine(
				$"{"Fold",4} {"Test window",23} {"Sym",4}   " +
				$"{"EMA",4} {"BiasP",6} {"LBias",6} {"BiasEMA",8} {"Drift%",7}   " +
				$"{"MeanShp",8} {"MedShp",7} {"Ret%",9} {"%Pos",6}");

			foreach (var f in folds)
			{
				string window = $"{f.TestStart:yyyy-MM-dd}..{f.TestEnd:yyyy-MM-dd}";
				Console.WriteLine(
					$"{f.Index,4} {window,23} {f.Symbols,4}   " +
					$"{f.Ema,4} {f.BiasP,6} {f.LongBias,6:0.##} {f.BiasEma,8} {f.Drift,7:0.#}   " +
					$"{f.MeanTestSharpe,8:0.00} {f.MedianTestSharpe,7:0.00} {Signed(f.MeanTestReturnPct),9} " +
					$"{f.PctPositive * 100.0,5:0.}%");
			}

			double meanShp = folds.Average(f => f.MeanTestSharpe);
			double medOfMeans = folds.Select(f => f.MeanTestSharpe).OrderBy(s => s).ToList() is var l && l.Count > 0
				? (l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2.0) : 0.0;
			int posFolds = folds.Count(f => f.MeanTestSharpe > 0);
			double sd = folds.Count > 1
				? Math.Sqrt(folds.Sum(f => Math.Pow(f.MeanTestSharpe - meanShp, 2)) / (folds.Count - 1)) : 0.0;

			Console.WriteLine();
			Console.WriteLine($"Folds                              : {folds.Count}");
			Console.WriteLine($"Mean OOS Sharpe across folds       : {meanShp,6:0.00}  (sd {sd:0.00})");
			Console.WriteLine($"Median OOS Sharpe across folds     : {medOfMeans,6:0.00}");
			Console.WriteLine($"Folds with positive mean Sharpe    : {posFolds}/{folds.Count}");
			Console.WriteLine();
			Console.WriteLine(meanShp > 0.30 && posFolds > folds.Count / 2
				? "=> OOS Sharpe is consistently positive => the strategy shows a real edge."
				: "=> OOS Sharpe hovers around zero / is inconsistent => no reliable edge on this basket.");
		}

		// Rolling walk-forward over the BUCKET-WEIGHT shape: one row per fold showing the
		// re-tuned map shape (Bull/Bear rows as [bottom..top]) and its OOS result.
		public static void PrintRollingBuckets(List<RollingFold> folds)
		{
			Console.WriteLine("\n===== ROLLING WALK-FORWARD — BUCKET WEIGHTS (re-tune map each fold) =====");
			if (folds.Count == 0)
			{
				Console.WriteLine("No folds — not enough history for the configured train/test window.");
				return;
			}

			Console.WriteLine(
				$"{"Fold",4} {"Test window",23} {"Sym",4}   " +
				$"{"LT Bull [bot..top]",18} {"LT Bear [bot..top]",18}   " +
				$"{"MeanShp",8} {"MedShp",7} {"Ret%",9} {"%Pos",6}");

			foreach (var f in folds)
			{
				string window = $"{f.TestStart:yyyy-MM-dd}..{f.TestEnd:yyyy-MM-dd}";
				var s = f.BucketShape;
				string bull = s == null ? "—" : $"[{s.BotBull,5:0.##} .. {s.TopBull,4:0.##}]";
				string bear = s == null ? "—" : $"[{s.BotBear,5:0.##} .. {s.TopBear,4:0.##}]";
				Console.WriteLine(
					$"{f.Index,4} {window,23} {f.Symbols,4}   " +
					$"{bull,18} {bear,18}   " +
					$"{f.MeanTestSharpe,8:0.00} {f.MedianTestSharpe,7:0.00} {Signed(f.MeanTestReturnPct),9} " +
					$"{f.PctPositive * 100.0,5:0.}%");
			}

			double meanShp = folds.Average(f => f.MeanTestSharpe);
			int posFolds = folds.Count(f => f.MeanTestSharpe > 0);
			double sd = folds.Count > 1
				? Math.Sqrt(folds.Sum(f => Math.Pow(f.MeanTestSharpe - meanShp, 2)) / (folds.Count - 1)) : 0.0;

			Console.WriteLine();
			Console.WriteLine($"Folds                              : {folds.Count}");
			Console.WriteLine($"Mean OOS Sharpe across folds       : {meanShp,6:0.00}  (sd {sd:0.00})");
			Console.WriteLine($"Folds with positive mean Sharpe    : {posFolds}/{folds.Count}");
			Console.WriteLine();
			Console.WriteLine(meanShp > 0.30 && posFolds > folds.Count / 2
				? "=> Bucket-weight tuning holds up OOS => the (LT,ST) map carries a real edge."
				: "=> OOS Sharpe still hovers around zero => the (LT,ST) map does not carry a tradeable edge.");
		}

		// Full-window strategy vs buy & hold: Sharpe, max drawdown, and return-per-drawdown,
		// side by side per symbol, with a verdict on whether the strategy is a net improvement.
		public static void PrintFullWindow(List<FullWindowRow> rows)
		{
			Console.WriteLine("\n===== FULL-WINDOW: STRATEGY vs BUY & HOLD (fixed params, no tuning) =====");
			if (rows.Count == 0)
			{
				Console.WriteLine("No symbols with usable data.");
				return;
			}
			Console.WriteLine("Positive = strategy better. (Sharpe: higher; MaxDD: lower/less negative; Ret/DD: higher.)");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",6} {"Bars",5}   " +
				$"{"Shp:Strat",9} {"BH",6} │ {"DD:Strat",8} {"BH",7} │ {"Ret/DD:S",8} {"BH",6}");

			foreach (var r in rows)
			{
				string sRd = double.IsNaN(r.StratRetPerDd) ? "  n/a" : $"{r.StratRetPerDd,6:0.00}";
				string bRd = double.IsNaN(r.BhRetPerDd)    ? "  n/a" : $"{r.BhRetPerDd,6:0.00}";
				Console.WriteLine(
					$"{r.Symbol,-8} {r.HistoricalVolatilityPct,6:0.0} {r.Bars,5}   " +
					$"{r.StratSharpe,9:0.00} {r.BhSharpe,6:0.00} │ " +
					$"-{r.StratMaxDd,7:0.0}% -{r.BhMaxDd,6:0.0}% │ {sRd} {bRd}");
			}

			int shpWins = rows.Count(r => r.StratSharpe > r.BhSharpe);
			int ddWins  = rows.Count(r => r.StratMaxDd < r.BhMaxDd);      // lower DD is better
			double mShpS = rows.Average(r => r.StratSharpe), mShpB = rows.Average(r => r.BhSharpe);
			double mDdS  = rows.Average(r => r.StratMaxDd),  mDdB  = rows.Average(r => r.BhMaxDd);

			Console.WriteLine();
			Console.WriteLine($"Mean Sharpe   — strategy {mShpS,6:0.00}   buy & hold {mShpB,6:0.00}   " +
			                  $"(strategy higher on {shpWins}/{rows.Count})");
			Console.WriteLine($"Mean Max DD   — strategy -{mDdS,5:0.0}%   buy & hold -{mDdB,5:0.0}%   " +
			                  $"(strategy lower on {ddWins}/{rows.Count})");
			Console.WriteLine();

			bool ddBetter  = mDdS < mDdB;
			bool shpBetter = mShpS > mShpB;
			Console.WriteLine(
				ddBetter && shpBetter ? "=> Strategy delivers BOTH lower drawdown and higher Sharpe: a useful risk overlay."
				: ddBetter            ? "=> Strategy cuts drawdown but does NOT raise Sharpe: risk-reducer, not alpha."
				: shpBetter           ? "=> Strategy raises Sharpe but not drawdown: unexpected; inspect per-symbol."
				:                       "=> Strategy improves NEITHER on average: no edge even as a risk overlay here.");
		}

		// Short-side A/B: strategy (Min 0% vs Min -100%) against the SAME buy & hold. Shows
		// whether allowing a real short improves the full-window risk/return.
		public static void PrintShortAb(List<FullWindowRow> noShort, List<FullWindowRow> withShort)
		{
			Console.WriteLine("\n===== SHORT-SIDE A/B: MinExposure 0% vs -100% (full window, fixed params) =====");
			Console.WriteLine("(Buy & hold is identical for both; only the strategy's short clamp changes.)");
			Console.WriteLine();
			Console.WriteLine($"{"Config",-22} {"MeanShp",8} {"MeanDD",8} {"MeanRet%",10} {"Shp>BH",8} {"DD<BH",7}");
			PrintAbRow("No short (Min 0%)", noShort);
			PrintAbRow("Full short (Min -100%)", withShort);

			double bh = noShort.Count > 0 ? noShort.Average(r => r.BhSharpe) : 0.0;
			Console.WriteLine($"{"Buy & hold",-22} {bh,8:0.00}");

			double sNo = noShort.Average(r => r.StratSharpe), sSh = withShort.Average(r => r.StratSharpe);
			double dNo = noShort.Average(r => r.StratMaxDd),  dSh = withShort.Average(r => r.StratMaxDd);
			Console.WriteLine();
			Console.WriteLine(
				sSh > sNo && dSh <= dNo + 2.0 ? "=> Allowing short IMPROVES Sharpe without materially worse drawdown => prefer -100%."
				: sSh > sNo                   ? "=> Short raises Sharpe but deepens drawdown => trade-off; depends on your objective."
				: dSh < dNo                   ? "=> Short lowers drawdown but not Sharpe => marginal; only if drawdown is the priority."
				:                               "=> Allowing short does NOT help (lower/equal Sharpe, no DD benefit) => keep Min 0% (no short).");
		}

		private static void PrintAbRow(string label, List<FullWindowRow> rows)
		{
			double mShp = rows.Average(r => r.StratSharpe);
			double mDd  = rows.Average(r => r.StratMaxDd);
			double mRet = rows.Average(r => r.StratReturn);
			int shpW = rows.Count(r => r.StratSharpe > r.BhSharpe);
			int ddW  = rows.Count(r => r.StratMaxDd  < r.BhMaxDd);
			Console.WriteLine(
				$"{label,-22} {mShp,8:0.00} -{mDd,6:0.0}% {Signed(mRet),10} " +
				$"{$"{shpW}/{rows.Count}",8} {$"{ddW}/{rows.Count}",7}");
		}

		// Volatility-threshold deployment sweep: aggregate strategy vs B&H over only the
		// symbols at/above each HV cutoff. Shows where the strategy's edge turns positive.
		public static void PrintVolThreshold(string label, List<VolThresholdBucket> buckets)
		{
			Console.WriteLine($"\n----- VOLATILITY-THRESHOLD DEPLOYMENT — {label} -----");
			Console.WriteLine(
				$"{"MinHV%",7} {"Sym",4}   {"StratShp",8} {"BhShp",7} {"ShpEdge",8}   " +
				$"{"StratDD",8} {"BhDD",7} {"DDcut",7}");
			foreach (var b in buckets)
			{
				Console.WriteLine(
					$"{b.MinHv,7:0.} {b.Symbols,4}   {b.MeanStratSharpe,8:0.00} {b.MeanBhSharpe,7:0.00} " +
					$"{b.SharpeEdge,8:+0.00;-0.00}   -{b.MeanStratDd,6:0.0}% -{b.MeanBhDd,6:0.0}% {b.DdReduction,6:0.0}%");
			}
		}

		// "Are the current knobs optimal?" — current combo vs. the full-grid distribution
		// over the deployment universe (HV-filtered), full window.
		public static void PrintKnobRank(KnobRankResult r)
		{
			Console.WriteLine("\n===== ARE THE CURRENT KNOBS OPTIMAL? =====");
			Console.WriteLine($"Universe : {r.Symbols} symbols with HV >= {r.HvThreshold:0.} (the deployment set), full window");
			if (r.Symbols == 0 || r.TotalCombos == 0)
			{
				Console.WriteLine("No symbols in the universe — lower KnobRankHvThreshold or widen the basket.");
				return;
			}
			Console.WriteLine();
			Console.WriteLine($"Current knobs : EMA={r.Ema} BiasP={r.BiasP} LBias={r.LongBias:0.##} " +
			                  $"BiasEMA={r.BiasEma} Drift={r.Drift:0.#}%");
			Console.WriteLine();
			Console.WriteLine($"{"",-26} {"MeanSharpe",11}");
			Console.WriteLine($"{"Current knobs",-26} {r.CurrentMeanSharpe,11:0.000}");
			Console.WriteLine($"{"Grid best (in-sample)",-26} {r.BestMeanSharpe,11:0.000}");
			Console.WriteLine($"{"Grid median",-26} {r.MedianMeanSharpe,11:0.000}");
			Console.WriteLine($"{"Grid worst",-26} {r.WorstMeanSharpe,11:0.000}");
			Console.WriteLine();
			Console.WriteLine($"Rank : {r.BetterThanCurrent} of {r.TotalCombos} combos beat current " +
			                  $"=> {r.Percentile:0.}th percentile");
			if (r.Best != null)
				Console.WriteLine($"Grid-best knobs (overfit) : EMA={r.Best.ExposureEmaPeriod} BiasP={r.Best.BiasPeriod} " +
				                  $"LBias={r.Best.LongBias:0.##} BiasEMA={r.Best.BiasEmaPeriod} Drift={r.Best.RebalanceDriftPercent:0.#}%");

			double headroom = r.BestMeanSharpe - r.CurrentMeanSharpe;
			Console.WriteLine();
			Console.WriteLine(
				r.Percentile >= 75 ? $"=> Current knobs are already near-optimal ({r.Percentile:0.}th pct). Best is only +{headroom:0.00} Sharpe better AND that best is in-sample/overfit (shown not to survive OOS). Leave them."
				: r.Percentile >= 40 ? $"=> Current knobs are middling ({r.Percentile:0.}th pct), +{headroom:0.00} below the (overfit) best. Since tuning doesn't survive OOS, not worth chasing — but they're not a bad choice."
				: $"=> Current knobs are in the bottom of the grid ({r.Percentile:0.}th pct). A better-behaved region exists; consider moving toward the grid median values, but do NOT fit to the in-sample best.");
		}

		// BiasPeriod x BiasEmaPeriod sweep: Sharpe and drawdown matrices (rows = BiasPeriod,
		// cols = Bias EMA) over the deployment universe, with the current cell marked [*] and
		// the smallest performance-preserving pair recommended.
		public static void PrintBiasSweep(BiasSweepResult r)
		{
			Console.WriteLine("\n===== BIAS-PERIOD x BIAS-EMA SWEEP (other knobs fixed) =====");
			Console.WriteLine($"Universe : {r.Symbols} symbols with HV >= {r.HvThreshold:0.} (deployment set), full window");
			if (r.Symbols == 0 || r.Cells.Count == 0)
			{
				Console.WriteLine("No symbols in the universe.");
				return;
			}
			Console.WriteLine($"Current  : BiasPeriod={r.CurBiasPeriod}, BiasEMA={r.CurBiasEmaPeriod}  " +
			                  $"=> Sharpe {r.CurSharpe:0.000}, MaxDD -{r.CurMaxDd:0.0}%   [* marks current]");

			double Get(int bp, int be, bool sharpe)
			{
				var c = r.Cells.First(x => x.BiasPeriod == bp && x.BiasEmaPeriod == be);
				return sharpe ? c.MeanSharpe : c.MeanMaxDd;
			}

			void Matrix(string title, bool sharpe)
			{
				Console.WriteLine($"\n{title}   (rows = BiasPeriod, cols = Bias EMA)");
				Console.Write($"{"BiasP\\EMA",-9}");
				foreach (var be in r.BiasEmaPeriods) Console.Write($"{be,9}");
				Console.WriteLine();
				foreach (var bp in r.BiasPeriods)
				{
					Console.Write($"{bp,-9}");
					foreach (var be in r.BiasEmaPeriods)
					{
						string mark = bp == r.CurBiasPeriod && be == r.CurBiasEmaPeriod ? "*" : " ";
						string val = sharpe ? $"{Get(bp, be, true):0.000}" : $"-{Get(bp, be, false):0.0}%";
						Console.Write($"{val + mark,9}");
					}
					Console.WriteLine();
				}
			}

			Matrix("Mean Sharpe", true);
			Matrix("Mean Max Drawdown", false);

			Console.WriteLine();
			if (r.Recommended != null)
			{
				var rec = r.Recommended;
				bool smaller = rec.BiasPeriod + rec.BiasEmaPeriod < r.CurBiasPeriod + r.CurBiasEmaPeriod;
				Console.WriteLine($"Recommended smaller pair : BiasPeriod={rec.BiasPeriod}, BiasEMA={rec.BiasEmaPeriod}  " +
				                  $"=> Sharpe {rec.MeanSharpe:0.000} (vs {r.CurSharpe:0.000}), MaxDD -{rec.MeanMaxDd:0.0}% (vs -{r.CurMaxDd:0.0}%)");
				Console.WriteLine(smaller
					? $"=> Holds performance with smaller windows. Update Program.cs: BiasPeriod={rec.BiasPeriod}, BiasEmaPeriod={rec.BiasEmaPeriod}."
					: "=> No smaller pair holds performance within tolerance; current values are already about as low as you can go.");
			}
			else
			{
				Console.WriteLine("No pair within tolerance — loosen BiasSweepSharpeTol/BiasSweepDdTolPp or accept current values.");
			}
		}

		// Pearson correlation coefficient; returns 0 when either series has no variance.
		private static double Corr(IEnumerable<double> xs, IEnumerable<double> ys)
		{
			var x = xs.ToList();
			var y = ys.ToList();
			int n = Math.Min(x.Count, y.Count);
			if (n < 2) return 0.0;

			double mx = x.Take(n).Average();
			double my = y.Take(n).Average();
			double sxy = 0, sxx = 0, syy = 0;
			for (int i = 0; i < n; i++)
			{
				double dx = x[i] - mx, dy = y[i] - my;
				sxy += dx * dy;
				sxx += dx * dx;
				syy += dy * dy;
			}
			double denom = Math.Sqrt(sxx * syy);
			return denom > 0 ? sxy / denom : 0.0;
		}

		private static string Signed(double v)
		{
			double x = Math.Round(v, 2);
			return (x >= 0 ? "+" : "-") + Math.Abs(x).ToString("0.00");
		}
	}
}
