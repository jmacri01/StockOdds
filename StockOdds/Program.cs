using StockOdds;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

class Program
{
	static string SYMBOL = "mstr";//"^GSPC";
	static string INTERVAL = "1d";//"1d";
	// Only simulate bars on/after this date. Set to DateTime.MinValue for all history.
	static DateTime START_DATE = new DateTime(2020, 1, 1);

	// When true, sweep the smoothing knobs for the best Sharpe and print the top
	// combinations instead of running the usual single bankroll simulation.
	static bool RUN_GRID_SEARCH = true;

	// When RUN_GRID_SEARCH is on, pick one mode:
	//   BiasSweep      -> 2-D sweep of BiasPeriod x BiasEmaPeriod (other knobs fixed) to find
	//                     the smallest pair that maintains performance on the deployment set.
	//   KnobRank       -> where the currently-configured smoothing knobs rank in the full
	//                     grid over the deployment universe (HV-filtered), full window.
	//   VolDeploy      -> short-side A/B (Min 0% vs -100%) + volatility-threshold deployment
	//                     sweep, both over the full window (fixed params).
	//   FullWindow     -> strategy vs buy & hold over the full window (fixed params, no
	//                     tuning) on Sharpe / max drawdown / return-per-drawdown.
	//   RollingBuckets -> rolling walk-forward over the (LT,ST) BUCKET-WEIGHT map shape,
	//                     re-tuned each fold (does the map itself carry an OOS edge?).
	//   Rolling        -> rolling walk-forward over the SMOOTHING knobs.
	//   WalkForward    -> single split: tuned-per-symbol vs. global default on held-out test.
	//   VolStudy       -> tune each symbol to its OWN best knobs; print (HV -> knobs) + corr.
	//   LongBiasStudy  -> 1-D sweep of ONLY LongBias per symbol (all other knobs fixed);
	//                     print (HV -> optimal LongBias) curve + correlation. Clean test of
	//                     "optimal LongBias falls as volatility rises."
	//   DynBiasStudy   -> per-candle LongBias derived from a vol EWMA (dynLB = scale·ln(pivot/vol));
	//                     static baseline vs dynamic per symbol + a scale sweep.
	//   VolScaleStudy  -> vol-SCALED exposure (adjEma *= pivot/vol long, vol/pivot short):
	//                     baseline vs scaled per symbol, at MinExposure 0% and -100%.
	//   NormBiasStudy  -> dynamic LongBias but with dynBias normalized by its MAX possible
	//                     value (bounded to [-1,1]); static baseline vs dynamic + scale sweep.
	//   DynMapSearch   -> grid-search the vol->LongBias mapping (pivot/scale/floor/ceil) vs
	//                     buy&hold and fixed LongBias; does the winner even use volatility?
	//   NormStaticStudy-> fixed LongBias A/B: plain dynBias vs normalized (bounded to [-1,1]).
	//   ProbExposureStudy-> calibrate P(next close > prev close) against the per-candle TARGET
	//                     exposure; per-level up-rate table + continuous curve + corr. Does the
	//                     exposure signal carry directional odds we could invert to a map?
	//   VolTargetWf    -> walk-forward: strategy vs a risk-matched PASSIVE vol-target baseline
	//                     (exposure = clamp(targetVol/past-vol, 0, 1)), OOS. Does the active
	//                     timing beat mechanical de-risking on out-of-sample Sharpe?
	//   StateLagStudy  -> LT state vs return at offset 0 (concurrent, as charted) vs +1,+2,...
	//                     (tradable). Shows the visible "Bull up / Bear down" pattern is the
	//                     state LABELING the current move, and vanishes by the next bar.
	//   BarrierStudy   -> RUN-level triple-barrier: entering long on an LT-Bull turn, P(reach
	//                     +Y% before −Z%) vs the same brackets from RANDOM entries. Tests path
	//                     asymmetry (trend edge) that the per-bar direction test can't see.
	//   SignalScreen   -> screen fresh look-ahead-free features (momentum, reversal, dist-from-MA,
	//                     vol regime), the overnight/intraday split, and seasonality for ANY
	//                     forward edge, each with an OOS long/flat-vs-B&H check. For a single name.
	//   BasketMean     -> single knob combo with the best MEAN Sharpe across the basket.
	enum GridMode { BiasSweep, KnobRank, VolDeploy, FullWindow, RollingBuckets, Rolling, WalkForward, VolStudy, LongBiasStudy, DynBiasStudy, VolScaleStudy, NormBiasStudy, DynMapSearch, NormStaticStudy, ProbExposureStudy, VolTargetWf, StateLagStudy, BarrierStudy, SignalScreen, BasketMean }
	static GridMode GRID_MODE = GridMode.SignalScreen;

	// Basket for the grid search. For the volatility study, spread it across low-HV
	// (indices/mega-caps) to high-HV (small/speculative) names so the relationship shows.
	static string[] GRID_SYMBOLS =
		{ "^gspc" };
	// Full basket (restore when done focusing on ^GSPC):
	// { "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr", "smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };

