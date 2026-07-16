using System;
using System.Collections.Generic;
using System.Linq;

namespace StockOdds
{
	// One evaluated combination of the smoothing knobs plus the metrics it produced.
	public class GridPoint
	{
		public int    ExposureEmaPeriod;
		public int    BiasPeriod;
		public double LongBias;
		public int    BiasEmaPeriod;
		public double RebalanceDriftPercent;

		public double Sharpe;          // objective
		public double TotalReturnPct;
		public double MaxDrawdownPct;
	}

	// The robust optimum for a single symbol, tagged with its historical volatility — one
	// row of the volatility→parameters study. Instead of the single best combo (noisy),
	// the knobs are AVERAGED over the top region (best RobustTopFraction of combos), so a
	// real HV relationship survives and a lucky single pick washes out. The averaged knobs
	// are fractional — this is a diagnostic, not a config to paste back verbatim.
	public class SymbolOptimum
	{
		public string Symbol = "";
		public double HistoricalVolatilityPct;
		public int    Bars;

		public double EmaPeriod;         // averages over the top region
		public double BiasPeriod;
		public double LongBias;
		public double BiasEmaPeriod;
		public double DriftPercent;

		public int    RegionCount;       // how many combos were averaged
		public double TopSharpe;         // single best Sharpe (region max)
		public double RegionMinSharpe;   // worst Sharpe inside the region
		public double MeanRegionReturnPct;
		public double MeanRegionMaxDdPct;

		// "do the knobs even matter?" — spread of Sharpe across the ENTIRE grid.
		public double GridMedianSharpe;
		public double GridWorstSharpe;
		public double SharpeSpread => TopSharpe - GridMedianSharpe;   // best vs typical
	}

	// The same knob combination scored across a basket of symbols. Ranked by MeanSharpe
	// (generalization) with MinSharpe shown alongside so a combo that only works on one
	// symbol is easy to spot.
	public class MultiGridPoint
	{
		public int    ExposureEmaPeriod;
		public int    BiasPeriod;
		public double LongBias;
		public int    BiasEmaPeriod;
		public double RebalanceDriftPercent;

		public double MeanSharpe;              // objective (average across symbols)
		public double MinSharpe;               // worst symbol — consistency check
		public double MeanReturnPct;
		public double MeanMaxDrawdownPct;
		public Dictionary<string, double> SharpeBySymbol = new();
	}

	// Brute-force sweep of the BankrollSimulator smoothing knobs, scored by annualized
	// Sharpe. The (LT, ST) bucket weights and the Min/Max exposure clamps are left at
	// whatever the caller configured -- only the five smoothing knobs move here.
	//
	// NOTE: the simulator keeps its parameters in static fields, so this runs
	// sequentially and restores the originals when done. The search space is small
	// enough (a few thousand runs, each a single pass over the bars) that this is fast.
	public static class GridSearch
	{
		// Candidate values per knob. Override any of these from Program.cs before Run().
		public static int[]    ExposureEmaPeriods     = { 3, 5, 8, 12, 20 };
		public static int[]    BiasPeriods            = { 5, 10, 20, 40 };
		public static double[] LongBiases             = { 0.0, 0.5, 1.0, 2.0 };
		public static int[]    BiasEmaPeriods         = { 20, 50, 100, 200 };
		public static double[] RebalanceDriftPercents = { 0.0, 10.0, 20.0, 30.0 };

		// Run the sim on a bar set with a specific knob combination, restoring the caller's
		// knobs afterward. Used by the walk-forward test to score a fixed combo on new data.
		public static BankrollResult RunWith(
			List<OhlcBar> bars, int ema, int biasP, double longBias, int biasEma, double drift,
			double initialBankroll = 10_000.0)
		{
			int    sEma      = BankrollSimulator.ExposureEmaPeriod;
			int    sBiasP    = BankrollSimulator.BiasPeriod;
			double sLongBias = BankrollSimulator.LongBias;
			int    sBiasEma  = BankrollSimulator.BiasEmaPeriod;
			double sDrift    = BankrollSimulator.RebalanceDriftPercent;
			try
			{
				BankrollSimulator.ExposureEmaPeriod     = ema;
				BankrollSimulator.BiasPeriod            = biasP;
				BankrollSimulator.LongBias              = longBias;
				BankrollSimulator.BiasEmaPeriod         = biasEma;
				BankrollSimulator.RebalanceDriftPercent = drift;
				return BankrollSimulator.Run(bars, initialBankroll);
			}
			finally
			{
				BankrollSimulator.ExposureEmaPeriod     = sEma;
				BankrollSimulator.BiasPeriod            = sBiasP;
				BankrollSimulator.LongBias              = sLongBias;
				BankrollSimulator.BiasEmaPeriod         = sBiasEma;
				BankrollSimulator.RebalanceDriftPercent = sDrift;
			}
		}

		// 2-D sweep of BiasPeriod x BiasEmaPeriod (all other knobs held at their current
		// values), scored by mean Sharpe / drawdown across the deployment universe over the
		// full window. Finds the smallest pair that maintains current performance.
		public static int[] BiasPeriodCandidates = { 5, 10, 15, 20, 30, 40 };
		public static int[] BiasEmaCandidates    = { 20, 50, 100, 150, 200 };
		public static double BiasSweepHvThreshold = 50.0;
		public static double BiasSweepSharpeTol   = 0.05;   // allowed Sharpe give-up vs current
		public static double BiasSweepDdTolPp     = 2.0;    // allowed extra drawdown (pct pts)

