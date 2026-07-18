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
	//   BasketMean     -> single knob combo with the best MEAN Sharpe across the basket.
	enum GridMode { BiasSweep, KnobRank, VolDeploy, FullWindow, RollingBuckets, Rolling, WalkForward, VolStudy, BasketMean }
	static GridMode GRID_MODE = GridMode.BiasSweep;

	// Basket for the grid search. For the volatility study, spread it across low-HV
	// (indices/mega-caps) to high-HV (small/speculative) names so the relationship shows.
	static string[] GRID_SYMBOLS =
		{ "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr", "smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };

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
		BankrollSimulator.ExposureEmaPeriod = 24;
		BankrollSimulator.BiasPeriod = 15;
		BankrollSimulator.LongBias = 0.5;
		BankrollSimulator.BiasEmaPeriod = 150;
		BankrollSimulator.RebalanceDriftPercent = 30;
		BankrollSimulator.MinExposurePercent    = 0.0;    // position clamp low
		BankrollSimulator.MaxExposurePercent    = 100.0;  // position clamp high

			// Long bias: ON by default — a per-candle dynamic bias scaled by each candle's
			// z = z(HV) + z(persistence), EMA-smoothed. Set DynamicLongBias = false to fall back
			// to the fixed LongBias above. Defaults are exp / base 1 / decay 0.6, refs calibrated
			// to a ~110-name universe. Quiet/low-vol names get a larger bias, hot names a smaller one.
			BankrollSimulator.DynamicLongBias = true;
			// BankrollSimulator.DynScale = BankrollSimulator.DynScaleMode.Exponential; // or Linear
			// BankrollSimulator.DynBase  = 1.0;   // LongBias at z = 0
			// BankrollSimulator.DynDecay = 0.6;   // exponential steepness
			// Bias-cap default (high-vol screening preset): DynMax raised so the slow-EMA*mult is the
			// real ceiling. LongBias = MAX(MIN(fast EMA(10), slow EMA(150)*0.5), DynMin). Defensive tilt
			// (captures runs, more bear-robust, gives up some bull-only Sharpe). Revert to the neutral
			// baseline with DynMax=15, DynSmoothSlow=10, DynSlowMult=1.0.
			// BankrollSimulator.DynSmoothPeriod = 10;   // fast EMA of the per-candle bias
			// BankrollSimulator.DynSmoothSlow   = 150;  // slow EMA (default); =DynSmoothPeriod to disable the min-cap
			// BankrollSimulator.DynSlowMult     = 0.5;  // slow-EMA ceiling multiplier (default); 1.0 = no scaling
			// BankrollSimulator.DynMax          = 150;  // raw-bias ceiling (default raised)
			// BankrollSimulator.BiasBlend = 1.0; // 1 = pure dynamic (default), dial down toward 0 for defense
			// BankrollSimulator.DefensiveBias = 0.5; // the blend's defensive-leg bias (LongBias has no effect when dynamic is on)
			// BankrollSimulator.BiasNoInvert = true; // stop the bias flipping a bearish EMA into a net long when
			//                                        // biasEma > 1 (it may trim to flat, not invert). Near-free on
			//                                        // high-vol names; removes the leverage-into-a-crash tail risk.
			// BankrollSimulator.BiasTiming = true;  // OFF by default: "level for direction, window for timing" — modulates
			//                                       // the level skew by clamp(dynBias/biasEma, 0.75, 1.25): up when the recent
			//                                       // window accelerates vs its norm, down when it decelerates. It's a WIN at the
			//                                       // neutral config (uncapped bias — lifts big-winner compounds at equal drawdown),
			//                                       // but at the shipped screening default (dynMax150/slow150/mult0.5) the ceiling
			//                                       // already caps the bias so timing only trims and mildly de-levers the winners.
			//                                       // Turn ON only when reverting to the neutral config (dynMax15/mult1.0).
			// BankrollSimulator.BiasEmaRatio = false; // ON by default: effLongBias = (slow*mult)*clamp(slow/fast,0.25,2).
			//                                         // Monotonic mean-reverting tilt: lifts the bias when the fast EMA dipped
			//                                         // below the slow, damps when it spiked above; fast==slow = plain ceiling.
			//                                         // Broad-500 + vs-B&H validated: closes most of the Sharpe gap to B&H and
			//                                         // keeps ~all the drawdown edge (82% of names shallower than B&H).
			// BankrollSimulator.BiasSplit = false;    // ON by default: rolling sum uses Bull 1+bias/2, Bear -1+bias/2 (bias is a
			//                                         // long tilt on BOTH LT dirs -> conviction persists through LT-Bear chop).
			//                                         // Broad-500 validated: Sharpe up in every HV bucket (0.17->0.20, 63% of
			//                                         // names), edges B&H on Sharpe (0.20 vs 0.19), ~flat DD. false = classic 1+bias/-1.
			// BankrollSimulator.BlendSlope = false;  // ON by default (K=6, slow=120, bars=10, gated). Modulates the
			//                                         // defensive/dynamic blend by the GRADE of the plotted-position momentum:
			//                                         // when EMA12(position) is above EMA120(position) AND rolling over, pull the
			//                                         // blend toward defensive proportional to the descent's steepness (a confirmed
			//                                         // late-run top = empirically low forward return). Broad-500 OOS-neutral (no
			//                                         // Sharpe cost anywhere); on volatile/trending names cuts drawdown and adds
			//                                         // return (110: FW 0.48->0.50, DD 53->52%, ret 166->181%, OOS flat). Off = the
			//                                         // fast-slow gate is essential; slope alone over-trims (turning it off restores
			//                                         // the pre-BlendSlope behaviour). BlendSlopeK / BlendSlowPeriod / BlendSlopeBars tune it.
			// BankrollSimulator.FinalSmooth = false; // ON by default (EMA5). EMA-smooths the FINAL plotted position (the last
			//                                        // output) on top of the drift band — damps residual whipsaw/turnover.
			//                                        // Broad-500 validated: raises OOS Sharpe 0.27->0.28 (the only change to lift
			//                                        // broad OOS rather than tie) and trims ~1pt drawdown, giving up only marginal
			//                                        // return. Keep FinalSmoothPeriod short (3-8); heavy smoothing over-lags.

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

			if (GRID_MODE is GridMode.FullWindow or GridMode.VolDeploy or GridMode.BiasSweep)
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