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

	// Run an ad-hoc research harness (MinCallSweep / BearBearCapSweep) instead of the grid search. Off = normal flow.
	static bool RUN_RESEARCH_SWEEP = true;

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

	static async Task Main(string[] args)
	{
		// COLLECTOR MODE: `dotnet run -- collect`. Kept as an argument rather than a flag in this file so an OS
		// scheduler can drive it without anyone editing source. These three feeds are SNAPSHOTS -- none of them
		// backfills, so a day not collected is a day lost forever. Run once per session, shortly after the close.
		if (args.Length > 0 && args[0].Equals("collect", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine($"[collect] {DateTime.Now:yyyy-MM-dd HH:mm}");
			try { var g = await GexClient.GetAsync(refresh: true); Console.WriteLine($"[collect] SqueezeMetrics GEX: {g.Count} days -> {g[^1].Date:yyyy-MM-dd}"); }
			catch (Exception ex) { Console.WriteLine($"[collect] GexClient FAILED: {ex.Message}"); }
			try { await CboeGexSnapshot.Run("_SPX", "SPX"); }
			catch (Exception ex) { Console.WriteLine($"[collect] CboeGexSnapshot FAILED: {ex.Message}"); }
			try { await CreditSpreadQuoteValidator.Run(); }
			catch (Exception ex) { Console.WriteLine($"[collect] CreditSpreadQuoteValidator FAILED: {ex.Message}"); }
			return;
		}

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
		BankrollSimulator.ExposureEmaPeriod = 5;
		BankrollSimulator.BiasPeriod = 15;
		BankrollSimulator.BiasEmaPeriod = 150;
		BankrollSimulator.RebalanceDriftPercent = 30;
		BankrollSimulator.AccurateFullSizing    = true;   // size to full when the target saturates (don't lag at 0.7)
		BankrollSimulator.MinExposurePercent    = 0.0;    // position clamp low
		BankrollSimulator.MaxExposurePercent    = 150.0;  // position clamp high (1.5x leverage; ceiling 200)
		BankrollSimulator.RsiOverlayPeriod = 2;           // RSI overbought-trim overlay
		// The RSI trim's numerator is min(cap, max(floor, slope * HV)). The cap now depends on how far the close sits
		// above its KAMA -- effectively no cap below +12%, a tight 30 at/above it. The slope is what actually does the
		// trimming (it binds on ~87% of the universe); the cap only bites when 0.6*HV exceeds it, so this layer is
		// inert for names under HV 50 and acts progressively on the high-vol tail. See BankrollSimulator for the
		// measurements and the three controls that qualified it.
		BankrollSimulator.RsiMultNumerator = 1000;        // no cap below the threshold (would need HV > 1667 to bind)
		BankrollSimulator.ExtTrimThreshold = 12;          // "extended" = close >= 12% above its KAMA
		BankrollSimulator.ExtTrimCap = 35;                // ...where the numerator is capped at 35 instead
		BankrollSimulator.ExtTrimSlope = 0;               // 0 = keep the normal slope in the extended zone
		BankrollSimulator.HvTrimSlope = 0.6;              // HV-conditioned trim: harder on low-vol candles (0 = off)
		BankrollSimulator.HvTrimFloor = 8;                // floor on the scaled N (hardest trim on the quietest candles)
		BankrollSimulator.PositionSmoothPeriod = 5;       // EMA-smooth the final position (cuts downside, keeps participation) -- the P5 floor
		BankrollSimulator.KamaSmooth = true;              // KAMA-distance smoothing: smooth HARDER the further price is below its KAMA, light P5 at/above it
		BankrollSimulator.KamaSmoothSlope = 4.0;          // ramp: smoothPer = clamp(P5 + slope*below*maxPer, P5, maxPer), below = max(0,(kama-close)/kama)
		BankrollSimulator.KamaSmoothMaxPeriod = 50;       // smoothing-period ceiling (floor = PositionSmoothPeriod). KamaSmooth=false = flat P5
		// Peak-age scaler, BELOW THE KAMA ONLY: position *= clamp(K * dd60/dd30, min, max). dd60 >= dd30 always,
		// and dd30 == dd60 exactly when the 60-bar peak falls inside the last 30 bars -- so the ratio reads PEAK
		// AGE, not depth: ~1 on a fresh pullback from a recent high (scaled to K), large when the 60-bar peak is
		// older than 30 bars and price is grinding back toward a nearer high (up to the cap). That read is only
		// useful below the KAMA -- above it a recent peak is just an uptrend pullback, and cutting it is pure
		// cost (return/drawdown 0.558 -> 0.475 on those bars). Confined, it is +0.029 over a matched-exposure
		// flat haircut; unconfined it is -0.018, i.e. worse than simply holding less.
		BankrollSimulator.DdWindow      = 60;             // long drawdown window
		BankrollSimulator.DdShortWindow = 30;             // short drawdown window
		BankrollSimulator.DdRatioMode = 1;                // 1 = ratio form (default), 0 = off, 2 = recovered-fraction
		BankrollSimulator.DdRatioKamaMode = 1;            // 1 = act only below the KAMA (the whole ballgame; see above)
		BankrollSimulator.DdRatioK    = 0.75;             // participation dial; flat plateau 0.6-0.9, so not a fit
		BankrollSimulator.DdRatioMin  = 0.5;              // hardest de-lever (fresh break down from a recent peak)
		BankrollSimulator.DdRatioMax  = 2.0;              // multiplier ceiling (barely matters)
		BankrollSimulator.DdRatioGate = 0.0;              // 0 = always on; gating it on a minimum dd60 measurably HURT
		BankrollSimulator.DdRatioMinDd = 1.0;             // only act when BOTH dd30 and dd60 exceed this % (else neutral).
		                                                  // Redundant under the KAMA confinement (a fresh high is above its
		                                                  // KAMA anyway); kept as insurance if KamaMode is set to 0

			// Long bias: a per-candle dynamic bias scaled by each candle's z = z(HV) + z(persistence),
			// EMA-smoothed. Defaults are exp / base 1 / decay 0.6, refs calibrated to a ~110-name
			// universe. Quiet/low-vol names get a larger bias, hot names a smaller one.
			// BankrollSimulator.DynBase  = 1.0;   // LongBias at z = 0
			// BankrollSimulator.DynDecay = 0.6;   // exponential steepness
			// Bias-cap default (high-vol screening preset): DynMax raised so the slow-EMA*mult is the
			// real ceiling. LongBias = MAX(MIN(fast EMA(10), slow EMA(150)), DynMin). Defensive tilt
			// (captures runs, more bear-robust, gives up some bull-only Sharpe). Revert to the neutral
			// baseline with DynMax=15, DynSmoothSlow=10.
			// BankrollSimulator.DynSmoothPeriod = 10;   // fast EMA of the per-candle bias
			// BankrollSimulator.DynSmoothSlow   = 150;  // slow EMA (default); =DynSmoothPeriod to disable the min-cap
			// BankrollSimulator.DynMax          = 150;  // raw-bias ceiling (default raised)
			// BankrollSimulator.BiasEmaRatio = false; // ON by default: effLongBias = slow*clamp(slow/fast,0.25,2).
			//                                         // Monotonic mean-reverting tilt: lifts the bias when the fast EMA dipped
			//                                         // below the slow, damps when it spiked above; fast==slow = plain ceiling.
			//                                         // Broad-500 + vs-B&H validated: closes most of the Sharpe gap to B&H and
			//                                         // keeps ~all the drawdown edge (82% of names shallower than B&H).

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

		// TEMP HARNESS: min short-call delta A/B (MinCallSweep.cs). Set false to restore normal flow.
		if (RUN_RESEARCH_SWEEP)
		{
			// MinCallSweep.Run = the full arm x structure x spread table;
			// .Detail = per-symbol for one arm; .OneSymbol = one name across spreads (blow-up forensics).
			await WingDteSweep.Run("SPY");
			return;
		}

		// -------------------------
		// 6. GRID SEARCH (optional) — sweep the smoothing knobs for the best Sharpe.
		//    The (LT, ST) bucket weights configured above are held fixed; only the five
		//    smoothing knobs move. Candidate value sets live in GridSearch (override
		//    e.g. GridSearch.BiasPeriods before calling Run to change the grid).
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