using StockOdds;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

class Program
{
	static string SYMBOL = "mstr";//"^GSPC";
	static string INTERVAL = "1d";//"1d";

	static async Task Main()
	{
		// -------------------------
		// 1. FETCH DATA
		// -------------------------
		var bars = await YahooClient.GetBarsAsync(SYMBOL, INTERVAL);

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
		// Pyramiding exposure: on an LT state change, exposure resets to 25% (long in
		// LT-Bull, short in LT-Bear). Each confirming candle (bull in LT-Bull, bear in
		// LT-Bear) adds +25% up to 100%; exposure is sticky otherwise.
		var bankroll = BankrollSimulator.Run(bars, initialBankroll: 10_000.0);
		BankrollPrinter.Print(bankroll);
	}
}