		public static BiasSweepResult BiasSweep(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			int    curEma   = BankrollSimulator.ExposureEmaPeriod;
			int    curBiasP = BankrollSimulator.BiasPeriod;
			double curLBias = BankrollSimulator.LongBias;
			int    curBEma  = BankrollSimulator.BiasEmaPeriod;
			double curDrift = BankrollSimulator.RebalanceDriftPercent;

			var universe = barsBySymbol
				.Where(kv => Volatility.AnnualizedHistoricalPct(kv.Value) >= BiasSweepHvThreshold)
				.ToDictionary(kv => kv.Key, kv => kv.Value);

			var result = new BiasSweepResult
			{
				HvThreshold = BiasSweepHvThreshold, Symbols = universe.Count,
				CurBiasPeriod = curBiasP, CurBiasEmaPeriod = curBEma,
				BiasPeriods = BiasPeriodCandidates, BiasEmaPeriods = BiasEmaCandidates,
			};
			if (universe.Count == 0)
				return result;

			// evaluate one (biasP, biasEma) pair across the universe, other knobs fixed
			(double sh, double dd) Eval(int biasP, int biasEma)
			{
				var shs = new List<double>();
				var dds = new List<double>();
				foreach (var b in universe.Values)
				{
					var r = RunWith(b, curEma, biasP, curLBias, biasEma, curDrift, initialBankroll);
					shs.Add(SharpeOf(r));
					dds.Add(r.MaxDrawdownPct);
				}
				return (shs.Average(), dds.Average());
			}

			var (curSh, curDd) = Eval(curBiasP, curBEma);
			result.CurSharpe = curSh;
			result.CurMaxDd  = curDd;

			foreach (var bp in BiasPeriodCandidates)
			foreach (var be in BiasEmaCandidates)
			{
				var (sh, dd) = Eval(bp, be);
				result.Cells.Add(new BiasSweepCell { BiasPeriod = bp, BiasEmaPeriod = be, MeanSharpe = sh, MeanMaxDd = dd });
			}

			// smallest pair (min BiasPeriod+BiasEmaPeriod) that keeps Sharpe within tol and
			// drawdown no worse than tol; tie-break toward the smaller Bias EMA.
			result.Recommended = result.Cells
				.Where(c => c.MeanSharpe >= curSh - BiasSweepSharpeTol && c.MeanMaxDd <= curDd + BiasSweepDdTolPp)
				.OrderBy(c => c.BiasPeriod + c.BiasEmaPeriod)
				.ThenBy(c => c.BiasEmaPeriod)
				.FirstOrDefault();

			return result;
		}

		// Where do the CURRENTLY configured smoothing knobs rank among the full grid, scored
		// by mean Sharpe across a universe (optionally HV-filtered) over the full window?
		// Answers "are the current knobs optimal?" with the whole distribution for context.
		public static double KnobRankHvThreshold = 50.0;

		public static KnobRankResult KnobRank(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			// capture the current knobs BEFORE any sweep mutates the statics
			int    curEma   = BankrollSimulator.ExposureEmaPeriod;
			int    curBiasP = BankrollSimulator.BiasPeriod;
			double curLBias = BankrollSimulator.LongBias;
			int    curBEma  = BankrollSimulator.BiasEmaPeriod;
			double curDrift = BankrollSimulator.RebalanceDriftPercent;

			var universe = barsBySymbol
				.Where(kv => Volatility.AnnualizedHistoricalPct(kv.Value) >= KnobRankHvThreshold)
				.ToDictionary(kv => kv.Key, kv => kv.Value);

			var result = new KnobRankResult
			{
				HvThreshold = KnobRankHvThreshold, Symbols = universe.Count,
				Ema = curEma, BiasP = curBiasP, LongBias = curLBias, BiasEma = curBEma, Drift = curDrift,
			};
			if (universe.Count == 0)
				return result;

			// current combo's mean Sharpe across the universe (full window)
			var curSharpes = universe.Values
				.Select(b => SharpeOf(RunWith(b, curEma, curBiasP, curLBias, curBEma, curDrift, initialBankroll)))
				.ToList();
			double curMean = curSharpes.Average();

			// full grid distribution (mean Sharpe across the same universe)
			var ranked = RunMulti(universe, initialBankroll);
			var means = ranked.Select(p => p.MeanSharpe).OrderBy(x => x).ToList();
			double median = means.Count % 2 == 1 ? means[means.Count / 2]
				: (means[means.Count / 2 - 1] + means[means.Count / 2]) / 2.0;

			result.CurrentMeanSharpe = curMean;
			result.BetterThanCurrent = ranked.Count(p => p.MeanSharpe > curMean);
			result.TotalCombos       = ranked.Count;
			result.BestMeanSharpe    = ranked.First().MeanSharpe;
			result.MedianMeanSharpe  = median;
			result.WorstMeanSharpe   = means.First();
			result.Best              = ranked.First();
			return result;
		}

		public static long GridSize =>
			(long)ExposureEmaPeriods.Length * BiasPeriods.Length * LongBiases.Length *
			BiasEmaPeriods.Length * RebalanceDriftPercents.Length;

