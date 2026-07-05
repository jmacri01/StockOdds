namespace StockOdds
{
	public enum CandleType
	{
		Bull,
		Bear,
		Neutral
	}

	public enum ShortTermState
	{
		Bull,
		BullNeutral,
		BearNeutral,
		Bear
	}

	public class CandleStateEngine
	{
		private int bullCount;
		private int bearCount;

		private ShortTermState? currentState;

		public ShortTermState? Update(OhlcBar prev, OhlcBar current)
		{
			var candle = GetCandleType(prev, current);

			switch (candle)
			{
				case CandleType.Bull:

					bullCount++;
					bearCount = 0;

					if (bullCount >= 2)
					{
						currentState = ShortTermState.Bull;
					}
					else
					{
						// First Bull after a Bear sequence interrupts it
						if (currentState == ShortTermState.Bear)
							currentState = ShortTermState.BearNeutral;
					}

					break;

				case CandleType.Bear:

					bearCount++;
					bullCount = 0;

					if (bearCount >= 2)
					{
						currentState = ShortTermState.Bear;
					}
					else
					{
						// First Bear after a Bull sequence interrupts it
						if (currentState == ShortTermState.Bull)
							currentState = ShortTermState.BullNeutral;
					}

					break;

				case CandleType.Neutral:
					// Neutral candles do not affect counts or state.
					break;
			}

			return currentState;
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