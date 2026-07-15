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

		// ===================== 1-D LONG-BIAS vs VOLATILITY STUDY =====================
		// The earlier VolStudy tuned all five smoothing knobs jointly and averaged the top
		// region, which confounds LongBias with the other four and only spanned {0,.5,1,2}.
		// This is the clean, isolated test of the user's hypothesis: hold EVERY other knob
		// (and the bucket map / clamps) at whatever the caller configured, and sweep ONLY
		// LongBias over a wide range per symbol. For each symbol we record the whole curve
		// and the LongBias that maximizes Sharpe (and Calmar), tagged with the symbol's HV,
		// so the (HV -> optimal LongBias) relationship — if any — shows up undiluted.
		//
		// Mechanism reminder: LongBias raises the per-Bull-candle contribution to the
		// dynamic bias (sig = LongBias + 1), so a larger LongBias pins exposure higher in
		// bullish regimes. On a calm, steadily-rising name that recovers the return the
		// overlay otherwise forfeits to cash; on a jumpy name it over-exposes into the
		// drawdowns the overlay exists to dodge. So Sharpe-optimal LongBias is EXPECTED to
		// fall as HV rises — this quantifies whether, and how strongly, that holds.
		public static double[] LongBiasSweepValues =
			{ 0.0, 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0, 16.0 };

		public static List<LongBiasVolRow> LongBiasVsVol(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			// hold all non-LongBias knobs at the caller's current config
			int    curEma   = BankrollSimulator.ExposureEmaPeriod;
			int    curBiasP = BankrollSimulator.BiasPeriod;
			int    curBEma  = BankrollSimulator.BiasEmaPeriod;
			double curDrift = BankrollSimulator.RebalanceDriftPercent;
			double refBias  = BankrollSimulator.LongBias;   // the configured default, for reference

			var rows = new List<LongBiasVolRow>();
			foreach (var (sym, bars) in barsBySymbol)
			{
				var row = new LongBiasVolRow
				{
					Symbol                  = sym,
					HistoricalVolatilityPct = Volatility.AnnualizedHistoricalPct(bars),
					Bars                    = bars.Count,
					RefBias                 = refBias,
				};

				foreach (var lb in LongBiasSweepValues)
				{
					var r = RunWith(bars, curEma, curBiasP, lb, curBEma, curDrift, initialBankroll);
					row.Curve.Add(new LongBiasPoint
					{
						LongBias  = lb,
						Sharpe    = SharpeOf(r),
						ReturnPct = r.TotalReturnPct,
						MaxDdPct  = r.MaxDrawdownPct,
					});
				}

				// argmax-Sharpe LongBias (the primary objective)
				var bestS = row.Curve.OrderByDescending(p => p.Sharpe).First();
				row.BestSharpeBias = bestS.LongBias;
				row.BestSharpe     = bestS.Sharpe;

				// argmax-Calmar (return-per-drawdown) LongBias, for the capital-preservation lens
				var calmar = row.Curve.Where(p => !double.IsNaN(p.RetPerDd)).ToList();
				var bestC = (calmar.Count > 0 ? calmar : row.Curve)
					.OrderByDescending(p => double.IsNaN(p.RetPerDd) ? double.NegativeInfinity : p.RetPerDd)
					.First();
				row.BestCalmarBias = bestC.LongBias;
				row.BestCalmar     = bestC.RetPerDd;

				// Sharpe at the configured default (closest swept value) — "what am I giving up?"
				var refPoint = row.Curve.OrderBy(p => Math.Abs(p.LongBias - refBias)).First();
				row.RefSharpe = refPoint.Sharpe;

				rows.Add(row);
			}

			return rows.OrderBy(r => r.HistoricalVolatilityPct).ToList();
		}

		// ===================== DYNAMIC (per-candle) LONG-BIAS STUDY =====================
		// Instead of a constant LongBias, derive it every candle from a running EWMA of
		// realized volatility (BankrollSimulator.UseDynamicLongBias): calm regimes lean more
		// long, volatile regimes lean flat/short. This compares that against the static
		// LongBias baseline across the basket and sweeps the map's slope (VolBiasScale) so
		// the data — not a guess — picks how aggressive the vol->LB response should be.
		public static double   DynPivot  = 100.0;   // vol where dyn LB crosses 0
		public static double   DynFloor  = 0.0;     // no negative lean (negative guts upside on trending high-vol names)
		public static double   DynCeil   = 12.0;
		public static int      DynVolEma = 30;
		public static double[] DynScales = { 0.5, 1.0, 1.5, 2.0, 3.0 };
		public static double   DynCompareScale = 1.5;   // scale for the per-symbol static-vs-dyn table
		public static bool     DynNormalize = false;    // divide dynBias by its max => bounded to [-1,1]

		// Run one symbol with dynamic long bias at the given map params, restoring the
		// caller's dynamic-bias config afterward.
		public static BankrollResult RunDynamic(
			List<OhlcBar> bars, double scale, double pivot, double floor, double ceil, int volEma,
			double initialBankroll = 10_000.0)
		{
			bool   sUse = BankrollSimulator.UseDynamicLongBias;
			bool   sNrm = BankrollSimulator.NormalizeDynBiasToMax;
			int    sVe  = BankrollSimulator.VolEmaPeriod;
			double sPiv = BankrollSimulator.VolBiasPivot, sSc = BankrollSimulator.VolBiasScale,
			       sFl  = BankrollSimulator.VolBiasFloor, sCe = BankrollSimulator.VolBiasCeil;
			try
			{
				BankrollSimulator.UseDynamicLongBias = true;
				BankrollSimulator.NormalizeDynBiasToMax = DynNormalize;
				BankrollSimulator.VolEmaPeriod = volEma;
				BankrollSimulator.VolBiasPivot = pivot;
				BankrollSimulator.VolBiasScale = scale;
				BankrollSimulator.VolBiasFloor = floor;
				BankrollSimulator.VolBiasCeil  = ceil;
				return BankrollSimulator.Run(bars, initialBankroll);
			}
			finally
			{
				BankrollSimulator.UseDynamicLongBias = sUse;
				BankrollSimulator.NormalizeDynBiasToMax = sNrm;
				BankrollSimulator.VolEmaPeriod = sVe;
				BankrollSimulator.VolBiasPivot = sPiv; BankrollSimulator.VolBiasScale = sSc;
				BankrollSimulator.VolBiasFloor = sFl;  BankrollSimulator.VolBiasCeil  = sCe;
			}
		}

		// map value at a given vol, for the "LB@HV" display column
		private static double DynLbAt(double vol, double scale) =>
			Math.Min(Math.Max(scale * Math.Log(DynPivot / Math.Max(vol, 1e-6)), Math.Min(DynFloor, DynCeil)),
			         Math.Max(DynFloor, DynCeil));

		// Per-symbol static-LongBias baseline vs dynamic long bias at one scale.
		public static List<DynBiasRow> DynBiasCompare(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double scale, double initialBankroll = 10_000.0)
		{
			double baseLb = BankrollSimulator.LongBias;   // the static baseline in force (e.g. 0.5)
			var rows = new List<DynBiasRow>();
			foreach (var (sym, bars) in barsBySymbol)
			{
				double hv = Volatility.AnnualizedHistoricalPct(bars);
				var stat = BankrollSimulator.Run(bars, initialBankroll);   // static (UseDynamic off by default)
				var dyn  = RunDynamic(bars, scale, DynPivot, DynFloor, DynCeil, DynVolEma, initialBankroll);
				rows.Add(new DynBiasRow
				{
					Symbol = sym, Hv = hv, Bars = bars.Count, BaseLb = baseLb, Scale = scale,
					StatSharpe = SharpeOf(stat), StatDd = stat.MaxDrawdownPct, StatRet = stat.TotalReturnPct,
					DynSharpe  = SharpeOf(dyn),  DynDd  = dyn.MaxDrawdownPct,  DynRet  = dyn.TotalReturnPct,
					LbAtHv = DynLbAt(hv, scale),
				});
			}
			return rows.OrderBy(r => r.Hv).ToList();
		}

		// Sweep the map slope (VolBiasScale) and report basket-mean dynamic metrics against
		// the fixed static baseline, so the best-behaved scale (if any) is visible.
		public static List<DynBiasScalePoint> DynBiasScaleSweep(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var baseline = barsBySymbol.ToDictionary(kv => kv.Key,
				kv => BankrollSimulator.Run(kv.Value, initialBankroll));
			double mBaseShp = baseline.Values.Average(SharpeOf);
			double mBaseDd  = baseline.Values.Average(r => r.MaxDrawdownPct);
			double mBaseRet = baseline.Values.Average(r => r.TotalReturnPct);

			var points = new List<DynBiasScalePoint>();
			foreach (var sc in DynScales)
			{
				var shp = new List<double>(); var dd = new List<double>(); var ret = new List<double>();
				int shpWins = 0, ddWins = 0;
				foreach (var (sym, bars) in barsBySymbol)
				{
					var d = RunDynamic(bars, sc, DynPivot, DynFloor, DynCeil, DynVolEma, initialBankroll);
					double s = SharpeOf(d);
					shp.Add(s); dd.Add(d.MaxDrawdownPct); ret.Add(d.TotalReturnPct);
					if (s > SharpeOf(baseline[sym])) shpWins++;
					if (d.MaxDrawdownPct < baseline[sym].MaxDrawdownPct) ddWins++;
				}
				points.Add(new DynBiasScalePoint
				{
					Scale = sc, N = barsBySymbol.Count,
					MeanBaseSharpe = mBaseShp, MeanBaseDd = mBaseDd, MeanBaseRet = mBaseRet,
					MeanDynSharpe = shp.Average(), MeanDynDd = dd.Average(), MeanDynRet = ret.Average(),
					ShpWins = shpWins, DdWins = ddWins,
				});
			}
			return points;
		}

		// Fixed-LongBias A/B: the plain engine (dynBias / BiasPeriod) vs the same with
		// dynBias normalized by its max possible value (bounded to [-1,1]). LongBias stays
		// constant (as configured), so this isolates the effect of the normalization alone.
		public static List<VolScaleRow> NormStaticCompare(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var rows = new List<VolScaleRow>();
			foreach (var (sym, bars) in barsBySymbol)
			{
				bool saved = BankrollSimulator.NormalizeDynBiasToMax;
				BankrollResult off, on;
				try
				{
					BankrollSimulator.NormalizeDynBiasToMax = false;
					off = BankrollSimulator.Run(bars, initialBankroll);
					BankrollSimulator.NormalizeDynBiasToMax = true;
					on  = BankrollSimulator.Run(bars, initialBankroll);
				}
				finally { BankrollSimulator.NormalizeDynBiasToMax = saved; }

				rows.Add(new VolScaleRow
				{
					Symbol = sym, Hv = Volatility.AnnualizedHistoricalPct(bars), Bars = bars.Count,
					MinExp = BankrollSimulator.MinExposurePercent,
					BaseSharpe = SharpeOf(off), BaseDd = off.MaxDrawdownPct, BaseRet = off.TotalReturnPct,
					SclSharpe  = SharpeOf(on),  SclDd  = on.MaxDrawdownPct,  SclRet  = on.TotalReturnPct,
				});
			}
			return rows.OrderBy(r => r.Hv).ToList();
		}

		// ===================== VOL->LONGBIAS MAPPING GRID SEARCH =====================
		// Grid-search the whole vol->LongBias mapping (pivot, scale, floor, ceil) with the
		// normalized dynBias, ranked by mean Sharpe across the basket. Answers directly:
		//   (a) can ANY single vol-adaptive mapping beat buy&hold / fixed LongBias 0.5?
		//   (b) does the winner actually USE volatility (scale > 0) or collapse to a flat,
		//       constant LB (scale = 0 => vol is irrelevant)?
		// Includes scale=0 in the grid so the "flat" hypothesis competes on equal footing.
		public static double[] MapPivots = { 50, 75, 100, 150, 200 };
		public static double[] MapScales = { 0.0, 0.5, 1.0, 1.5, 2.0, 3.0 };
		public static double[] MapFloors = { 0.0 };
		public static double[] MapCeils  = { 4.0, 8.0, 12.0 };

		public static DynMapResult DynMapSearch(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			// baselines: fixed LongBias (as configured, e.g. 0.5) and buy & hold, per symbol
			var fixRuns = barsBySymbol.ToDictionary(kv => kv.Key,
				kv => BankrollSimulator.Run(kv.Value, initialBankroll));
			var res = new DynMapResult
			{
				N        = barsBySymbol.Count,
				FixLb    = BankrollSimulator.LongBias,
				FixSharpe= fixRuns.Values.Average(SharpeOf),
				FixDd    = fixRuns.Values.Average(r => r.MaxDrawdownPct),
				FixRet   = fixRuns.Values.Average(r => r.TotalReturnPct),
				BhSharpe = fixRuns.Values.Average(r => double.IsNaN(r.BuyHoldSharpeRatio) ? 0.0 : r.BuyHoldSharpeRatio),
				BhDd     = fixRuns.Values.Average(r => r.BuyHoldMaxDrawdownPct),
				BhRet    = fixRuns.Values.Average(r => r.BuyHoldReturnPct),
			};
			res.FixShpWinsBH = fixRuns.Values.Count(r => SharpeOf(r) > (double.IsNaN(r.BuyHoldSharpeRatio) ? 0.0 : r.BuyHoldSharpeRatio));
			res.FixDdWinsBH  = fixRuns.Values.Count(r => r.MaxDrawdownPct < r.BuyHoldMaxDrawdownPct);

			bool savedNorm = DynNormalize;
			DynNormalize = true;   // normalized dynBias (the best-behaved form)
			try
			{
				foreach (var piv in MapPivots)
				foreach (var sc in MapScales)
				foreach (var fl in MapFloors)
				foreach (var ce in MapCeils)
				{
					// scale=0 => LB is constant (flat) regardless of pivot; dedupe those.
					if (sc == 0.0 && piv != MapPivots[0])
						continue;

					var shp = new List<double>(); var dd = new List<double>(); var ret = new List<double>();
					int shpWinsBH = 0, ddWinsBH = 0, shpWinsFix = 0;
					foreach (var (sym, bars) in barsBySymbol)
					{
						var d = RunDynamic(bars, sc, piv, fl, ce, DynVolEma, initialBankroll);
						double s = SharpeOf(d);
						shp.Add(s); dd.Add(d.MaxDrawdownPct); ret.Add(d.TotalReturnPct);
						double bh = double.IsNaN(d.BuyHoldSharpeRatio) ? 0.0 : d.BuyHoldSharpeRatio;
						if (s > bh) shpWinsBH++;
						if (d.MaxDrawdownPct < d.BuyHoldMaxDrawdownPct) ddWinsBH++;
						if (s > SharpeOf(fixRuns[sym])) shpWinsFix++;
					}
					res.Ranked.Add(new DynMapPoint
					{
						Pivot = piv, Scale = sc, Floor = fl, Ceil = ce, Flat = sc == 0.0,
						MeanSharpe = shp.Average(), MeanDd = dd.Average(), MeanRet = ret.Average(),
						ShpWinsBH = shpWinsBH, DdWinsBH = ddWinsBH, ShpWinsFix = shpWinsFix,
					});
				}
			}
			finally { DynNormalize = savedNorm; }

			res.Ranked = res.Ranked
				.OrderByDescending(p => double.IsNaN(p.MeanSharpe) ? double.NegativeInfinity : p.MeanSharpe)
				.ToList();
			return res;
		}

		// ===================== VOLATILITY-SCALED EXPOSURE STUDY =====================
		// Leaves LongBias alone and instead scales the adjusted EMA by volatility
		// (BankrollSimulator.UseVolExposureScale): longs amplified / shorts dampened as vol
		// falls. Compares baseline vs vol-scaled per symbol at a given MinExposure, so it can
		// be run for both Min 0% (long/cash) and Min -100% (short enabled, where the short
		// dampening actually bites).
		public static double VolScalePivotCfg = 100.0;
		public static int    VolScaleEmaCfg   = 30;

		// Baseline run (no vol scaling) at a specific MinExposure, restoring both afterward.
		public static BankrollResult RunPlainWithMin(
			List<OhlcBar> bars, double minExp, double initialBankroll = 10_000.0)
		{
			bool   sScale = BankrollSimulator.UseVolExposureScale;
			double sMin   = BankrollSimulator.MinExposurePercent;
			try
			{
				BankrollSimulator.UseVolExposureScale = false;
				BankrollSimulator.MinExposurePercent  = minExp;
				return BankrollSimulator.Run(bars, initialBankroll);
			}
			finally
			{
				BankrollSimulator.UseVolExposureScale = sScale;
				BankrollSimulator.MinExposurePercent  = sMin;
			}
		}

		// Vol-scaled run at a specific pivot / vol-EMA / MinExposure, restoring afterward.
		public static BankrollResult RunVolScale(
			List<OhlcBar> bars, double pivot, int volEma, double minExp, double initialBankroll = 10_000.0)
		{
			bool   sScale = BankrollSimulator.UseVolExposureScale;
			double sPiv   = BankrollSimulator.VolScalePivot;
			int    sVe    = BankrollSimulator.VolEmaPeriod;
			double sMin   = BankrollSimulator.MinExposurePercent;
			try
			{
				BankrollSimulator.UseVolExposureScale = true;
				BankrollSimulator.VolScalePivot       = pivot;
				BankrollSimulator.VolEmaPeriod        = volEma;
				BankrollSimulator.MinExposurePercent  = minExp;
				return BankrollSimulator.Run(bars, initialBankroll);
			}
			finally
			{
				BankrollSimulator.UseVolExposureScale = sScale;
				BankrollSimulator.VolScalePivot       = sPiv;
				BankrollSimulator.VolEmaPeriod        = sVe;
				BankrollSimulator.MinExposurePercent  = sMin;
			}
		}

		// Per-symbol baseline vs vol-scaled at one MinExposure setting.
		public static List<VolScaleRow> VolScaleCompare(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double minExp, double initialBankroll = 10_000.0)
		{
			var rows = new List<VolScaleRow>();
			foreach (var (sym, bars) in barsBySymbol)
			{
				var b = RunPlainWithMin(bars, minExp, initialBankroll);
				var s = RunVolScale(bars, VolScalePivotCfg, VolScaleEmaCfg, minExp, initialBankroll);
				rows.Add(new VolScaleRow
				{
					Symbol = sym, Hv = Volatility.AnnualizedHistoricalPct(bars), Bars = bars.Count, MinExp = minExp,
					BaseSharpe = SharpeOf(b), BaseDd = b.MaxDrawdownPct, BaseRet = b.TotalReturnPct,
					SclSharpe  = SharpeOf(s), SclDd  = s.MaxDrawdownPct, SclRet  = s.TotalReturnPct,
				});
			}
			return rows.OrderBy(r => r.Hv).ToList();
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

		// ============================================================================
		// LT STATE: CONCURRENT vs FORWARD returns.
		// Reconciles "on the chart, Bull-state bars go up and Bear-state bars go down" with
		// "the state has no predictive edge." For every bar the LT state is computed FROM that
		// bar's close (exactly as drawn on a chart). Offset 0 measures that SAME bar's return
		// (concurrent — the state literally labels this move; not tradable). Offsets +1,+2,...
		// measure LATER bars' returns (tradable: the state is known at the current close). If
		// the Bull−Bear return spread is large at offset 0 but ~0 by +1, the visible pattern is
		// descriptive (the label is a function of the move), not a forecast.
		// ============================================================================
		public static readonly int[] StateLagOffsets = { 0, 1, 2, 3, 5, 10 };

		public static List<StateLagResult> StateLag(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			int nOff = StateLagOffsets.Length;

			// pooled accumulators: [state 0=Bull,1=Bear][offset] -> (sumRet%, count, upCount)
			var pSum = new double[2, nOff];
			var pCnt = new int[2, nOff];
			var pUp  = new int[2, nOff];

			var perSymbol = new List<StateLagResult>();

			foreach (var (sym, bars) in barsBySymbol)
			{
				if (bars.Count < 3) continue;

				var ltEngine = new LongTermStateEngine();
				var sSum = new double[2, nOff];
				var sCnt = new int[2, nOff];
				var sUp  = new int[2, nOff];

				for (int i = 1; i < bars.Count; i++)
				{
					// concurrent LT state at bar i — uses bars[i]'s close, exactly as charted
					var s = ltEngine.Update(bars[i - 1], bars[i]);
					int si = s == LongTermState.Bull ? 0 : 1;

					for (int j = 0; j < nOff; j++)
					{
						int t = i + StateLagOffsets[j];   // return on bar t (offset 0 => bar i itself)
						if (t < 1 || t >= bars.Count) continue;
						if (bars[t - 1].Close <= 0) continue;
						double r = (bars[t].Close - bars[t - 1].Close) / bars[t - 1].Close * 100.0;
						sSum[si, j] += r; sCnt[si, j]++; if (r > 0) sUp[si, j]++;
						pSum[si, j] += r; pCnt[si, j]++; if (r > 0) pUp[si, j]++;
					}
				}

				perSymbol.Add(BuildStateLag(sym, Volatility.AnnualizedHistoricalPct(bars), sSum, sCnt, sUp));
			}

			var results = new List<StateLagResult> { BuildStateLag("BASKET", 0.0, pSum, pCnt, pUp) };
			results.AddRange(perSymbol.OrderBy(r => r.Hv));
			return results;
		}

		private static StateLagResult BuildStateLag(string scope, double hv, double[,] sum, int[,] cnt, int[,] up)
		{
			int nOff = StateLagOffsets.Length;
			StateLagRow Row(int si) => new StateLagRow
			{
				State      = si == 0 ? LongTermState.Bull : LongTermState.Bear,
				N          = Enumerable.Range(0, nOff).Select(j => cnt[si, j]).ToArray(),
				MeanRetPct = Enumerable.Range(0, nOff).Select(j => cnt[si, j] > 0 ? sum[si, j] / cnt[si, j] : 0.0).ToArray(),
				UpRatePct  = Enumerable.Range(0, nOff).Select(j => cnt[si, j] > 0 ? (double)up[si, j] / cnt[si, j] * 100.0 : 0.0).ToArray(),
			};
			return new StateLagResult { Scope = scope, Hv = hv, Offsets = StateLagOffsets, Bull = Row(0), Bear = Row(1) };
		}

		// ============================================================================
		// TRIPLE-BARRIER (bracket) study on LT-Bull entries vs random entries.
		// The RUN-level question: entering long when the LT state turns Bull, what is the
		// probability the trade reaches +Y% before −Z% (within a max hold)? A per-bar
		// direction test can't see this — trend edge lives in PATH asymmetry, not next-bar
		// sign. The honest control is the SAME brackets from RANDOM (every-bar) entries: if
		// Bull entries hit the +Y barrier first no more often than random entries, the state
		// adds no timing skill. Entry price is the close of the transition bar (known then);
		// barriers are checked on later bars' High/Low (tradable, no look-ahead). Same-bar
		// double-touch counts as a loss (conservative). Both groups share the same window,
		// drift and symbols, so the EDGE (Bull − random) is meaningful even in-sample.
		// ============================================================================
		public static (double Up, double Down)[] Brackets =
			{ (2, 2), (3, 3), (5, 3), (5, 5), (7, 5), (10, 5), (10, 10) };
		public static int BracketMaxHold = 120;

		private enum BarrierOutcome { Win, Loss, Timeout }

		public static List<BracketResult> BarrierStudy(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var results = new List<BracketResult>();

			foreach (var (up, down) in Brackets)
			{
				int bN = 0, bWin = 0, bLoss = 0, bTo = 0; double bRet = 0, bBars = 0;
				int rN = 0, rWin = 0, rLoss = 0, rTo = 0; double rRet = 0, rBars = 0;

				foreach (var (sym, bars) in barsBySymbol)
				{
					if (bars.Count < 3) continue;

					// Bull-transition entries
					var lt = new LongTermStateEngine();
					var prev = LongTermState.Bear;
					for (int i = 1; i < bars.Count - 1; i++)
					{
						var s = lt.Update(bars[i - 1], bars[i]);
						if (s == LongTermState.Bull && prev != LongTermState.Bull)
						{
							var (o, ret, held) = RunBarrier(bars, i, up, down, BracketMaxHold);
							bN++; bRet += ret; bBars += held;
							if (o == BarrierOutcome.Win) bWin++; else if (o == BarrierOutcome.Loss) bLoss++; else bTo++;
						}
						prev = s;
					}

					// random / unconditional baseline: enter at EVERY bar
					for (int i = 1; i < bars.Count - 1; i++)
					{
						var (o, ret, held) = RunBarrier(bars, i, up, down, BracketMaxHold);
						rN++; rRet += ret; rBars += held;
						if (o == BarrierOutcome.Win) rWin++; else if (o == BarrierOutcome.Loss) rLoss++; else rTo++;
					}
				}

				results.Add(new BracketResult
				{
					Up = up, Down = down, MaxHold = BracketMaxHold,
					BullN = bN,
					BullWinPct     = bN > 0 ? 100.0 * bWin / bN : 0.0,
					BullLossPct    = bN > 0 ? 100.0 * bLoss / bN : 0.0,
					BullTimeoutPct = bN > 0 ? 100.0 * bTo / bN : 0.0,
					BullExpectancyPct = bN > 0 ? bRet / bN : 0.0,
					BullAvgBars    = bN > 0 ? bBars / bN : 0.0,
					RandN = rN,
					RandWinPct     = rN > 0 ? 100.0 * rWin / rN : 0.0,
					RandLossPct    = rN > 0 ? 100.0 * rLoss / rN : 0.0,
					RandTimeoutPct = rN > 0 ? 100.0 * rTo / rN : 0.0,
					RandExpectancyPct = rN > 0 ? rRet / rN : 0.0,
				});
			}

			return results;
		}

		// Walk forward from `entry` (entered at bars[entry].Close). Win if +up% is touched
		// before −down%, Loss if −down% first, Timeout at maxHold. Uses later bars' High/Low;
		// a same-bar double touch is scored as a Loss (conservative). Returns the realized %.
		private static (BarrierOutcome o, double ret, int bars) RunBarrier(
			List<OhlcBar> bars, int entry, double upPct, double downPct, int maxHold)
		{
			double p0 = bars[entry].Close;
			if (p0 <= 0) return (BarrierOutcome.Timeout, 0.0, 0);
			double tp = p0 * (1.0 + upPct / 100.0);
			double sl = p0 * (1.0 - downPct / 100.0);
			int last = Math.Min(entry + maxHold, bars.Count - 1);
			for (int j = entry + 1; j <= last; j++)
			{
				bool hitSl = bars[j].Low <= sl;
				bool hitTp = bars[j].High >= tp;
				if (hitSl) return (BarrierOutcome.Loss, -downPct, j - entry);   // SL checked first => conservative
				if (hitTp) return (BarrierOutcome.Win, upPct, j - entry);
			}
			double ret = (bars[last].Close - p0) / p0 * 100.0;
			return (BarrierOutcome.Timeout, ret, last - entry);
		}

		// ============================================================================
		// EXPOSURE -> RETURN CURVE.
		// Map the continuous exposure signal (the EMA-of-target, in [-1,1], look-ahead-free)
		// into fixed 0.1-wide buckets and measure the AVERAGE forward return in each — no
		// position sizing, just the raw shape. Answers: is mean return flat across exposure
		// (no info), linearly rising (predictive & sizeable), or curved? Reports the per-bucket
		// means plus a linear and quadratic fit over the raw observations.
		// ============================================================================
		public static List<ExpCurveResult> ExposureReturnCurve(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			const double W = 0.1;                 // bucket width
			int nBins = (int)Math.Round(2.0 / W); // 20 buckets over [-1, 1]
			double alpha = 2.0 / (BankrollSimulator.ExposureEmaPeriod + 1);

			var results = new List<ExpCurveResult>();

			foreach (var (sym, bars) in barsBySymbol)
			{
				if (bars.Count < 3) continue;
				var ltE = new LongTermStateEngine();
				var stE = new CandleStateEngine();
				double ema = double.NaN;

				var cnt = new int[nBins];
				var sumFull = new double[nBins];
				var sumOn = new double[nBins];
				var up = new int[nBins];
				var xs = new List<double>();   // exposure
				var ys = new List<double>();   // full-day fwd return %

				for (int i = 2; i < bars.Count; i++)
				{
					var lt = ltE.Update(bars[i - 2], bars[i - 1]);
					var st = stE.Update(bars[i - 2], bars[i - 1]);
					if (st == null) continue;
					double target = BankrollSimulator.TargetExposure(lt, st.Value);
					ema = double.IsNaN(ema) ? target : alpha * target + (1.0 - alpha) * ema;

					double pc = bars[i - 1].Close, cl = bars[i].Close, op = bars[i].Open;
					if (pc <= 0 || cl <= 0) continue;
					double full = (cl / pc - 1.0) * 100.0;
					double on = op > 0 ? (op / pc - 1.0) * 100.0 : 0.0;

					double e = Math.Max(-1.0, Math.Min(1.0, ema));
					int bin = (int)Math.Floor((e + 1.0) / W);
					if (bin < 0) bin = 0; if (bin >= nBins) bin = nBins - 1;

					cnt[bin]++; sumFull[bin] += full; sumOn[bin] += on; if (full > 0) up[bin]++;
					xs.Add(e); ys.Add(full);
				}

				var res = new ExpCurveResult { Scope = sym, Hv = Volatility.AnnualizedHistoricalPct(bars), N = xs.Count };
				if (xs.Count > 0) res.MeanExp = xs.Average();
				for (int b = 0; b < nBins; b++)
				{
					double lo = -1.0 + b * W;
					res.Bins.Add(new ExpCurveBin
					{
						Lo = lo, Hi = lo + W, Center = lo + W / 2.0, N = cnt[b],
						MeanFullPct = cnt[b] > 0 ? sumFull[b] / cnt[b] : double.NaN,
						MeanOnPct   = cnt[b] > 0 ? sumOn[b] / cnt[b] : double.NaN,
						UpPct       = cnt[b] > 0 ? (double)up[b] / cnt[b] * 100.0 : double.NaN,
					});
				}

				// linear fit y = a + b*x  and quadratic y = a2 + b2*x + c2*x^2 over raw pairs
				res.Corr = ProbCorr(xs, ys);
				var (a, b1) = LinFit2(xs, ys);
				res.Intercept = a; res.Slope = b1; res.R2 = res.Corr * res.Corr;
				var (qa, qb, qc) = PolyFit2(xs, ys);
				res.QuadA = qa; res.QuadB = qb; res.QuadC = qc;

				// R^2 of the quadratic fit (how much of the return variance the curve explains)
				double my = ys.Count > 0 ? ys.Average() : 0.0, ssTot = 0, ssRes = 0;
				for (int k = 0; k < ys.Count; k++)
				{
					double pred = qa + qb * xs[k] + qc * xs[k] * xs[k];
					ssRes += (ys[k] - pred) * (ys[k] - pred);
					ssTot += (ys[k] - my) * (ys[k] - my);
				}
				res.QuadR2 = ssTot > 0 ? 1.0 - ssRes / ssTot : 0.0;

				results.Add(res);
			}

			return results;
		}

		// OLS line y = a + b*x.
		private static (double a, double b) LinFit2(List<double> xs, List<double> ys)
		{
			int n = xs.Count; if (n < 2) return (0, 0);
			double mx = xs.Average(), my = ys.Average(), sxy = 0, sxx = 0;
			for (int i = 0; i < n; i++) { double dx = xs[i] - mx; sxy += dx * (ys[i] - my); sxx += dx * dx; }
			double b = sxx > 0 ? sxy / sxx : 0.0;
			return (my - b * mx, b);
		}

		// Least-squares quadratic y = a + b*x + c*x^2 (3x3 normal equations, Cramer's rule).
		private static (double a, double b, double c) PolyFit2(List<double> xs, List<double> ys)
		{
			int n = xs.Count; if (n < 3) return (0, 0, 0);
			double S0 = n, S1 = 0, S2 = 0, S3 = 0, S4 = 0, T0 = 0, T1 = 0, T2 = 0;
			for (int i = 0; i < n; i++)
			{
				double x = xs[i], y = ys[i], x2 = x * x;
				S1 += x; S2 += x2; S3 += x2 * x; S4 += x2 * x2;
				T0 += y; T1 += x * y; T2 += x2 * y;
			}
			double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i)
				=> a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
			double D = Det(S0, S1, S2, S1, S2, S3, S2, S3, S4);
			if (Math.Abs(D) < 1e-12) return (0, 0, 0);
			double a0 = Det(T0, S1, S2, T1, S2, S3, T2, S3, S4) / D;
			double b0 = Det(S0, T0, S2, S1, T1, S3, S2, T2, S4) / D;
			double c0 = Det(S0, S1, T0, S1, S2, T1, S2, S3, T2) / D;
			return (a0, b0, c0);
		}

		// ============================================================================
		// EXPOSURE vs OVERNIGHT GAP.
		// The full-day close (close_i vs close_{i-1}) is a coin flip vs exposure, but the
		// OVERNIGHT gap (open_i vs close_{i-1}) carries real drift. So: does the exposure
		// signal (target exposure as of the prior close — look-ahead-free, tradable MOC→MOO)
		// predict the overnight gap even though it can't predict the full day? Scores the same
		// signal against overnight / full-day / intraday outcomes side by side.
		// ============================================================================
		public static List<ExposureGapResult> ExposureGap(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var allExp = new List<double>();
			var onUp = new List<double>(); var fullUp = new List<double>(); var idUp = new List<double>();
			var onRet = new List<double>(); var fullRet = new List<double>(); var idRet = new List<double>();
			var perSymbol = new List<ExposureGapResult>();

			foreach (var (sym, bars) in barsBySymbol)
			{
				if (bars.Count < 3) continue;
				var ltE = new LongTermStateEngine();
				var stE = new CandleStateEngine();

				var sExp = new List<double>();
				var sOnUp = new List<double>(); var sFullUp = new List<double>(); var sIdUp = new List<double>();
				var sOnRet = new List<double>(); var sFullRet = new List<double>(); var sIdRet = new List<double>();

				for (int i = 2; i < bars.Count; i++)
				{
					var lt = ltE.Update(bars[i - 2], bars[i - 1]);
					var st = stE.Update(bars[i - 2], bars[i - 1]);
					if (st == null) continue;
					double pc = bars[i - 1].Close, op = bars[i].Open, cl = bars[i].Close;
					if (pc <= 0 || op <= 0 || cl <= 0) continue;

					double target = BankrollSimulator.TargetExposure(lt, st.Value);
					double on = op / pc - 1.0, full = cl / pc - 1.0, id = cl / op - 1.0;

					sExp.Add(target);
					sOnUp.Add(on > 0 ? 1 : 0); sFullUp.Add(full > 0 ? 1 : 0); sIdUp.Add(id > 0 ? 1 : 0);
					sOnRet.Add(on * 100); sFullRet.Add(full * 100); sIdRet.Add(id * 100);

					allExp.Add(target);
					onUp.Add(on > 0 ? 1 : 0); fullUp.Add(full > 0 ? 1 : 0); idUp.Add(id > 0 ? 1 : 0);
					onRet.Add(on * 100); fullRet.Add(full * 100); idRet.Add(id * 100);
				}

				if (sExp.Count == 0) continue;
				perSymbol.Add(BuildExposureGap(sym, Volatility.AnnualizedHistoricalPct(bars),
					sExp, sOnUp, sFullUp, sIdUp, sOnRet, sFullRet, sIdRet));
			}

			var results = new List<ExposureGapResult>
			{
				BuildExposureGap("BASKET", 0.0, allExp, onUp, fullUp, idUp, onRet, fullRet, idRet)
			};
			results.AddRange(perSymbol.OrderBy(r => r.Hv));
			return results;
		}

		private static ExposureGapResult BuildExposureGap(
			string scope, double hv, List<double> exp,
			List<double> onUp, List<double> fullUp, List<double> idUp,
			List<double> onRet, List<double> fullRet, List<double> idRet)
		{
			int n = exp.Count;
			var res = new ExposureGapResult
			{
				Scope = scope, Hv = hv, N = n,
				BaseOnUp   = Avg(onUp) * 100,   BaseFullUp = Avg(fullUp) * 100, BaseIdUp = Avg(idUp) * 100,
				BaseOnRet  = Avg(onRet),        BaseFullRet = Avg(fullRet),     BaseIdRet = Avg(idRet),
				CorrOnUp   = ProbCorr(exp, onUp),   CorrFullUp = ProbCorr(exp, fullUp), CorrIdUp = ProbCorr(exp, idUp),
				CorrOnRet  = ProbCorr(exp, onRet),  CorrFullRet = ProbCorr(exp, fullRet),
			};
			foreach (var g in Enumerable.Range(0, n).GroupBy(i => Math.Round(exp[i], 4)).OrderBy(g => g.Key))
			{
				var idx = g.ToList();
				res.Levels.Add(new ExposureGapBin
				{
					Exposure = g.Key, N = idx.Count,
					OnUpPct   = idx.Average(i => onUp[i]) * 100,
					FullUpPct = idx.Average(i => fullUp[i]) * 100,
					IdUpPct   = idx.Average(i => idUp[i]) * 100,
					OnRetPct   = idx.Average(i => onRet[i]),
					FullRetPct = idx.Average(i => fullRet[i]),
					IdRetPct   = idx.Average(i => idRet[i]),
				});
			}
			return res;
		}

		private static double Avg(List<double> xs) => xs.Count > 0 ? xs.Average() : 0.0;

		// ============================================================================
		// SIGNAL SCREEN (single-name, e.g. ^GSPC).
		// Accept that the (LT,ST) state machine has no directional edge and screen fresh,
		// look-ahead-free features for ANY forward predictability: momentum, short-term
		// reversal, distance-from-MA, vol regime; the overnight/intraday return split; and
		// weekday / turn-of-month seasonality. Each scalar feature gets an in-sample corr +
		// quintile spread AND an out-of-sample check: pick the sign on a 60% train slice, trade
		// long/flat on the 40% test slice, and compare the rule's Sharpe to buy&hold. Positive
		// OOS edge = the feature times the tape better than always-in.
		// ============================================================================
		public static double SignalScreenTrainFraction = 0.60;

		public static List<SignalScreenResult> SignalScreen(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var results = new List<SignalScreenResult>();

			foreach (var (sym, bars) in barsBySymbol)
			{
				int n = bars.Count;
				if (n < 260) continue;
				var res = new SignalScreenResult { Symbol = sym, Bars = n };

				// close-to-close return r[t] (t>=1)
				var r = new double[n];
				for (int t = 1; t < n; t++) r[t] = bars[t - 1].Close > 0 ? bars[t].Close / bars[t - 1].Close - 1 : 0.0;

				double SMA(int t, int w)
				{
					if (t + 1 < w) return double.NaN;
					double s = 0; for (int k = t - w + 1; k <= t; k++) s += bars[k].Close; return s / w;
				}
				double Vol(int t, int w)
				{
					if (t < w) return double.NaN;
					double m = 0; for (int k = t - w + 1; k <= t; k++) m += r[k]; m /= w;
					double v = 0; for (int k = t - w + 1; k <= t; k++) { double d = r[k] - m; v += d * d; }
					return Math.Sqrt(v / (w - 1));
				}

				void Add(string name, Func<int, double> f)
				{
					var feat = new double[n];
					for (int t = 0; t < n; t++) feat[t] = f(t);
					res.Features.Add(ScreenFeature(name, feat, r, n));
				}
				Add("mom20",      t => t >= 20 && bars[t - 20].Close > 0 ? bars[t].Close / bars[t - 20].Close - 1 : double.NaN);
				Add("mom50",      t => t >= 50 && bars[t - 50].Close > 0 ? bars[t].Close / bars[t - 50].Close - 1 : double.NaN);
				Add("ret1",       t => t >= 1 ? r[t] : double.NaN);
				Add("ret5",       t => t >= 5 && bars[t - 5].Close > 0 ? bars[t].Close / bars[t - 5].Close - 1 : double.NaN);
				Add("distSMA50",  t => { double s = SMA(t, 50);  return double.IsNaN(s) || s <= 0 ? double.NaN : bars[t].Close / s - 1; });
				Add("distSMA200", t => { double s = SMA(t, 200); return double.IsNaN(s) || s <= 0 ? double.NaN : bars[t].Close / s - 1; });
				Add("vol20",      t => Vol(t, 20));

				// overnight (open/prev-close) vs intraday (close/open) decomposition
				var overnight = new List<double>(); var intraday = new List<double>(); var full = new List<double>();
				for (int t = 1; t < n; t++)
					if (bars[t - 1].Close > 0 && bars[t].Open > 0)
					{
						overnight.Add(bars[t].Open / bars[t - 1].Close - 1);
						intraday.Add(bars[t].Close / bars[t].Open - 1);
						full.Add(r[t]);
					}
				res.Segments.Add(MakeSegment("Overnight",  overnight));
				res.Segments.Add(MakeSegment("Intraday",   intraday));
				res.Segments.Add(MakeSegment("Full (B&H)", full));

				// day-of-week (return realized on each weekday)
				var byDow = new Dictionary<DayOfWeek, (double sum, int n, int up)>();
				for (int t = 1; t < n; t++)
				{
					var d = bars[t].Date.DayOfWeek;
					if (!byDow.TryGetValue(d, out var e)) e = (0, 0, 0);
					e.sum += r[t]; e.n++; if (r[t] > 0) e.up++; byDow[d] = e;
				}
				foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
					if (byDow.TryGetValue(d, out var e) && e.n > 0)
						res.Dow.Add(new DowRow { Name = d.ToString().Substring(0, 3), N = e.n, MeanRetPct = e.sum / e.n * 100.0, UpPct = (double)e.up / e.n * 100.0 });

				// turn-of-month (calendar-day approx: day <= 3 or >= 26)
				double tomSum = 0, restSum = 0; int tomN = 0, restN = 0;
				for (int t = 1; t < n; t++)
				{
					int dd = bars[t].Date.Day;
					if (dd <= 3 || dd >= 26) { tomSum += r[t]; tomN++; } else { restSum += r[t]; restN++; }
				}
				res.Tom  = new DowRow { Name = "ToM(<=3,>=26)", N = tomN,  MeanRetPct = tomN  > 0 ? tomSum  / tomN  * 100.0 : 0.0 };
				res.Rest = new DowRow { Name = "Rest",          N = restN, MeanRetPct = restN > 0 ? restSum / restN * 100.0 : 0.0 };

				results.Add(res);
			}

			return results;
		}

		// Screen one scalar feature: in-sample corr + quintile spread, and an OOS long/flat
		// rule (direction chosen on the 60% train slice) scored against buy&hold on the test.
		private static FeatureScreenRow ScreenFeature(string name, double[] feat, double[] r, int n)
		{
			var fs = new List<double>(); var ys = new List<double>();
			for (int t = 0; t < n - 1; t++) { if (double.IsNaN(feat[t])) continue; fs.Add(feat[t]); ys.Add(r[t + 1] * 100.0); }
			int m = fs.Count;
			var row = new FeatureScreenRow { Name = name, N = m };
			if (m < 50) return row;

			row.Corr = ProbCorr(fs, ys);

			var order = Enumerable.Range(0, m).OrderBy(i => fs[i]).ToList();
			double QMean(int q) { int lo = (int)((long)q * m / 5), hi = (int)((long)(q + 1) * m / 5); double s = 0; int c = 0; for (int i = lo; i < hi; i++) { s += ys[order[i]]; c++; } return c > 0 ? s / c : 0.0; }
			double QUp(int q)   { int lo = (int)((long)q * m / 5), hi = (int)((long)(q + 1) * m / 5); int u = 0, c = 0; for (int i = lo; i < hi; i++) { if (ys[order[i]] > 0) u++; c++; } return c > 0 ? (double)u / c * 100.0 : 0.0; }
			row.QSpreadRet = QMean(4) - QMean(0);
			row.QSpreadUp  = QUp(4) - QUp(0);

			int split = (int)(m * SignalScreenTrainFraction);
			if (split >= 10 && m - split >= 10)
			{
				double tc = ProbCorr(fs.Take(split).ToList(), ys.Take(split).ToList());
				double dir = tc >= 0 ? 1.0 : -1.0;
				double tmean = fs.Take(split).Average();
				var ruleR = new List<double>(); var bhR = new List<double>(); int longN = 0;
				for (int i = split; i < m; i++)
				{
					bool lng = dir * (fs[i] - tmean) > 0;
					ruleR.Add(lng ? ys[i] / 100.0 : 0.0);
					bhR.Add(ys[i] / 100.0);
					if (lng) longN++;
				}
				row.OosRuleSharpe = SharpeOfReturns(ruleR);
				row.OosBhSharpe   = SharpeOfReturns(bhR);
				row.PctLong       = (m - split) > 0 ? (double)longN / (m - split) * 100.0 : 0.0;
			}
			return row;
		}

		private static SegmentRow MakeSegment(string name, List<double> rets)
		{
			double mean = rets.Count > 0 ? rets.Average() : 0.0;
			double sd = SegStd(rets);
			double cum = 1.0; foreach (var x in rets) cum *= 1.0 + x;
			double py = BankrollSimulator.PeriodsPerYear;
			return new SegmentRow
			{
				Name = name,
				AnnMeanPct = mean * py * 100.0,
				AnnVolPct  = sd * Math.Sqrt(py) * 100.0,
				Sharpe     = sd > 0 ? mean / sd * Math.Sqrt(py) : 0.0,
				CumRetPct  = (cum - 1.0) * 100.0,
			};
		}

		private static double SegStd(List<double> xs)
		{
			if (xs.Count < 2) return 0.0;
			double m = xs.Average();
			double v = xs.Sum(x => (x - m) * (x - m)) / (xs.Count - 1);
			return Math.Sqrt(v);
		}

		// ============================================================================
		// PROBABILITY <-> EXPOSURE calibration.
		// Does the per-candle TARGET exposure (the (LT,ST) map value, known as of `prev`)
		// predict the odds that the NEXT candle closes ABOVE the previous close?
		// For every bar we record (target exposure, forward return, up/down), bin by
		// exposure level, and measure the empirical P(up-close). If P(up) rises
		// monotonically with exposure, the signal is a genuine probability and the map is
		// invertible (probability -> exposure). Look-ahead-free: states use
		// bars[i-2],bars[i-1]; the outcome is the move into bars[i] — exactly the return the
		// simulator trades on. LongBias/EMA smoothing don't touch the raw target, so the
		// discrete table is independent of them; only the configured (LT,ST) bucket map sets
		// the exposure levels. The continuous curve uses an EMA-of-target at the currently
		// configured ExposureEmaPeriod.
		// ============================================================================
		public static ProbExposureStudyResult ProbExposure(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			// pooled (whole-basket) observations for the headline calibration
			var allExp = new List<double>();
			var allEma = new List<double>();
			var allUp  = new List<double>();   // 1.0 = up-close, 0.0 otherwise
			var allRet = new List<double>();   // forward return, %
			var perSymbol = new List<ProbExposureResult>();

			double alpha = 2.0 / (BankrollSimulator.ExposureEmaPeriod + 1);

			foreach (var (sym, bars) in barsBySymbol)
			{
				if (bars.Count < 3) continue;

				var ltEngine = new LongTermStateEngine();
				var stEngine = new CandleStateEngine();
				double ema = double.NaN;

				var symExp = new List<double>();
				var symUp  = new List<double>();
				var symRet = new List<double>();

				for (int i = 2; i < bars.Count; i++)
				{
					var lt = ltEngine.Update(bars[i - 2], bars[i - 1]);
					var st = stEngine.Update(bars[i - 2], bars[i - 1]);
					if (st == null) continue;

					double target = BankrollSimulator.TargetExposure(lt, st.Value);
					ema = double.IsNaN(ema) ? target : alpha * target + (1.0 - alpha) * ema;

					double r = bars[i - 1].Close > 0
						? (bars[i].Close - bars[i - 1].Close) / bars[i - 1].Close : 0.0;
					double up = r > 0 ? 1.0 : 0.0;

					symExp.Add(target); symUp.Add(up); symRet.Add(r * 100.0);
					allExp.Add(target); allEma.Add(ema); allUp.Add(up); allRet.Add(r * 100.0);
				}

				if (symExp.Count == 0) continue;
				perSymbol.Add(BuildProbResult(sym, Volatility.AnnualizedHistoricalPct(bars),
					symExp, symUp, symRet, null));
			}

			var study = new ProbExposureStudyResult();

			// basket (pooled) result first, with the continuous EMA-of-target decile curve
			study.Calibration.Add(BuildProbResult("BASKET", 0.0, allExp, allUp, allRet, allEma));
			study.Calibration.AddRange(perSymbol.OrderBy(r => r.Hv));

			// de-risking vs timing control: strategy DD vs a constant position at the same
			// average exposure. Needs the per-bar position series, so run the sim with recording.
			bool savedRec = BankrollSimulator.RecordBars;
			try
			{
				BankrollSimulator.RecordBars = true;
				foreach (var (sym, bars) in barsBySymbol)
				{
					if (bars.Count < 3) continue;
					var r = BankrollSimulator.Run(bars, initialBankroll);
					if (r.BarPositions.Count == 0) continue;

					double avgExp = r.BarPositions.Average();
					// constant-exposure benchmark: hold avgExp every bar over the same returns.
					var constRets = r.BarBhReturns.Select(x => avgExp * x).ToList();

					study.Timing.Add(new RiskTimingRow
					{
						Symbol         = sym,
						Hv             = Volatility.AnnualizedHistoricalPct(bars),
						AvgExposurePct = avgExp * 100.0,
						StratDd        = r.MaxDrawdownPct,
						ConstDd        = MaxDdOf(constRets),
						BhDd           = r.BuyHoldMaxDrawdownPct,
						StratSharpe    = SharpeOf(r),
						ConstSharpe    = SharpeOfReturns(constRets),
					});
				}
			}
			finally { BankrollSimulator.RecordBars = savedRec; }

			study.Timing = study.Timing.OrderBy(t => t.Hv).ToList();
			return study;
		}

		// Assemble one calibration result: base rate, direction + risk correlations, the
		// discrete per-exposure-level table (odds + forward risk), and (basket only) the
		// continuous EMA-of-target decile curve.
		private static ProbExposureResult BuildProbResult(
			string scope, double hv,
			List<double> exp, List<double> up, List<double> ret, List<double>? ema)
		{
			int n = exp.Count;
			var absRet  = ret.Select(Math.Abs).ToList();
			var downRet = ret.Select(x => Math.Min(x, 0.0)).ToList();  // 0 on up bars, r on down bars
			var res = new ProbExposureResult
			{
				Scope           = scope,
				Hv              = hv,
				N               = n,
				BaseUpRatePct   = n > 0 ? up.Average() * 100.0 : 0.0,
				MeanRetPct      = n > 0 ? ret.Average() : 0.0,
				CorrExpUp       = ProbCorr(exp, up),
				CorrExpRet      = ProbCorr(exp, ret),
				CorrExpAbsRet   = ProbCorr(exp, absRet),
				CorrExpDownside = ProbCorr(exp, downRet),
			};

			// discrete calibration: group by the distinct target-exposure value
			foreach (var g in Enumerable.Range(0, n)
				.GroupBy(i => Math.Round(exp[i], 4))
				.OrderBy(g => g.Key))
			{
				var idx = g.ToList();
				res.Levels.Add(MakeBin(g.Key, idx, ret, up));
			}

			// continuous curve: EMA-of-target sorted into equal-count deciles (basket only)
			if (ema != null && ema.Count == n && n >= 10)
			{
				var order = Enumerable.Range(0, n).OrderBy(i => ema[i]).ToList();
				const int bins = 10;
				for (int b = 0; b < bins; b++)
				{
					int lo = (int)((long)b * n / bins);
					int hi = (int)((long)(b + 1) * n / bins);
					if (hi <= lo) continue;
					var slice = order.GetRange(lo, hi - lo);
					res.Deciles.Add(MakeBin(slice.Average(i => ema[i]), slice, ret, up));
				}
			}

			return res;
		}

		// Build one bin (odds + forward-risk stats) from a set of observation indices.
		private static ProbExposureBin MakeBin(double exposure, List<int> idx, List<double> ret, List<double> up)
		{
			int cnt = idx.Count;
			double mean = cnt > 0 ? idx.Average(i => ret[i]) : 0.0;
			double var  = cnt > 1 ? idx.Sum(i => (ret[i] - mean) * (ret[i] - mean)) / (cnt - 1) : 0.0;
			double downSq = cnt > 0 ? idx.Average(i => { double d = Math.Min(ret[i], 0.0); return d * d; }) : 0.0;
			return new ProbExposureBin
			{
				Exposure       = exposure,
				N              = cnt,
				Ups            = idx.Count(i => up[i] > 0),
				MeanFwdRetPct  = mean,
				FwdVolPct      = Math.Sqrt(var),
				DownsideDevPct = Math.Sqrt(downSq),
				DownRatePct    = cnt > 0 ? (double)idx.Count(i => ret[i] < 0) / cnt * 100.0 : 0.0,
			};
		}

		// Max peak-to-trough drawdown (%) of the equity curve from compounding a return series.
		private static double MaxDdOf(List<double> rets)
		{
			double equity = 1.0, peak = 1.0, maxDd = 0.0;
			foreach (var r in rets)
			{
				equity *= 1.0 + r;
				if (equity > peak) peak = equity;
				double dd = (peak - equity) / peak * 100.0;
				if (dd > maxDd) maxDd = dd;
			}
			return maxDd;
		}

		// Annualized Sharpe (rf=0) of a per-bar return series, using the configured periods/yr.
		private static double SharpeOfReturns(List<double> rets)
		{
			if (rets.Count < 2) return 0.0;
			double mean = rets.Average();
			double variance = rets.Sum(x => (x - mean) * (x - mean)) / (rets.Count - 1);
			double sd = Math.Sqrt(variance);
			return sd > 0.0 ? mean / sd * Math.Sqrt(BankrollSimulator.PeriodsPerYear) : 0.0;
		}

		// Pearson correlation; 0 when either series has no variance.
		private static double ProbCorr(List<double> xs, List<double> ys)
		{
			int n = Math.Min(xs.Count, ys.Count);
			if (n < 2) return 0.0;
			double mx = xs.Average(), my = ys.Average();
			double sxy = 0, sxx = 0, syy = 0;
			for (int i = 0; i < n; i++)
			{
				double dx = xs[i] - mx, dy = ys[i] - my;
				sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
			}
			double denom = Math.Sqrt(sxx * syy);
			return denom > 0 ? sxy / denom : 0.0;
		}

		// ============================================================================
		// VOL-TARGET BASELINE, WALK-FORWARD.
		// The decisive test: does the strategy's ACTIVE exposure timing beat a PASSIVE,
		// risk-matched volatility-target baseline out-of-sample? The baseline is long-only
		// and has NO directional view — it just sizes exposure inversely to past realized
		// vol: position = clamp(targetVol / realizedVol, 0, 1). Its single knob (targetVol)
		// is calibrated on each fold's TRAIN slice to MATCH the strategy's average exposure
		// there (same risk budget), then scored on the next out-of-sample block. Expanding-
		// window walk-forward; OOS blocks are pooled per symbol. Sharpe is the decider
		// (scale-invariant, so the exposure-level match doesn't bias it). If the strategy
		// doesn't beat this baseline OOS, its machinery isn't buying risk-adjusted edge over
		// mechanical de-risking. Look-ahead-free: vol uses only past returns, same EWMA (and
		// same bar-skip) as the simulator, so vols[k] aligns 1:1 with the recorded strategy
		// bars.
		// ============================================================================
		public static double VolTargetWfTrainFraction = 0.50;   // first fold trains on the front half
		public static int    VolTargetWfFolds         = 5;      // OOS test blocks over the back half

		public static List<VolTargetWfRow> VolTargetWalkForward(
			Dictionary<string, List<OhlcBar>> barsBySymbol, double initialBankroll = 10_000.0)
		{
			var rows = new List<VolTargetWfRow>();

			bool savedRec = BankrollSimulator.RecordBars;
			try
			{
				BankrollSimulator.RecordBars = true;
				foreach (var (sym, bars) in barsBySymbol)
				{
					if (bars.Count < 3) continue;
					var sim = BankrollSimulator.Run(bars, initialBankroll);
					var stratPos = sim.BarPositions;
					var bhRet    = sim.BarBhReturns;
					if (stratPos.Count == 0) continue;

					// realized-vol series aligned 1:1 with the recorded strategy bars
					var vols = BuildAlignedVolSeries(bars);
					if (vols.Count != stratPos.Count) continue;   // alignment safety

					int nRec  = stratPos.Count;
					int start = (int)(nRec * VolTargetWfTrainFraction);
					int folds = VolTargetWfFolds;
					int blockLen = folds > 0 ? (nRec - start) / folds : 0;
					if (start < 20 || blockLen < 10) continue;

					var sR = new List<double>(); var vR = new List<double>(); var bR = new List<double>();
					var sExp = new List<double>(); var vExp = new List<double>();
					var tvs = new List<double>();
					int usedFolds = 0;

					for (int f = 0; f < folds; f++)
					{
						int trainEnd = start + f * blockLen;
						int testEnd  = (f == folds - 1) ? nRec : trainEnd + blockLen;
						if (testEnd <= trainEnd) break;

						// calibrate targetVol on [0, trainEnd) to match the strategy's avg exposure there
						double stratAvgExpTrain = 0.0;
						for (int k = 0; k < trainEnd; k++) stratAvgExpTrain += stratPos[k];
						stratAvgExpTrain /= trainEnd;
						double tv = CalibrateTargetVol(vols, trainEnd, stratAvgExpTrain);
						tvs.Add(tv);
						usedFolds++;

						for (int k = trainEnd; k < testEnd; k++)
						{
							double vtPos = Math.Min(1.0, tv / vols[k]);
							sR.Add(stratPos[k] * bhRet[k]);
							vR.Add(vtPos * bhRet[k]);
							bR.Add(bhRet[k]);
							sExp.Add(stratPos[k]);
							vExp.Add(vtPos);
						}
					}

					if (sR.Count == 0) continue;

					rows.Add(new VolTargetWfRow
					{
						Symbol        = sym,
						Hv            = Volatility.AnnualizedHistoricalPct(bars),
						Folds         = usedFolds,
						OosBars       = sR.Count,
						TargetVolMean = tvs.Count > 0 ? tvs.Average() : 0.0,
						StratSharpe = SharpeOfReturns(sR), StratDd = MaxDdOf(sR), StratRet = TotalRet(sR), StratAvgExp = sExp.Average() * 100.0,
						VtSharpe    = SharpeOfReturns(vR), VtDd    = MaxDdOf(vR), VtRet    = TotalRet(vR), VtAvgExp    = vExp.Average() * 100.0,
						BhSharpe    = SharpeOfReturns(bR), BhDd    = MaxDdOf(bR), BhRet    = TotalRet(bR),
					});
				}
			}
			finally { BankrollSimulator.RecordBars = savedRec; }

			return rows.OrderBy(r => r.Hv).ToList();
		}

		// Realized-vol EWMA series that matches the simulator EXACTLY: same VolEmaPeriod, same
		// annualization, and the same bar-skip (only bars where the short-term state is defined
		// are emitted, and the EWMA only advances on those bars) — so vols[k] is the vol
		// as-of-prev for the k-th recorded strategy bar.
		private static List<double> BuildAlignedVolSeries(List<OhlcBar> bars)
		{
			var vols = new List<double>();
			var stE = new CandleStateEngine();
			double volAlpha = 2.0 / (BankrollSimulator.VolEmaPeriod + 1);
			double varEwma = double.NaN;
			for (int i = 2; i < bars.Count; i++)
			{
				var st = stE.Update(bars[i - 2], bars[i - 1]);
				if (st == null) continue;
				double lr = bars[i - 2].Close > 0 ? Math.Log(bars[i - 1].Close / bars[i - 2].Close) : 0.0;
				double r2 = lr * lr;
				varEwma = double.IsNaN(varEwma) ? r2 : volAlpha * r2 + (1.0 - volAlpha) * varEwma;
				double volPct = Math.Sqrt(varEwma) * Math.Sqrt(BankrollSimulator.PeriodsPerYear) * 100.0;
				vols.Add(Math.Max(volPct, 1e-6));
			}
			return vols;
		}

		// Find target vol tv so the long-only vol-target's average exposure over the first
		// `count` records — mean_k min(1, tv/vol_k) — equals targetAvgExp. Monotonic in tv → bisect.
		private static double CalibrateTargetVol(List<double> vols, int count, double targetAvgExp)
		{
			if (targetAvgExp <= 0.0) return 0.0;
			double hi = 0.0;
			for (int k = 0; k < count; k++) if (vols[k] > hi) hi = vols[k];
			if (targetAvgExp >= 1.0 || hi <= 0.0) return hi;   // tv = max(vol) => everything clamps to 1
			double lo = 0.0;
			for (int iter = 0; iter < 60; iter++)
			{
				double mid = 0.5 * (lo + hi);
				double avg = 0.0;
				for (int k = 0; k < count; k++) avg += Math.Min(1.0, mid / vols[k]);
				avg /= count;
				if (avg < targetAvgExp) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
		}

		// Compounded total return (%) of a per-bar return series.
		private static double TotalRet(List<double> rets)
		{
			double eq = 1.0;
			foreach (var r in rets) eq *= 1.0 + r;
			return (eq - 1.0) * 100.0;
		}

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

	// One point on a single symbol's 1-D LongBias sweep (all other knobs fixed).
	public class LongBiasPoint
	{
		public double LongBias;
		public double Sharpe;
		public double ReturnPct;
		public double MaxDdPct;
		// return per unit of max drawdown (Calmar-like); guarded against tiny drawdowns.
		public double RetPerDd => MaxDdPct > 0.01 ? ReturnPct / MaxDdPct : double.NaN;
	}

	// A single symbol's 1-D LongBias-vs-volatility result: the full sweep curve plus the
	// LongBias maximizing Sharpe (and Calmar), tagged with the symbol's HV. Every knob
	// except LongBias is held at the caller's configuration.
	public class LongBiasVolRow
	{
		public string Symbol = "";
		public double HistoricalVolatilityPct;
		public int    Bars;
		public List<LongBiasPoint> Curve = new();

		public double BestSharpeBias;   // argmax-Sharpe LongBias
		public double BestSharpe;
		public double BestCalmarBias;   // argmax-(return/drawdown) LongBias
		public double BestCalmar;

		public double RefBias;          // the configured default LongBias
		public double RefSharpe;        // Sharpe at the swept value closest to RefBias
	}

	// One exposure level (or decile bin) with the empirical up-close odds AND forward-risk
	// stats measured on it.
	public class ProbExposureBin
	{
		public double Exposure;        // target-exposure value, or mean EMA-exposure of the bin
		public int    N;
		public int    Ups;             // # of bars whose next close was above the prior close
		public double UpRatePct => N > 0 ? (double)Ups / N * 100.0 : 0.0;
		public double MeanFwdRetPct;
		// forward-risk view: is the NEXT bar riskier when exposure is low?
		public double FwdVolPct;       // std of the forward returns in the bin
		public double DownsideDevPct;  // sqrt(mean(min(r,0)^2)) — downside semi-deviation
		public double DownRatePct;     // P(next return < 0)
	}

	// Probability<->exposure calibration for one scope (the whole basket, or one symbol).
	public class ProbExposureResult
	{
		public string Scope = "";
		public double Hv;
		public int    N;
		public double BaseUpRatePct;   // unconditional odds of an up-close
		public double MeanRetPct;
		public double CorrExpUp;       // point-biserial corr(target exposure, up-close)
		public double CorrExpRet;      // corr(target exposure, forward return)
		public double CorrExpAbsRet;   // corr(target exposure, |forward return|): - => higher exp calmer
		public double CorrExpDownside; // corr(target exposure, min(r,0)): + => higher exp = less downside
		public List<ProbExposureBin> Levels  = new();   // discrete target-exposure values
		public List<ProbExposureBin> Deciles = new();   // continuous EMA-of-target deciles
	}

	// De-risking-vs-timing control for one symbol: the strategy's drawdown against a CONSTANT
	// position held at the strategy's own average exposure. If the strategy's drawdown is
	// lower than the matched-constant one, the reduction is genuine risk TIMING; if they are
	// ~equal, the strategy just holds less on average (plain de-risking, no skill).
	public class RiskTimingRow
	{
		public string Symbol = "";
		public double Hv;
		public double AvgExposurePct;   // mean applied position (the constant benchmark's size)
		public double StratDd, ConstDd, BhDd;
		public double StratSharpe, ConstSharpe;
		public double DdSaved => ConstDd - StratDd;   // + => timing beats matched de-risking
	}

	// One fixed-width exposure bucket with its average forward return.
	public class ExpCurveBin
	{
		public double Lo, Hi, Center;
		public int    N;
		public double MeanFullPct;   // avg full-day forward return in the bucket
		public double MeanOnPct;     // avg overnight return
		public double UpPct;
	}

	// Exposure->return curve for one symbol, with linear + quadratic fits.
	public class ExpCurveResult
	{
		public string Scope = "";
		public double Hv;
		public int    N;
		public double MeanExp;
		public double Corr, Slope, Intercept, R2;   // linear: return% ~ a + slope*exposure
		public double QuadA, QuadB, QuadC, QuadR2;  // quadratic: a + b*x + c*x^2 (c = curvature)
		public List<ExpCurveBin> Bins = new();
	}

	// One exposure level: up-rate and mean return for overnight / full-day / intraday.
	public class ExposureGapBin
	{
		public double Exposure;
		public int    N;
		public double OnUpPct, FullUpPct, IdUpPct;
		public double OnRetPct, FullRetPct, IdRetPct;
	}

	// Exposure-vs-gap calibration for one scope (basket or symbol).
	public class ExposureGapResult
	{
		public string Scope = "";
		public double Hv;
		public int    N;
		public double BaseOnUp, BaseFullUp, BaseIdUp;
		public double BaseOnRet, BaseFullRet, BaseIdRet;
		public double CorrOnUp, CorrFullUp, CorrIdUp;     // corr(exposure, up-indicator)
		public double CorrOnRet, CorrFullRet;             // corr(exposure, return)
		public List<ExposureGapBin> Levels = new();
	}

	// One scalar feature screened against the next-bar return: in-sample association +
	// an out-of-sample long/flat rule vs buy&hold.
	public class FeatureScreenRow
	{
		public string Name = "";
		public int    N;
		public double Corr;            // corr(feature, next-bar return)
		public double QSpreadRet;      // top-quintile minus bottom-quintile mean fwd return (pp)
		public double QSpreadUp;       // top-minus-bottom up-rate (pp)
		public double OosRuleSharpe;   // long/flat rule (dir from train) on the test slice
		public double OosBhSharpe;     // buy&hold on the same test slice
		public double PctLong;         // % of test bars the rule was long
		public double OosEdge => OosRuleSharpe - OosBhSharpe;   // + => feature times the tape OOS
	}

	// One return segment (overnight / intraday / full) with annualized stats.
	public class SegmentRow
	{
		public string Name = "";
		public double AnnMeanPct, AnnVolPct, Sharpe, CumRetPct;
	}

	// One seasonality bucket (weekday, or turn-of-month vs rest).
	public class DowRow
	{
		public string Name = "";
		public int    N;
		public double MeanRetPct, UpPct;
	}

	// Full signal-screen output for one symbol.
	public class SignalScreenResult
	{
		public string Symbol = "";
		public int    Bars;
		public List<FeatureScreenRow> Features = new();
		public List<SegmentRow>       Segments = new();   // overnight, intraday, full
		public List<DowRow>           Dow      = new();
		public DowRow? Tom;
		public DowRow? Rest;
	}

	// One (Y-up, Z-down) bracket: outcome odds for LT-Bull entries vs random entries.
	public class BracketResult
	{
		public double Up, Down;
		public int    MaxHold;

		public int    BullN;
		public double BullWinPct, BullLossPct, BullTimeoutPct, BullExpectancyPct, BullAvgBars;

		public int    RandN;
		public double RandWinPct, RandLossPct, RandTimeoutPct, RandExpectancyPct;

		public double WinEdge => BullWinPct - RandWinPct;              // + => Bull hits +Y first more often
		public double ExpEdge => BullExpectancyPct - RandExpectancyPct; // + => Bull entries more profitable
	}

	// One LT state's mean return / up-rate across the measured offsets (parallel to
	// GridSearch.StateLagOffsets).
	public class StateLagRow
	{
		public LongTermState State;
		public int[]    N          = System.Array.Empty<int>();
		public double[] MeanRetPct = System.Array.Empty<double>();
		public double[] UpRatePct  = System.Array.Empty<double>();
	}

	// Concurrent-vs-forward return separation for one scope (basket or symbol). Offsets[0]==0
	// is concurrent (the bar the state was computed on); the rest are tradable forward bars.
	public class StateLagResult
	{
		public string Scope = "";
		public double Hv;
		public int[]  Offsets = System.Array.Empty<int>();
		public StateLagRow Bull = new();
		public StateLagRow Bear = new();
		// Bull−Bear mean-return spread at offset index j
		public double Spread(int j) => Bull.MeanRetPct[j] - Bear.MeanRetPct[j];
	}

	// Everything the probability/risk-exposure study produces.
	public class ProbExposureStudyResult
	{
		public List<ProbExposureResult> Calibration = new();   // [0] = basket, then per symbol
		public List<RiskTimingRow>      Timing      = new();   // per symbol, sorted by HV
	}

	// One symbol's out-of-sample walk-forward: the strategy vs a risk-matched passive
	// vol-target vs buy&hold, on pooled OOS blocks. Edge > 0 means the active timing beats
	// the vol-target on OOS Sharpe (the scale-invariant, risk-adjusted-return decider).
	public class VolTargetWfRow
	{
		public string Symbol = "";
		public double Hv;
		public int    Folds;
		public int    OosBars;
		public double TargetVolMean;   // mean calibrated targetVol across folds (diagnostic)

		public double StratSharpe, StratDd, StratRet, StratAvgExp;
		public double VtSharpe,    VtDd,    VtRet,    VtAvgExp;
		public double BhSharpe,    BhDd,    BhRet;

		public double Edge => StratSharpe - VtSharpe;   // + => active timing beats vol-target OOS
	}

	// One symbol: static-LongBias baseline vs dynamic (vol-driven) long bias at one scale.
	public class DynBiasRow
	{
		public string Symbol = "";
		public double Hv;
		public int    Bars;
		public double BaseLb;      // the static LongBias baseline (e.g. 0.5)
		public double Scale;       // VolBiasScale used for the dynamic run
		public double LbAtHv;      // representative dynamic LB at this symbol's HV

		public double StatSharpe, StatDd, StatRet;
		public double DynSharpe,  DynDd,  DynRet;

		public double DShp => DynSharpe - StatSharpe;   // + => dynamic better
		public double DDd  => DynDd - StatDd;           // - => dynamic shallower drawdown
	}

	// One vol->LongBias mapping scored across the basket (normalized dynBias).
	public class DynMapPoint
	{
		public double Pivot, Scale, Floor, Ceil;
		public bool   Flat;          // scale == 0 => constant LB, volatility ignored
		public double MeanSharpe, MeanDd, MeanRet;
		public int    ShpWinsBH;     // # symbols beating buy&hold on Sharpe
		public int    DdWinsBH;      // # symbols with shallower drawdown than buy&hold
		public int    ShpWinsFix;    // # symbols beating the fixed-LongBias baseline on Sharpe
	}

	// Mapping grid-search result plus the fixed-LongBias and buy&hold baselines.
	public class DynMapResult
	{
		public int    N;
		public double FixLb;
		public double FixSharpe, FixDd, FixRet;   public int FixShpWinsBH, FixDdWinsBH;
		public double BhSharpe,  BhDd,  BhRet;
		public List<DynMapPoint> Ranked = new();

		// best mapping that actually uses volatility (scale > 0) and best flat one
		public DynMapPoint? BestOverall => Ranked.Count > 0 ? Ranked[0] : null;
		public DynMapPoint? BestVarying => Ranked.FirstOrDefault(p => !p.Flat);
		public DynMapPoint? BestFlat    => Ranked.FirstOrDefault(p => p.Flat);
	}

	// One symbol: baseline vs volatility-scaled exposure at one MinExposure setting.
	public class VolScaleRow
	{
		public string Symbol = "";
		public double Hv;
		public int    Bars;
		public double MinExp;

		public double BaseSharpe, BaseDd, BaseRet;
		public double SclSharpe,  SclDd,  SclRet;

		public double DShp => SclSharpe - BaseSharpe;   // + => vol-scaled better
		public double DDd  => SclDd - BaseDd;           // - => vol-scaled shallower drawdown
	}

	// Basket-mean metrics for one VolBiasScale, against the fixed static baseline.
	public class DynBiasScalePoint
	{
		public double Scale;
		public int    N;
		public double MeanBaseSharpe, MeanBaseDd, MeanBaseRet;
		public double MeanDynSharpe,  MeanDynDd,  MeanDynRet;
		public int    ShpWins, DdWins;   // # symbols where dynamic beats static
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