		public static List<GridPoint> Run(List<OhlcBar> bars, double initialBankroll = 10_000.0)
		{
			// snapshot the knobs we mutate so the caller's config survives the search
			int    savedEma       = BankrollSimulator.ExposureEmaPeriod;
			int    savedBiasP     = BankrollSimulator.BiasPeriod;
			double savedLongBias  = BankrollSimulator.LongBias;
			int    savedBiasEma   = BankrollSimulator.BiasEmaPeriod;
			double savedDrift     = BankrollSimulator.RebalanceDriftPercent;

			var results = new List<GridPoint>((int)Math.Min(GridSize, int.MaxValue));

			try
			{
				foreach (var ema in ExposureEmaPeriods)
				foreach (var biasP in BiasPeriods)
				foreach (var longBias in LongBiases)
				foreach (var biasEma in BiasEmaPeriods)
				foreach (var drift in RebalanceDriftPercents)
				{
					BankrollSimulator.ExposureEmaPeriod     = ema;
					BankrollSimulator.BiasPeriod            = biasP;
					BankrollSimulator.LongBias              = longBias;
					BankrollSimulator.BiasEmaPeriod         = biasEma;
					BankrollSimulator.RebalanceDriftPercent = drift;

					var r = BankrollSimulator.Run(bars, initialBankroll);

					results.Add(new GridPoint
					{
						ExposureEmaPeriod     = ema,
						BiasPeriod            = biasP,
						LongBias              = longBias,
						BiasEmaPeriod         = biasEma,
						RebalanceDriftPercent = drift,
						Sharpe                = r.SharpeRatio,
						TotalReturnPct        = r.TotalReturnPct,
						MaxDrawdownPct        = r.MaxDrawdownPct,
					});
				}
			}
			finally
			{
				// always restore, even if a run throws
				BankrollSimulator.ExposureEmaPeriod     = savedEma;
				BankrollSimulator.BiasPeriod            = savedBiasP;
				BankrollSimulator.LongBias              = savedLongBias;
				BankrollSimulator.BiasEmaPeriod         = savedBiasEma;
				BankrollSimulator.RebalanceDriftPercent = savedDrift;
			}

			// best Sharpe first (ignore NaN so degenerate combos sink to the bottom)
			return results
				.OrderByDescending(p => double.IsNaN(p.Sharpe) ? double.NegativeInfinity : p.Sharpe)
				.ToList();
		}

		// Fraction of the ranked combos treated as the "top region" whose knobs get
		// averaged into each symbol's robust optimum. 0.10 = top 10% by Sharpe.
		public static double RobustTopFraction = 0.10;

		// Volatility study: for each symbol, run its own single-symbol grid search, then
		// AVERAGE the knobs over the top RobustTopFraction of combos (by Sharpe) to get a
		// robust optimum, and tag it with the symbol's historical volatility. The rows,
		// sorted by HV, are the data for whether optimal smoothing scales with volatility.
		public static List<SymbolOptimum> RunPerSymbol(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var optima = new List<SymbolOptimum>();

			foreach (var (symbol, bars) in barsBySymbol)
			{
				var ranked = Run(bars, initialBankroll);   // sorted desc by Sharpe, restores statics
				if (ranked.Count == 0)
					continue;

				int take = Math.Max(1, (int)Math.Round(ranked.Count * RobustTopFraction));
				var region = ranked.Take(take).ToList();

				// full-grid Sharpe distribution (NaN excluded) for the "do knobs matter?" spread
				var allSharpes = ranked.Select(p => p.Sharpe)
					.Where(s => !double.IsNaN(s)).OrderBy(s => s).ToList();
				double median = allSharpes.Count == 0 ? 0.0
					: allSharpes.Count % 2 == 1 ? allSharpes[allSharpes.Count / 2]
					: (allSharpes[allSharpes.Count / 2 - 1] + allSharpes[allSharpes.Count / 2]) / 2.0;

				optima.Add(new SymbolOptimum
				{
					Symbol                  = symbol,
					HistoricalVolatilityPct = Volatility.AnnualizedHistoricalPct(bars),
					Bars                    = bars.Count,

					EmaPeriod           = region.Average(p => (double)p.ExposureEmaPeriod),
					BiasPeriod          = region.Average(p => (double)p.BiasPeriod),
					LongBias            = region.Average(p => p.LongBias),
					BiasEmaPeriod       = region.Average(p => (double)p.BiasEmaPeriod),
					DriftPercent        = region.Average(p => p.RebalanceDriftPercent),

					RegionCount         = region.Count,
					TopSharpe           = region.First().Sharpe,
					RegionMinSharpe     = region.Min(p => p.Sharpe),
					MeanRegionReturnPct = region.Average(p => p.TotalReturnPct),
					MeanRegionMaxDdPct  = region.Average(p => p.MaxDrawdownPct),

					GridMedianSharpe    = median,
					GridWorstSharpe     = allSharpes.Count > 0 ? allSharpes[0] : 0.0,
				});
			}

			return optima
				.OrderBy(o => o.HistoricalVolatilityPct)
				.ToList();
		}

