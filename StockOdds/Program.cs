using StockOdds;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

class Program
{
	static string SYMBOL = "be";//"^GSPC";
	static string INTERVAL = "1d";//"1d";
	// Only simulate bars on/after this date. Set to DateTime.MinValue for all history.
	static DateTime START_DATE = new DateTime(2024, 1, 1);

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
		BankrollSimulator.ExposureEmaPeriod     = 5;      // EMA smoothing of the per-candle target
		BankrollSimulator.BiasPeriod            = 10;     // dynamic-bias LT-direction look-back
		BankrollSimulator.LongBias              = 0;    // Bull-candle weight skew (Bull = LongBias+1, Bear = -1)
		BankrollSimulator.BiasEmaPeriod         = 50;    // EMA smoothing of the dynamic bias
		BankrollSimulator.RebalanceDriftPercent = 20;   // deadband before the position is moved
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

		var bankroll = BankrollSimulator.Run(bars, initialBankroll: 10_000.0);
		BankrollPrinter.Print(bankroll);
	}
}