using System;
using System.Collections.Generic;
using System.Linq;

namespace StockOdds
{
	public enum TradeDirection
	{
		Long,
		Short
	}

	// One contiguous (LT, ST) run, with the bankroll effect accumulated across the
	// bars it spanned. Exposure ramps within a run, so we record the stake at entry
	// and at exit.
	public class BankrollTrade
	{
		public (LongTermState LT, ShortTermState ST) Bucket { get; set; }

		public DateTime EntryDate { get; set; }
		public DateTime ExitDate { get; set; }

		public TradeDirection Direction { get; set; }
		public double StakeStart { get; set; }       // exposure fraction at entry [0..1]
		public double StakeEnd { get; set; }         // exposure fraction at exit  [0..1]

		public double StockPct { get; set; }         // compounded % move of the stock over the run
		public double TradePct { get; set; }         // compounded % change of TOTAL bankroll over the run

		public double BankrollBefore { get; set; }
		public double BankrollAfter { get; set; }
	}

	// Returns attributed to a state, summed over every bar spent in it.
	public class PerStateStat
	{
		public (LongTermState LT, ShortTermState ST) Bucket { get; set; }
		public TradeDirection Direction { get; set; }
		public int Bars { get; set; }
		public double TotTradePct { get; set; }
		public double AvgTradePct => Bars > 0 ? TotTradePct / Bars : 0.0;
		public double TotStockPct { get; set; }
		public double AvgStockPct => Bars > 0 ? TotStockPct / Bars : 0.0;
	}

	public class BankrollResult
	{
		public double InitialBankroll { get; set; }
		public double FinalBankroll { get; set; }
		public List<BankrollTrade> Trades { get; set; } = new();
		public List<PerStateStat> PerState { get; set; } = new();

		// buy & hold over the same span, for reference
		public double BuyHoldFinal { get; set; }

		// still-open position at the end of the data (not yet realized), if any
		public (LongTermState LT, ShortTermState ST)? OpenBucket { get; set; }
		public TradeDirection OpenDirection { get; set; }
		public double OpenStake { get; set; }

		public double TotalReturnPct => (FinalBankroll - InitialBankroll) / InitialBankroll * 100.0;
		public double BuyHoldReturnPct => (BuyHoldFinal - InitialBankroll) / InitialBankroll * 100.0;

		public int WinCount => Trades.Count(t => t.TradePct > 0);
		public int LossCount => Trades.Count(t => t.TradePct < 0);
		public double WinRatePct => Trades.Count > 0 ? (double)WinCount / Trades.Count * 100.0 : 0.0;

		// worst peak-to-trough drawdown of the bankroll equity curve
		public double MaxDrawdownPct { get; set; }
	}

	public static class BankrollSimulator
	{
		// ============ LONG-side exposure (set these to taste) ============
		// Two independent controls, combined as exposure = MIN(ramp, ST-state cap):
		//
		// 1) LongRampLevels  -- how you SCALE INTO a fresh long. Indexed by the number
		//    of bull-sequence confirmations since entering LT-Bull: rung 0 at entry,
		//    +1 rung per confirming bull candle, held at the top rung thereafter.
		//    Default {0.25,0.50,0.75,1.00} => build 25% -> 100% over four confirmations.
		public static double[] LongRampLevels = { .25, .5, .75, 1};

		// 2) StExposureLevels -- the exposure CEILING for each ST state, indexed by
		//    bullishness: [0]=Bear, [1]=BearNeutral, [2]=BullNeutral, [3]=Bull. The cap
		//    moves toward the current state's level but only ONE step per ST state
		//    change (up or down), so a skip like BullNeutral->Bear can't crash the cap
		//    from 75% straight to 25% -- it only steps to 50%.
		public static double[] StExposureLevels = { .25, .5, .75, 1};

		// -------- SHORT-side exposure knobs (set these to taste) --------
		// The short side never ramps. When LT flips to Bear the position is opened at
		// MaxShortExposureFirstConfirmation. On every subsequent bear-sequence
		// confirmation it is (re)set to MaxShortExposureSecondPlusConfirmation.
		// Defaults (0.25 / 0.00) => open 25% short, then flatten to 0% on the next
		// confirmation (i.e. the previous "exit on confirmation" behavior).
		public static double MaxShortExposureFirstConfirmation = 0.25;
		public static double MaxShortExposureSecondPlusConfirmation = 0;

		// entry-ramp exposure at a given rung (clamped to the ends of the ladder)
		private static double LongRampAt(int rung) =>
			LongRampLevels.Length == 0 ? 0.0
			: LongRampLevels[Math.Clamp(rung, 0, LongRampLevels.Length - 1)];

		// ST-state exposure cap at a given rung (clamped to the ends of the ladder)
		private static double StCapAt(int rung) =>
			StExposureLevels.Length == 0 ? 1.0
			: StExposureLevels[Math.Clamp(rung, 0, StExposureLevels.Length - 1)];