		// Same sweep, but each combination is scored on every symbol in the basket and
		// ranked by the MEAN Sharpe across them, so the winning knobs are the ones that
		// generalize rather than fit a single symbol's path.
		public static List<MultiGridPoint> RunMulti(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			int    savedEma       = BankrollSimulator.ExposureEmaPeriod;
			int    savedBiasP     = BankrollSimulator.BiasPeriod;
			double savedLongBias  = BankrollSimulator.LongBias;
			int    savedBiasEma   = BankrollSimulator.BiasEmaPeriod;
			double savedDrift     = BankrollSimulator.RebalanceDriftPercent;

			var results = new List<MultiGridPoint>((int)Math.Min(GridSize, int.MaxValue));

			try
			{
				foreach (var ema in ExposureEmaPeriods)
				foreach (var biasP in BiasPeriods)
				foreach (var longBias in LongBiases)
				foreach (var biasEma in BiasEmaPeriods)
				foreach (var drift in RebalanceDriftPercents)
				{
					BankrollSimulator.ExposureEmaPeriod     = ema;
					BankrollSimulator.BiasPeriod            = biasP;
					BankrollSimulator.LongBias              = longBias;
					BankrollSimulator.BiasEmaPeriod         = biasEma;
					BankrollSimulator.RebalanceDriftPercent = drift;

					var point = new MultiGridPoint
					{
						ExposureEmaPeriod     = ema,
						BiasPeriod            = biasP,
						LongBias              = longBias,
						BiasEmaPeriod         = biasEma,
						RebalanceDriftPercent = drift,
					};

					var sharpes = new List<double>();
					var returns = new List<double>();
					var drawdowns = new List<double>();

					foreach (var (symbol, bars) in barsBySymbol)
					{
						var r = BankrollSimulator.Run(bars, initialBankroll);
						double sh = double.IsNaN(r.SharpeRatio) ? 0.0 : r.SharpeRatio;
						point.SharpeBySymbol[symbol] = sh;
						sharpes.Add(sh);
						returns.Add(r.TotalReturnPct);
						drawdowns.Add(r.MaxDrawdownPct);
					}

					point.MeanSharpe         = sharpes.Count > 0 ? sharpes.Average() : 0.0;
					point.MinSharpe          = sharpes.Count > 0 ? sharpes.Min() : 0.0;
					point.MeanReturnPct      = returns.Count > 0 ? returns.Average() : 0.0;
					point.MeanMaxDrawdownPct = drawdowns.Count > 0 ? drawdowns.Average() : 0.0;
					results.Add(point);
				}
			}
			finally
			{
				BankrollSimulator.ExposureEmaPeriod     = savedEma;
				BankrollSimulator.BiasPeriod            = savedBiasP;
				BankrollSimulator.LongBias              = savedLongBias;
				BankrollSimulator.BiasEmaPeriod         = savedBiasEma;
				BankrollSimulator.RebalanceDriftPercent = savedDrift;
			}

			return results
				.OrderByDescending(p => double.IsNaN(p.MeanSharpe) ? double.NegativeInfinity : p.MeanSharpe)
				.ToList();
		}

		// ===================== LT-TRANSITION CHOP-PENALTY SWEEP =====================
		// Sweeps the chop penalty (TransitionPenalty x TransitionPeriod) across the basket,
		// holding EVERY other knob at whatever the caller configured. Penalty 0 is the
		// baseline (period is irrelevant there) and is emitted once for reference, so the
		// table answers "does penalizing choppy names improve the basket, and by how much?".
		// Penalty grids per measure. LtTransitions' chopFrac is small (few flips / window) so
		// it needs large penalties to bite; ExposureEfficiency's chopFrac is 1-ER (typically
		// 0.5..0.9) so much smaller penalties are the right scale.
		public static double[] TransitionPenaltyGrid = { 1.0, 2.0, 5.0, 10.0, 20.0 };
		public static double[] EfficiencyPenaltyGrid = { 0.25, 0.5, 1.0, 1.5, 2.0 };
		public static int[]    TransitionPeriodGrid  = { 60, 120, 250, 500, 750 };

		public static List<TransitionSweepCell> TransitionSweep(
			Dictionary<string, List<OhlcBar>> barsBySymbol,
			BankrollSimulator.ChopMeasure measure = BankrollSimulator.ChopMeasure.LtTransitions,
			double[]? penalties = null,
			double initialBankroll = 10_000.0)
		{
			penalties ??= measure == BankrollSimulator.ChopMeasure.ExposureEfficiency
				? EfficiencyPenaltyGrid : TransitionPenaltyGrid;

			var savedMode = BankrollSimulator.ChopMeasureMode;
			double savedPen = BankrollSimulator.TransitionPenalty;
			int    savedPer = BankrollSimulator.TransitionPeriod;

			var cells = new List<TransitionSweepCell>();
			try
			{
				BankrollSimulator.ChopMeasureMode = measure;

				// baseline: penalty off (period irrelevant, reuse the caller's value as a tag)
				cells.Add(EvalTransition(barsBySymbol, 0.0, savedPer, initialBankroll, isBaseline: true));

				foreach (var per in TransitionPeriodGrid)
				foreach (var pen in penalties)
					cells.Add(EvalTransition(barsBySymbol, pen, per, initialBankroll, isBaseline: false));
			}
			finally
			{
				BankrollSimulator.ChopMeasureMode   = savedMode;
				BankrollSimulator.TransitionPenalty = savedPen;
				BankrollSimulator.TransitionPeriod  = savedPer;
			}
			return cells;
		}

		private static TransitionSweepCell EvalTransition(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double pen, int per,
			double initialBankroll, bool isBaseline)
		{
			BankrollSimulator.TransitionPenalty = pen;
			BankrollSimulator.TransitionPeriod  = per;

			var cell = new TransitionSweepCell { Penalty = pen, Period = per, IsBaseline = isBaseline };
			var sharpes = new List<double>();
			var returns = new List<double>();
			var dds     = new List<double>();
			foreach (var (sym, bars) in barsBySymbol)
			{
				var r  = BankrollSimulator.Run(bars, initialBankroll);
				double sh = SharpeOf(r);
				cell.SharpeBySymbol[sym] = sh;
				sharpes.Add(sh);
				returns.Add(r.TotalReturnPct);
				dds.Add(r.MaxDrawdownPct);
			}
			cell.MeanSharpe    = sharpes.Count > 0 ? sharpes.Average() : 0.0;
			cell.MinSharpe     = sharpes.Count > 0 ? sharpes.Min() : 0.0;
			cell.MeanReturnPct = returns.Count > 0 ? returns.Average() : 0.0;
			cell.MeanMaxDdPct  = dds.Count > 0 ? dds.Average() : 0.0;
			return cell;
		}

