namespace StockOdds
{
	public enum LongTermState
	{
		Bull,
		Bear
	}

	public class LongTermStateEngine
	{
		private int bullCount = 0;
		private int bearCount = 0;

		// Anchors trail the sequence: they sit on the 2ND-TO-LAST candle of the current
		// bull/bear sequence (the low of the penultimate bull candle / the high of the
		// penultimate bear candle), advancing by one as each new candle extends the run.
		private double? bullAnchor = null;
		private double? bearAnchor = null;

		// low/high of the most recent (last) candle in the current sequence
		private double? lastBullLow = null;
		private double? lastBearHigh = null;

		private LongTermState state = LongTermState.Bear;

		public LongTermState Update(OhlcBar prev, OhlcBar current)
		{
			var candle = GetCandleType(prev, current);

			// -------------------------
			// BULL CANDLE
			// -------------------------
			if (candle == CandleType.Bull)
			{
				bearCount = 0;

				bullCount++;

				// The prior "last" bull candle becomes the 2nd-to-last -> new anchor.
				// The first candle of a fresh sequence has no 2nd-to-last, so it only
				// seeds the "last" low without moving the anchor.
				if (bullCount >= 2)
					bullAnchor = lastBullLow;
				lastBullLow = current.Low;

				// confirmation condition
				if (bullCount >= 2 && bearAnchor.HasValue && current.Close > bearAnchor.Value)
				{
					state = LongTermState.Bull;
				}
			}

			// -------------------------
			// BEAR CANDLE
			// -------------------------
			else if (candle == CandleType.Bear)
			{
				bullCount = 0;

				bearCount++;

				// symmetric: anchor trails to the 2nd-to-last bear candle's high
				if (bearCount >= 2)
					bearAnchor = lastBearHigh;
				lastBearHigh = current.High;

				// exit / bearish transition condition
				if (bearCount >= 2 && bullAnchor.HasValue && current.Close < bullAnchor.Value)
				{
					state = LongTermState.Bear;
				}
			}

			// -------------------------
			// NEUTRAL CANDLE
			// -------------------------
			else
			{
				// neutral does nothing except preserve sequences
			}

			return state;
		}

		private static CandleType GetCandleType(OhlcBar prev, OhlcBar current)
		{
			if (current.Close > prev.High)
				return CandleType.Bull;

			if (current.Close < prev.Low)
				return CandleType.Bear;

			return CandleType.Neutral;
		}
	}
}