		// bullishness rank of an ST state (higher = more bullish).
		private static int StRank(ShortTermState st) => st switch
		{
			ShortTermState.Bull => 3,
			ShortTermState.BullNeutral => 2,
			ShortTermState.BearNeutral => 1,
			ShortTermState.Bear => 0,
			_ => 0
		};

		// Rung into StExposureLevels for the CURRENT trade direction: how aligned the ST
		// state is with the position. Long => bullishness (Bull is fully aligned); short
		// => bearishness (Bear is fully aligned). So StExposureLevels[3] is the ceiling
		// when ST agrees with the trade, [0] when it least agrees. Applies to both sides.
		private static int AlignedRank(LongTermState lt, ShortTermState st) =>
			lt == LongTermState.Bull ? StRank(st) : 3 - StRank(st);

		// Walks the bars bar-by-bar, sizing exposure = MIN(scale-in level, ST-state cap):
		//   * on an LT state change, exposure resets (direction = LT: Bull->long, Bear->short)
		//   * scale-in level: LONG uses the LongRampLevels ramp (one rung per bull
		//     confirmation); SHORT uses the MaxShortExposure* confirmation knobs
		//   * ST-state cap: StExposureLevels indexed by how aligned ST is with the trade
		//     (bullishness when long, bearishness when short), moving one rung per ST
		//     state change (rate limit) -- caps BOTH long and short exposure
		//   * exposure is sticky otherwise (neutral / opposing candles don't change it)
		// State is evaluated as of `prev` (bars[i-1]); the resulting exposure is held
		// over the move into `cur` (bars[i]), so there is no look-ahead.
		public static BankrollResult Run(List<OhlcBar> bars, double initialBankroll = 10_000.0)
		{
			var result = new BankrollResult { InitialBankroll = initialBankroll };

			if (bars.Count < 3)
			{
				result.FinalBankroll = initialBankroll;
				result.BuyHoldFinal = initialBankroll;
				return result;
			}

			var ltEngine = new LongTermStateEngine();
			var stEngine = new CandleStateEngine();

			double bankroll = initialBankroll;
			double peak = initialBankroll;
			double maxDd = 0.0;

			LongTermState? prevLt = null;
			double exposure = 0.0;
			TradeDirection dir = TradeDirection.Long;

			// Consecutive-candle run counts (bear candle resets the bull run and vice
			// versa; neutral candles preserve both, matching the ST engine). A run is a
			// confirmed "sequence" once it reaches 2.
			int bullRun = 0, bearRun = 0;

			// entry-ramp rung within an LT-Bull regime (climbs one rung per bull confirmation)
			int rampRung = 0;

			// ST-state cap rung, rate-limited to move one rung per ST state change
			// (aligned to bullishness in LT-Bull, bearishness in LT-Bear)
			int capRung = 0;

			// short-side scale-in level from the confirmation knobs (before the ST cap)
			double shortLevel = 0.0;

			// previous bar's ST state, to detect ST state changes
			ShortTermState? prevSt = null;

			var perState = new Dictionary<(LongTermState, ShortTermState), PerStateStat>();

			// current (LT, ST) run being accumulated for the ledger
			BankrollTrade? cur = null;
			double curStockFactor = 1.0, curTradeFactor = 1.0;

			void CloseRun()
			{
				if (cur == null) return;
				cur.StockPct = (curStockFactor - 1.0) * 100.0;
				cur.TradePct = (curTradeFactor - 1.0) * 100.0;
				cur.BankrollAfter = bankroll;
				result.Trades.Add(cur);
			}

			for (int i = 2; i < bars.Count; i++)
			{
				var prevPrev = bars[i - 2];
				var prev = bars[i - 1];
				var bar = bars[i];

				var lt = ltEngine.Update(prevPrev, prev);
				var st = stEngine.Update(prevPrev, prev);
				if (st == null)
					continue;

				var candle = GetCandleType(prevPrev, prev);

				// advance the consecutive-run counters for this candle
				if (candle == CandleType.Bull) { bullRun++; bearRun = 0; }
				else if (candle == CandleType.Bear) { bearRun++; bullRun = 0; }
				// neutral: preserve both runs

				// -------- exposure sizing --------
				// exposure = MIN(direction scale-in, ST-state cap). The cap applies to
				// long AND short (aligned to bullishness / bearishness respectively) and
				// moves at most one rung per ST state change.
				if (prevLt == null || lt != prevLt)
				{
					// LT state change (or the very first evaluation): reset for the new regime.
					dir = lt == LongTermState.Bull ? TradeDirection.Long : TradeDirection.Short;
					rampRung = 0;                                   // long scale-in from the bottom
					shortLevel = MaxShortExposureFirstConfirmation;  // short opens at the first-conf level
					capRung = AlignedRank(lt, st.Value);            // cap starts at the entry state's level
				}
				else if (lt == LongTermState.Bull)
				{
					// long scale-in: a confirming bull candle climbs one ramp rung
					if (candle == CandleType.Bull && bullRun >= 2)
						rampRung = Math.Min(LongRampLevels.Length - 1, rampRung + 1);
				}
				else // lt == LongTermState.Bear
				{
					// short scale-in: a new bear confirmation (re)sets to the second+ level
					if (candle == CandleType.Bear && bearRun >= 2)
						shortLevel = MaxShortExposureSecondPlusConfirmation;
				}

				// ST cap: on an ST state change, step the cap ONE rung toward the current
				// state's aligned rank (up or down) -- never skipping rungs. Applies to
				// both regimes.
				if (prevLt != null && lt == prevLt && prevSt.HasValue && st.Value != prevSt.Value)
				{
					int target = AlignedRank(lt, st.Value);
					if (target > capRung) capRung++;
					else if (target < capRung) capRung--;
				}

				// combine the direction's scale-in level with the ST-state cap
				double scaleIn = lt == LongTermState.Bull ? LongRampAt(rampRung) : shortLevel;
				exposure = Math.Min(scaleIn, StCapAt(capRung));

				prevLt = lt;
				prevSt = st.Value;

				// -------- ledger run boundary --------
				var bucket = (lt, st.Value);
				if (cur == null || cur.Bucket != bucket)
				{
					CloseRun();
					cur = new BankrollTrade
					{
						Bucket = bucket,
						Direction = dir,
						EntryDate = prev.Date,
						ExitDate = bar.Date,
						StakeStart = exposure,
						StakeEnd = exposure,
						BankrollBefore = bankroll,
					};
					curStockFactor = 1.0;
					curTradeFactor = 1.0;
				}

				// -------- P&L for this bar-step (prev.Close -> bar.Close) --------
				double r = (bar.Close - prev.Close) / prev.Close;
				double signed = (dir == TradeDirection.Long ? 1.0 : -1.0) * exposure;
				double tradeReturn = signed * r;

				bankroll *= (1.0 + tradeReturn);

				cur.ExitDate = bar.Date;
				cur.StakeEnd = exposure;
				curStockFactor *= (1.0 + r);
				curTradeFactor *= (1.0 + tradeReturn);

				// drawdown tracking on the bar-level equity curve
				if (bankroll > peak) peak = bankroll;
				double dd = (peak - bankroll) / peak * 100.0;
				if (dd > maxDd) maxDd = dd;

				// per-state attribution (bar level)
				if (!perState.TryGetValue(bucket, out var stat))
				{
					stat = new PerStateStat { Bucket = bucket, Direction = dir };
					perState[bucket] = stat;
				}
				stat.Bars++;
				stat.TotTradePct += tradeReturn * 100.0;
				stat.TotStockPct += r * 100.0;
			}

			CloseRun();

			result.FinalBankroll = bankroll;
			result.MaxDrawdownPct = maxDd;

			result.PerState = perState.Values
				.OrderBy(s => s.Bucket.Item1)
				.ThenBy(s => s.Bucket.Item2)
				.ToList();

			// Open position: state as of the LAST bar (one more engine step), plus the
			// exposure we'd be carrying into the next, still-unrealized bar.
			var lastLt = ltEngine.Update(bars[^2], bars[^1]);
			var lastSt = stEngine.Update(bars[^2], bars[^1]);
			if (lastSt != null)
			{
				var lastCandle = GetCandleType(bars[^2], bars[^1]);
				if (lastCandle == CandleType.Bull) { bullRun++; bearRun = 0; }
				else if (lastCandle == CandleType.Bear) { bearRun++; bullRun = 0; }

			if (prevLt == null || lastLt != prevLt)
				{
					dir = lastLt == LongTermState.Bull ? TradeDirection.Long : TradeDirection.Short;
					rampRung = 0;
					shortLevel = MaxShortExposureFirstConfirmation;
					capRung = AlignedRank(lastLt, lastSt.Value);
				}
				else if (lastLt == LongTermState.Bull)
				{
					if (lastCandle == CandleType.Bull && bullRun >= 2)
						rampRung = Math.Min(LongRampLevels.Length - 1, rampRung + 1);
				}
				else // lastLt == LongTermState.Bear
				{
					if (lastCandle == CandleType.Bear && bearRun >= 2)
						shortLevel = MaxShortExposureSecondPlusConfirmation;
				}

				if (prevLt != null && lastLt == prevLt && prevSt.HasValue && lastSt.Value != prevSt.Value)
				{
					int target = AlignedRank(lastLt, lastSt.Value);
					if (target > capRung) capRung++;
					else if (target < capRung) capRung--;
				}

				double lastScaleIn = lastLt == LongTermState.Bull ? LongRampAt(rampRung) : shortLevel;
				exposure = Math.Min(lastScaleIn, StCapAt(capRung));

				result.OpenBucket = (lastLt, lastSt.Value);
				result.OpenDirection = dir;
				result.OpenStake = exposure;
			}

			// Buy & hold across the traded span: bars[1].Close -> last close.
			double entry = bars[1].Close;
			double exit = bars[^1].Close;
			result.BuyHoldFinal = entry > 0 ? initialBankroll * (exit / entry) : initialBankroll;

			return result;
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