		// ===================== TRADABILITY STUDY =====================
		// Tests the hypothesis "a stock whose exposure trends for long periods is tradable;
		// one whose exposure round-trips quickly is not." For each symbol (penalty OFF), it
		// records the mean rolling exposure-EMA efficiency ratio (persistence) alongside the
		// strategy's Sharpe/return, then the caller correlates the two. If persistence
		// predicts Sharpe, it is a stock SCREEN (like the HV/volume filter), not a bias knob.
		public static List<TradabilityRow> TradabilityStudy(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var savedMode = BankrollSimulator.ChopMeasureMode;
			double savedPen = BankrollSimulator.TransitionPenalty;
			var rows = new List<TradabilityRow>();
			try
			{
				BankrollSimulator.ChopMeasureMode   = BankrollSimulator.ChopMeasure.LtTransitions;
				BankrollSimulator.TransitionPenalty = 0.0;   // measure the un-penalized engine
				foreach (var (sym, bars) in barsBySymbol)
				{
					var r = BankrollSimulator.Run(bars, initialBankroll);
					rows.Add(new TradabilityRow
					{
						Symbol                  = sym,
						Bars                    = bars.Count,
						HistoricalVolatilityPct = Volatility.AnnualizedHistoricalPct(bars),
						ExposureEfficiency      = r.MeanExposureEfficiency,
						StratSharpe             = SharpeOf(r),
						StratReturnPct          = r.TotalReturnPct,
						BhReturnPct             = r.BuyHoldReturnPct,
					});
				}
			}
			finally
			{
				BankrollSimulator.ChopMeasureMode   = savedMode;
				BankrollSimulator.TransitionPenalty = savedPen;
			}
			return rows.OrderByDescending(x => x.ExposureEfficiency).ToList();
		}

		// One symbol's walk-forward outcome: tuned on the train slice, then both the tuned
		// knobs and the global-default knobs scored on the held-out test slice.
		public static double WalkForwardTrainFraction = 0.70;

		public static List<WalkForwardRow> WalkForward(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			// split each symbol into train (first fraction) / test (remainder); need >= 3
			// bars on each side for the engine to produce a state.
			var train = new Dictionary<string, List<OhlcBar>>();
			var test  = new Dictionary<string, List<OhlcBar>>();
			foreach (var (sym, bars) in barsBySymbol)
			{
				int split = (int)(bars.Count * WalkForwardTrainFraction);
				if (split < 3 || bars.Count - split < 3)
					continue;
				train[sym] = bars.Take(split).ToList();
				test[sym]  = bars.Skip(split).ToList();
			}

			// Global default = the single knob combo with the best MEAN Sharpe across ALL
			// train slices. This is the strong "one setting for everything" baseline.
			var globalRanked = RunMulti(train, initialBankroll);
			var gd = globalRanked.FirstOrDefault();

			var rows = new List<WalkForwardRow>();
			foreach (var (sym, trBars) in train)
			{
				var teBars = test[sym];

				// per-symbol tuning: best combo on this symbol's train slice
				var tuned = Run(trBars, initialBankroll).First();

				double tunedTest = gd == null ? 0.0
					: SharpeOf(RunWith(teBars, tuned.ExposureEmaPeriod, tuned.BiasPeriod,
						tuned.LongBias, tuned.BiasEmaPeriod, tuned.RebalanceDriftPercent, initialBankroll));
				double defaultTest = gd == null ? 0.0
					: SharpeOf(RunWith(teBars, gd.ExposureEmaPeriod, gd.BiasPeriod,
						gd.LongBias, gd.BiasEmaPeriod, gd.RebalanceDriftPercent, initialBankroll));

				rows.Add(new WalkForwardRow
				{
					Symbol                  = sym,
					HistoricalVolatilityPct = Volatility.AnnualizedHistoricalPct(barsBySymbol[sym]),
					TrainBars               = trBars.Count,
					TestBars                = teBars.Count,
					TunedKnobs              = tuned,
					TunedTrainSharpe        = tuned.Sharpe,
					TunedTestSharpe         = tunedTest,
					DefaultTestSharpe       = defaultTest,
				});
			}

			// stash the global default on every row so the printer can report it once
			foreach (var row in rows)
				row.GlobalDefault = gd;

			return rows.OrderBy(r => r.HistoricalVolatilityPct).ToList();
		}

		private static double SharpeOf(BankrollResult r) =>
			double.IsNaN(r.SharpeRatio) ? 0.0 : r.SharpeRatio;

		// Strategy vs buy & hold over each symbol's full window, using the parameters exactly
		// as currently configured on BankrollSimulator (no tuning). Sorted by volatility.
		public static List<FullWindowRow> FullWindowCompare(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var rows = new List<FullWindowRow>();
			foreach (var (sym, bars) in barsBySymbol)
			{
				var r = BankrollSimulator.Run(bars, initialBankroll);
				rows.Add(new FullWindowRow
				{
					Symbol                  = sym,
					Bars                    = bars.Count,
					HistoricalVolatilityPct = Volatility.AnnualizedHistoricalPct(bars),
					StratSharpe = SharpeOf(r),          StratMaxDd = r.MaxDrawdownPct,        StratReturn = r.TotalReturnPct,
					BhSharpe    = double.IsNaN(r.BuyHoldSharpeRatio) ? 0.0 : r.BuyHoldSharpeRatio,
					BhMaxDd     = r.BuyHoldMaxDrawdownPct, BhReturn = r.BuyHoldReturnPct,
				});
			}
			return rows.OrderBy(r => r.HistoricalVolatilityPct).ToList();
		}