	// Window used ONLY by the grid search / studies, independent of START_DATE (which
	// governs the normal single-symbol run). Yahoo caps history at ~5y, so an early date
	// here means "use everything available" — which includes the 2022 bear market.
	static DateTime GRID_START_DATE = new DateTime(2020, 1, 1);

	static async Task Main()
	{
		// -------------------------
		// 1. FETCH DATA
		// -------------------------
		var bars = await YahooClient.GetBarsAsync(SYMBOL, INTERVAL);
		bars = bars.Where(b => b.Date >= START_DATE).ToList();

		// -------------------------
		// 2. INIT ENGINES
		// -------------------------
		var ltEngine = new LongTermStateEngine();
		var stEngine = new CandleStateEngine();

		var episodes = new List<StateEpisode>();
		StateEpisode? current = null;

		// -------------------------
		// 3. RUN STATE MACHINE OVER TIME
		// -------------------------
		// The state is computed from (prevPrev, prev), so `prev` is the bar that
		// triggers the transition. When the (LT, ST) tuple changes, `prev.Close`
		// is both the exit of the old episode and the entry of the new one.
		for (int i = 2; i < bars.Count; i++)
		{
			var prevPrev = bars[i - 2];
			var prev = bars[i - 1];

			var lt = ltEngine.Update(prevPrev, prev);
			var st = stEngine.Update(prevPrev, prev);

			if (st == null)
				continue;

			bool newEpisode = current == null || current.LT != lt || current.ST != st.Value;

			if (newEpisode)
			{
				// close the previous episode at the transition bar's close
				if (current != null)
				{
					current.ExitDate = prev.Date;
					current.ExitClose = prev.Close;
					current.ExitIndex = i - 1;
					current.IsClosed = true;
				}

				current = new StateEpisode
				{
					LT = lt,
					ST = st.Value,
					EntryDate = prev.Date,
					EntryClose = prev.Close,
					EntryIndex = i - 1,
				};
				episodes.Add(current);
			}
		}

		// -------------------------
		// 4. PRICE CHANGE BY STATE (LT × ST)
		// -------------------------
		Console.WriteLine("\n===== PRICE CHANGE BY STATE =====");

		var buckets = StateChangeMetricsEngine.Compute(episodes);
		StateChangePrinter.Print(buckets);

		// -------------------------
		// 5. BANKROLL SIMULATION
		// -------------------------
		// Each candle's target exposure is looked up by its (LT, ST) bucket, smoothed by
		// an EMA, skewed by a dynamic long bias, only rebalanced when it drifts past a
		// threshold, then clamped to the configured min/max. Tune the knobs here:
		BankrollSimulator.ExposureEmaPeriod = 12;
		BankrollSimulator.BiasPeriod = 15;
		BankrollSimulator.LongBias = 0.5;
		BankrollSimulator.BiasEmaPeriod = 150;
		BankrollSimulator.RebalanceDriftPercent = 30;
		BankrollSimulator.MinExposurePercent    = 0.0;    // position clamp low
		BankrollSimulator.MaxExposurePercent    = 100.0;  // position clamp high

		//BankrollSimulator.BullBull = 1.0;
		//BankrollSimulator.BullBullNeutral = 0.5;
		//BankrollSimulator.BullBearNeutral = 0.0;
		//BankrollSimulator.BullBear = -0.5;

		//BankrollSimulator.BearBull = 0.5;
		//BankrollSimulator.BearBullNeutral = 0;
		//BankrollSimulator.BearBearNeutral = -0.5;
		//BankrollSimulator.BearBear = -1.0;

		BankrollSimulator.BullBull = 1.0;
		BankrollSimulator.BullBullNeutral = 0.5;
		BankrollSimulator.BullBearNeutral = 0.0;
		BankrollSimulator.BullBear = -0.5;

		BankrollSimulator.BearBull = 0.5;
		BankrollSimulator.BearBullNeutral = 0;
		BankrollSimulator.BearBearNeutral = -0.5;
		BankrollSimulator.BearBear = -1.0;

		// -------------------------
		// 6. GRID SEARCH (optional) — sweep the smoothing knobs for the best Sharpe.
		//    The (LT, ST) bucket weights configured above are held fixed; only the five
		//    smoothing knobs move. Candidate value sets live in GridSearch (override
		//    e.g. GridSearch.LongBiases before calling Run to change the grid).
		// -------------------------
		if (RUN_GRID_SEARCH)
		{
			// Fetch each symbol in the basket over the same window as the main run.
			var barsBySymbol = new Dictionary<string, List<OhlcBar>>();
			foreach (var sym in GRID_SYMBOLS)
			{
				try
				{
					var symBars = (await YahooClient.GetBarsAsync(sym, INTERVAL))
						.Where(b => b.Date >= GRID_START_DATE).ToList();
					if (symBars.Count >= 3)
						barsBySymbol[sym] = symBars;
					else
						Console.WriteLine($"  skipping {sym}: only {symBars.Count} bars");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"  skipping {sym}: {ex.Message}");
				}
			}

			if (GRID_MODE is GridMode.FullWindow or GridMode.VolDeploy or GridMode.BiasSweep or GridMode.ProbExposureStudy or GridMode.VolTargetWf or GridMode.StateLagStudy or GridMode.BarrierStudy or GridMode.SignalScreen)
				Console.WriteLine($"\nComparing over the full window x {barsBySymbol.Count} symbols...");
			else
			{
				long combos = GRID_MODE == GridMode.RollingBuckets ? GridSearch.BucketGridSize : GridSearch.GridSize;
				Console.WriteLine($"\nRunning grid search over {combos} combinations x {barsBySymbol.Count} symbols...");
			}

			switch (GRID_MODE)
			{
				case GridMode.BiasSweep:
					var bs = GridSearch.BiasSweep(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintBiasSweep(bs);
					break;
				case GridMode.KnobRank:
					var kr = GridSearch.KnobRank(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintKnobRank(kr);
					break;
				case GridMode.VolDeploy:
					double[] thresholds = { 0, 25, 50, 75, 100 };
					var noShort   = GridSearch.FullWindowCompareWithMin(barsBySymbol, 0.0,    initialBankroll: 10_000.0);
					var withShort = GridSearch.FullWindowCompareWithMin(barsBySymbol, -100.0, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintShortAb(noShort, withShort);
					GridSearchPrinter.PrintVolThreshold("No short (Min 0%)", GridSearch.VolThresholdSweep(noShort, thresholds));
					GridSearchPrinter.PrintVolThreshold("Full short (Min -100%)", GridSearch.VolThresholdSweep(withShort, thresholds));
					break;
				case GridMode.FullWindow:
					var fw = GridSearch.FullWindowCompare(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintFullWindow(fw);
					break;
				case GridMode.RollingBuckets:
					var rollB = GridSearch.RollingWalkForwardBuckets(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintRollingBuckets(rollB);
					break;
				case GridMode.Rolling:
					var roll = GridSearch.RollingWalkForward(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintRolling(roll);
					break;
				case GridMode.WalkForward:
					var wf = GridSearch.WalkForward(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintWalkForward(wf);
					break;
				case GridMode.VolStudy:
					var optima = GridSearch.RunPerSymbol(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintPerSymbolOptima(optima);
					break;
				case GridMode.LongBiasStudy:
					var lbv = GridSearch.LongBiasVsVol(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintLongBiasVsVol(lbv);
					break;
				case GridMode.DynBiasStudy:
					var dynRows = GridSearch.DynBiasCompare(barsBySymbol, GridSearch.DynCompareScale, initialBankroll: 10_000.0);
					var dynSweep = GridSearch.DynBiasScaleSweep(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintDynBias(dynRows, dynSweep);
					break;
				case GridMode.VolScaleStudy:
					var vs0   = GridSearch.VolScaleCompare(barsBySymbol,    0.0, initialBankroll: 10_000.0);
					var vsNeg = GridSearch.VolScaleCompare(barsBySymbol, -100.0, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintVolScale(new() { (0.0, vs0), (-100.0, vsNeg) });
					break;
				case GridMode.NormBiasStudy:
					GridSearch.DynNormalize = true;   // dynBias divided by its max => bounded to [-1,1]
					Console.WriteLine("(dynBias normalized by MAX possible value => bounded to [-1,1]; LongBias sets short-side compression)");
					var nbRows  = GridSearch.DynBiasCompare(barsBySymbol, GridSearch.DynCompareScale, initialBankroll: 10_000.0);
					var nbSweep = GridSearch.DynBiasScaleSweep(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintDynBias(nbRows, nbSweep);
					break;
				case GridMode.DynMapSearch:
					var mapRes = GridSearch.DynMapSearch(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintDynMap(mapRes);
					break;
				case GridMode.NormStaticStudy:
					var nsRows = GridSearch.NormStaticCompare(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintNormStatic(nsRows, BankrollSimulator.LongBias);
					break;
				case GridMode.ProbExposureStudy:
					var pe = GridSearch.ProbExposure(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintProbExposure(pe);
					break;
				case GridMode.VolTargetWf:
					var vtwf = GridSearch.VolTargetWalkForward(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintVolTargetWf(vtwf);
					break;
				case GridMode.StateLagStudy:
					var sl = GridSearch.StateLag(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintStateLag(sl);
					break;
				case GridMode.BarrierStudy:
					var br = GridSearch.BarrierStudy(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintBarrier(br);
					break;
				case GridMode.SignalScreen:
					var ss = GridSearch.SignalScreen(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintSignalScreen(ss);
					break;
				default:
					var grid = GridSearch.RunMulti(barsBySymbol, initialBankroll: 10_000.0);
					GridSearchPrinter.PrintMulti(grid, barsBySymbol.Keys, top: 20);
					break;
			}
			return;
		}

		var bankroll = BankrollSimulator.Run(bars, initialBankroll: 10_000.0);
		BankrollPrinter.Print(bankroll);
	}
}