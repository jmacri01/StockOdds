namespace StockOdds
{
	// One contiguous run where (LongTermState, ShortTermState) stays the same.
	// Entry/exit are the CLOSES of the bars that triggered the transitions in and
	// out of the state, so one episode's exit price equals the next one's entry.
	public class StateEpisode
	{
		public LongTermState LT { get; set; }
		public ShortTermState ST { get; set; }

		public DateTime EntryDate { get; set; }
		public double EntryClose { get; set; }   // close of the bar that triggered entry
		public int EntryIndex { get; set; }      // bar index of the entry trigger

		public DateTime ExitDate { get; set; }
		public double ExitClose { get; set; }     // close of the bar that triggered the exit
		public int ExitIndex { get; set; }        // bar index of the exit trigger

		// false for the final, still-open episode (never transitioned out)
		public bool IsClosed { get; set; }

		// number of bars the state spanned (entry trigger -> exit trigger)
		public int Duration => ExitIndex - EntryIndex;

		// % price change across the state (entry close -> exit close)
		public double PctChange => (ExitClose - EntryClose) / EntryClose * 100.0;
	}
}