		// Full-window comparison with a specific MinExposurePercent (short clamp), restoring
		// the caller's value afterward. Min 0 = no short; Min -100 = allow fully short.
		public static List<FullWindowRow> FullWindowCompareWithMin(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double minExposurePct,
			double initialBankroll = 10_000.0)
		{
			double saved = BankrollSimulator.MinExposurePercent;
			try
			{
				BankrollSimulator.MinExposurePercent = minExposurePct;
				return FullWindowCompare(barsBySymbol, initialBankroll);
			}
			finally { BankrollSimulator.MinExposurePercent = saved; }
		}

		// Aggregate strategy-vs-B&H over only the symbols at/above each HV threshold, to find
		// the volatility cutoff above which deploying the strategy is worthwhile.
		public static List<VolThresholdBucket> VolThresholdSweep(
			List<FullWindowRow> rows, double[] thresholds)
		{
			var res = new List<VolThresholdBucket>();
			foreach (var t in thresholds)
			{
				var sub = rows.Where(r => r.HistoricalVolatilityPct >= t).ToList();
				if (sub.Count == 0)
					continue;
				res.Add(new VolThresholdBucket
				{
					MinHv           = t,
					Symbols         = sub.Count,
					MeanStratSharpe = sub.Average(r => r.StratSharpe),
					MeanBhSharpe    = sub.Average(r => r.BhSharpe),
					MeanStratDd     = sub.Average(r => r.StratMaxDd),
					MeanBhDd        = sub.Average(r => r.BhMaxDd),
					MeanStratReturn = sub.Average(r => r.StratReturn),
					MeanBhReturn    = sub.Average(r => r.BhReturn),
				});
			}
			return res;
		}

		// ---- Rolling walk-forward ----
		// Slides a (train -> test) window through history. Each fold re-derives the global
		// default (best mean-Sharpe combo across that fold's train slices) and scores it on
		// the immediately following test slice. If the default's out-of-sample Sharpe is
		// consistently positive across folds, the strategy has a real edge; if it's noise
		// around zero, the earlier single-split near-zero result was not just a bad period.
		//
		// Windows are defined by DATE (taken from the longest-history symbol as a calendar),
		// so late-listed symbols simply contribute fewer bars or drop out of a fold rather
		// than misaligning by index.
		public static int RollTrainBars = 378;   // ~1.5y train
		public static int RollTestBars  = 63;    // ~1 quarter test
		public static int RollStepBars  = 63;    // advance one quarter per fold

		// One rolling (train -> test) window, with each symbol's bars sliced into it.
		private class RollWindow
		{
			public int      Index;
			public DateTime TestStart;
			public DateTime TestEndLabel;   // date of the last actual test bar (for display)
			public Dictionary<string, List<OhlcBar>> Train = new();
			public Dictionary<string, List<OhlcBar>> Test  = new();
		}

		// Build date-based rolling windows off the longest-history symbol as a calendar, so
		// late-listed symbols contribute fewer bars or drop out rather than misaligning.
		private static List<RollWindow> BuildRollingWindows(
			Dictionary<string, List<OhlcBar>> barsBySymbol)
		{
			var windows = new List<RollWindow>();
			if (barsBySymbol.Count == 0)
				return windows;

			var reference = barsBySymbol.Values.OrderByDescending(b => b.Count).First();
			int foldIdx = 0;
			for (int start = 0;
			     start + RollTrainBars + RollTestBars <= reference.Count;
			     start += RollStepBars)
			{
				DateTime trStart = reference[start].Date;
				DateTime teStart = reference[start + RollTrainBars].Date;
				int teEndIdx = start + RollTrainBars + RollTestBars;
				DateTime teEnd = teEndIdx < reference.Count ? reference[teEndIdx].Date : DateTime.MaxValue;

				var w = new RollWindow
				{
					Index        = foldIdx,
					TestStart    = teStart,
					TestEndLabel = teEndIdx < reference.Count ? reference[teEndIdx - 1].Date : reference[^1].Date,
				};
				foreach (var (sym, bars) in barsBySymbol)
				{
					var tr = bars.Where(b => b.Date >= trStart && b.Date < teStart).ToList();
					var te = bars.Where(b => b.Date >= teStart && b.Date < teEnd).ToList();
					if (tr.Count >= 3 && te.Count >= 3)
					{
						w.Train[sym] = tr;
						w.Test[sym]  = te;
					}
				}
				if (w.Train.Count > 0)
				{
					windows.Add(w);
					foldIdx++;
				}
			}
			return windows;
		}

		private static (double mean, double median, double meanRet, double pctPos) TestStats(
			List<double> sharpes, List<double> returns)
		{
			var sorted = sharpes.OrderBy(s => s).ToList();
			double median = sorted.Count == 0 ? 0.0
				: sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
				: (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
			return (
				sharpes.Count > 0 ? sharpes.Average() : 0.0,
				median,
				returns.Count > 0 ? returns.Average() : 0.0,
				sharpes.Count > 0 ? (double)sharpes.Count(s => s > 0) / sharpes.Count : 0.0);
		}

		// Rolling walk-forward over the SMOOTHING knobs (global default re-tuned each fold).
		public static List<RollingFold> RollingWalkForward(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var folds = new List<RollingFold>();
			foreach (var w in BuildRollingWindows(barsBySymbol))
			{
				var gd = RunMulti(w.Train, initialBankroll).FirstOrDefault();
				if (gd == null)
					continue;

				var sh = new List<double>();
				var ret = new List<double>();
				foreach (var (sym, teBars) in w.Test)
				{
					var r = RunWith(teBars, gd.ExposureEmaPeriod, gd.BiasPeriod, gd.LongBias,
						gd.BiasEmaPeriod, gd.RebalanceDriftPercent, initialBankroll);
					sh.Add(SharpeOf(r));
					ret.Add(r.TotalReturnPct);
				}
				var (mean, median, meanRet, pctPos) = TestStats(sh, ret);

				folds.Add(new RollingFold
				{
					Index = w.Index, TestStart = w.TestStart, TestEnd = w.TestEndLabel,
					Symbols = w.Test.Count,
					Ema = gd.ExposureEmaPeriod, BiasP = gd.BiasPeriod, LongBias = gd.LongBias,
					BiasEma = gd.BiasEmaPeriod, Drift = gd.RebalanceDriftPercent,
					MeanTestSharpe = mean, MedianTestSharpe = median,
					MeanTestReturnPct = meanRet, PctPositive = pctPos,
				});
			}
			return folds;
		}

		// ===================== BUCKET-WEIGHT (map shape) SEARCH =====================
		// The 8 (LT, ST) weights are parameterized as two linear gradients over the ST
		// bullishness rank (Bull=3 .. Bear=0): each LT row is set by its top (ST Bull) and
		// bottom (ST Bear) value, with the two middle states interpolated. This reduces the
		// map to 4 knobs and keeps it a monotonic gradient when top >= bot. The default map
		// (topBull=1, botBull=-0.5, topBear=0.5, botBear=-1) is one grid point.
		// Smoothing knobs are held at whatever the caller configured (they are second-order).
		public static double[] TopBulls = { 0.5, 0.75, 1.0 };
		public static double[] BotBulls = { -0.5, 0.0, 0.5 };
		public static double[] TopBears = { -0.5, 0.0, 0.5 };
		public static double[] BotBears = { -1.0, -0.5, 0.0 };

		public static long BucketGridSize =>
			(long)TopBulls.Length * BotBulls.Length * TopBears.Length * BotBears.Length;

		private static void SetBucketShape(double topBull, double botBull, double topBear, double botBear)
		{
			BankrollSimulator.BullBull        = topBull;
			BankrollSimulator.BullBullNeutral = botBull + (topBull - botBull) * 2.0 / 3.0;
			BankrollSimulator.BullBearNeutral = botBull + (topBull - botBull) * 1.0 / 3.0;
			BankrollSimulator.BullBear        = botBull;
			BankrollSimulator.BearBull        = topBear;
			BankrollSimulator.BearBullNeutral = botBear + (topBear - botBear) * 2.0 / 3.0;
			BankrollSimulator.BearBearNeutral = botBear + (topBear - botBear) * 1.0 / 3.0;
			BankrollSimulator.BearBear        = botBear;
		}

		// Run one bucket-shape on a bar set, restoring the caller's 8 weights afterward.
		public static BankrollResult RunWithBucketShape(
			List<OhlcBar> bars, double topBull, double botBull, double topBear, double botBear,
			double initialBankroll = 10_000.0)
		{
			double a = BankrollSimulator.BullBull,        b = BankrollSimulator.BullBullNeutral;
			double c = BankrollSimulator.BullBearNeutral, d = BankrollSimulator.BullBear;
			double e = BankrollSimulator.BearBull,        f = BankrollSimulator.BearBullNeutral;
			double g = BankrollSimulator.BearBearNeutral, h = BankrollSimulator.BearBear;
			try
			{
				SetBucketShape(topBull, botBull, topBear, botBear);
				return BankrollSimulator.Run(bars, initialBankroll);
			}
			finally
			{
				BankrollSimulator.BullBull = a; BankrollSimulator.BullBullNeutral = b;
				BankrollSimulator.BullBearNeutral = c; BankrollSimulator.BullBear = d;
				BankrollSimulator.BearBull = e; BankrollSimulator.BearBullNeutral = f;
				BankrollSimulator.BearBearNeutral = g; BankrollSimulator.BearBear = h;
			}
		}

		// Best bucket-shape by MEAN Sharpe across a basket (the per-fold tuner).
		public static List<BucketPoint> RunMultiBuckets(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var results = new List<BucketPoint>();
			foreach (var topBull in TopBulls)
			foreach (var botBull in BotBulls)
			foreach (var topBear in TopBears)
			foreach (var botBear in BotBears)
			{
				var sharpes = new List<double>();
				foreach (var (sym, bars) in barsBySymbol)
				{
					var r = RunWithBucketShape(bars, topBull, botBull, topBear, botBear, initialBankroll);
					sharpes.Add(SharpeOf(r));
				}
				results.Add(new BucketPoint
				{
					TopBull = topBull, BotBull = botBull, TopBear = topBear, BotBear = botBear,
					MeanSharpe = sharpes.Count > 0 ? sharpes.Average() : 0.0,
					MinSharpe  = sharpes.Count > 0 ? sharpes.Min() : 0.0,
				});
			}
			return results.OrderByDescending(p => p.MeanSharpe).ToList();
		}

		// Rolling walk-forward over the BUCKET-WEIGHT shape (re-tuned each fold).
		public static List<RollingFold> RollingWalkForwardBuckets(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var folds = new List<RollingFold>();
			foreach (var w in BuildRollingWindows(barsBySymbol))
			{
				var gd = RunMultiBuckets(w.Train, initialBankroll).FirstOrDefault();
				if (gd == null)
					continue;

				var sh = new List<double>();
				var ret = new List<double>();
				foreach (var (sym, teBars) in w.Test)
				{
					var r = RunWithBucketShape(teBars, gd.TopBull, gd.BotBull, gd.TopBear, gd.BotBear, initialBankroll);
					sh.Add(SharpeOf(r));
					ret.Add(r.TotalReturnPct);
				}
				var (mean, median, meanRet, pctPos) = TestStats(sh, ret);

				folds.Add(new RollingFold
				{
					Index = w.Index, TestStart = w.TestStart, TestEnd = w.TestEndLabel,
					Symbols = w.Test.Count,
					BucketShape = gd,
					MeanTestSharpe = mean, MedianTestSharpe = median,
					MeanTestReturnPct = meanRet, PctPositive = pctPos,
				});
			}
			return folds;
		}
	}

	// Strategy vs buy & hold over one full window, using the CURRENTLY configured (fixed,
	// untuned) parameters — the "is it a useful risk overlay?" test rather than an alpha hunt.
	public class FullWindowRow
	{
		public string Symbol = "";
		public int    Bars;
		public double HistoricalVolatilityPct;

		public double StratSharpe, StratMaxDd, StratReturn;
		public double BhSharpe,    BhMaxDd,    BhReturn;

		// return per unit of max drawdown (Calmar-like); guarded against tiny drawdowns.
		public double StratRetPerDd => StratMaxDd > 0.01 ? StratReturn / StratMaxDd : double.NaN;
		public double BhRetPerDd    => BhMaxDd    > 0.01 ? BhReturn    / BhMaxDd    : double.NaN;
	}

	public class BiasSweepCell
	{
		public int    BiasPeriod;
		public int    BiasEmaPeriod;
		public double MeanSharpe;
		public double MeanMaxDd;
	}

	// One symbol's exposure persistence vs. its strategy performance (penalty off).
	public class TradabilityRow
	{
		public string Symbol = "";
		public int    Bars;
		public double HistoricalVolatilityPct;
		public double ExposureEfficiency;   // mean rolling ER of the exposure EMA (0..1)
		public double StratSharpe;
		public double StratReturnPct;
		public double BhReturnPct;
	}

	// One (TransitionPenalty, TransitionPeriod) combo scored across the basket, with the
	// per-symbol Sharpe kept so the flip-prone names can be compared against the baseline.
	public class TransitionSweepCell
	{
		public double Penalty;
		public int    Period;
		public bool   IsBaseline;      // penalty == 0 reference row
		public double MeanSharpe, MinSharpe, MeanReturnPct, MeanMaxDdPct;
		public Dictionary<string, double> SharpeBySymbol = new();
	}

	public class BiasSweepResult
	{
		public double HvThreshold;
		public int    Symbols;
		public int    CurBiasPeriod, CurBiasEmaPeriod;
		public double CurSharpe, CurMaxDd;
		public int[]  BiasPeriods = System.Array.Empty<int>();
		public int[]  BiasEmaPeriods = System.Array.Empty<int>();
		public List<BiasSweepCell> Cells = new();
		public BiasSweepCell? Recommended;
	}

	// Where the currently-configured smoothing knobs sit in the full-grid distribution.
	public class KnobRankResult
	{
		public double HvThreshold;
		public int    Symbols;
		public int    Ema, BiasP, BiasEma;
		public double LongBias, Drift;

		public double CurrentMeanSharpe;
		public double BestMeanSharpe;
		public double MedianMeanSharpe;
		public double WorstMeanSharpe;
		public int    BetterThanCurrent;   // # grid combos beating current
		public int    TotalCombos;
		public MultiGridPoint? Best;

		// fraction of combos current is >= to (higher = closer to optimal)
		public double Percentile => TotalCombos > 0
			? (double)(TotalCombos - BetterThanCurrent) / TotalCombos * 100.0 : 0.0;
	}

	// Aggregate strategy-vs-B&H over the symbols at/above one HV threshold.
	public class VolThresholdBucket
	{
		public double MinHv;
		public int    Symbols;
		public double MeanStratSharpe, MeanBhSharpe;
		public double MeanStratDd,     MeanBhDd;
		public double MeanStratReturn, MeanBhReturn;

		public double SharpeEdge  => MeanStratSharpe - MeanBhSharpe;   // + => strategy better
		public double DdReduction => MeanBhDd - MeanStratDd;           // + => strategy shallower
	}

	// Best bucket-shape (4-knob linear map) with its basket score.
	public class BucketPoint
	{
		public double TopBull, BotBull, TopBear, BotBear;
		public double MeanSharpe;
		public double MinSharpe;
		public override string ToString() =>
			$"Bull[{BotBull:0.##}..{TopBull:0.##}] Bear[{BotBear:0.##}..{TopBear:0.##}]";
	}

	public class RollingFold
	{
		public int      Index;
		public DateTime TestStart;
		public DateTime TestEnd;
		public int      Symbols;

		// smoothing-knob mode: the global default re-tuned on this fold's train
		public int      Ema, BiasP, BiasEma;
		public double   LongBias, Drift;

		// bucket-shape mode: the map shape re-tuned on this fold's train (null in knob mode)
		public BucketPoint? BucketShape;

		public double   MeanTestSharpe;
		public double   MedianTestSharpe;
		public double   MeanTestReturnPct;
		public double   PctPositive;           // fraction of symbols with test Sharpe > 0
	}

	public class WalkForwardRow
	{
		public string    Symbol = "";
		public double    HistoricalVolatilityPct;
		public int       TrainBars;
		public int       TestBars;

		public GridPoint       TunedKnobs = new();     // best on this symbol's train slice
		public MultiGridPoint? GlobalDefault;          // best mean-Sharpe combo across all train

		public double TunedTrainSharpe;                // tuned combo, in-sample (train)
		public double TunedTestSharpe;                 // tuned combo, out-of-sample (test)
		public double DefaultTestSharpe;               // global default combo, out-of-sample (test)

		public double TuningEdge => TunedTestSharpe - DefaultTestSharpe;   // OOS payoff of per-symbol tuning
		public double OverfitDecay => TunedTrainSharpe - TunedTestSharpe;  // in-sample -> OOS drop
	}
}
