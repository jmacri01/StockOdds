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
	// bars it spanned. The applied exposure can drift within a run as the EMA of the
	// target moves, so we record the stake at entry and at exit.
	public class BankrollTrade
	{
		public (LongTermState LT, ShortTermState ST) Bucket { get; set; }

		public DateTime EntryDate { get; set; }
		public DateTime ExitDate { get; set; }

		public TradeDirection Direction { get; set; }
		public double StakeStart { get; set; }       // signed exposure at entry [-1..1] (short is negative)
		public double StakeEnd { get; set; }         // signed exposure at exit  [-1..1] (short is negative)

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
		public double OpenStake { get; set; }         // signed exposure [-1..1] (short is negative)

		public double TotalReturnPct => (FinalBankroll - InitialBankroll) / InitialBankroll * 100.0;
		public double BuyHoldReturnPct => (BuyHoldFinal - InitialBankroll) / InitialBankroll * 100.0;

		public int WinCount => Trades.Count(t => t.TradePct > 0);
		public int LossCount => Trades.Count(t => t.TradePct < 0);
		public double WinRatePct => Trades.Count > 0 ? (double)WinCount / Trades.Count * 100.0 : 0.0;

		// worst peak-to-trough drawdown of the bankroll equity curve
		public double MaxDrawdownPct { get; set; }

		// annualized Sharpe ratio of the per-bar strategy returns (risk-free rate = 0)
		public double SharpeRatio { get; set; }

		// buy & hold, over the same span, for reference
		public double BuyHoldMaxDrawdownPct { get; set; }
		public double BuyHoldSharpeRatio { get; set; }

		// Per-bar series, only populated when BankrollSimulator.RecordBars is on. Used by the
		// risk-timing study to compare the strategy's drawdown against a constant position at
		// the same AVERAGE exposure (does exposure TIMING beat plain de-risking?).
		public List<double> BarPositions = new();   // applied signed exposure each bar
		public List<double> BarBhReturns = new();   // raw buy&hold return each bar (prev->cur)
	}

	public static class BankrollSimulator
	{
		// ============ Exposure model (the core (LT, ST) map) ============
		// Every candle has a single TARGET exposure, looked up by its (LT, ST) bucket:
		// 4 ST states under each of the 2 LT states. Long is positive, short is negative;
		// the eight values form a gradient from most-bullish (Bull, Bull) to most-bearish
		// (Bear, Bear), crossing through zero in the middle. No ramps, no caps.
		//
		// LT Bull row:
		public static double BullBear        = -0.50;   // ST Bear
		public static double BullBearNeutral =  0.00;   // ST Bear Neutral
		public static double BullBullNeutral =  0.50;   // ST Bull Neutral
		public static double BullBull        =  1.00;   // ST Bull
		// LT Bear row:
		public static double BearBull        =  0.50;   // ST Bull
		public static double BearBullNeutral =  0.00;   // ST Bull Neutral
		public static double BearBearNeutral = -0.50;   // ST Bear Neutral
		public static double BearBear        = -1.00;   // ST Bear

		// ============ Smoothing & rebalance control ============
		// 1) The per-candle target is smoothed with an EMA (avoids the raw map jumping
		//    e.g. +0.75 -> -1.00 in a single state change).
		// 2) We only move the actual position when it has drifted more than
		//    RebalanceDriftPercent (in exposure-percent) away from that EMA, then snap it
		//    back to the EMA. e.g. drift 20 with EMA 0.50 -> rebalance only if position
		//    < 0.30 or > 0.70.
		// 3) Finally the position is clamped to [MinExposurePercent, MaxExposurePercent].
		//    These bound the POSITION only -- not the per-candle target that feeds the EMA.
		// Percentages are of full exposure: 100 = fully long, -100 = fully short.
		public static int    ExposureEmaPeriod     = 5;
		public static double RebalanceDriftPercent = 20.0;
		public static double MinExposurePercent    =    0.0;
		public static double MaxExposurePercent    =  100.0;

		// Dynamic long bias: a directional skew applied to the EMA before the drift/clamp
		// stage, driven by how one-sided the recent LT trend has been.
		//   dynBias     = sum(LT dir over BiasPeriod) / BiasPeriod
		//   biasEma     = EMA(dynBias, BiasEmaPeriod)
		//   adjustedEma = |ema| * biasEma + ema
		// LT dir is (LongBias + 1) on a Bull candle, -1 on a Bear candle. LongBias
		// skews the Bull weight: 0 -> +1 (symmetric), -0.5 -> +0.5, +1 -> +2. So an
		// all-Bull window gives dynBias (LongBias + 1) and all-Bear gives -1. dynBias is
		// then EMA-smoothed before skewing. Unclamped.
		// Applies to the smoothed exposure, not the per-candle target.
		public static int    BiasPeriod    = 20;
		public static double LongBias      = 2.0;
		// The dynamic bias is smoothed by this EMA before it skews the exposure EMA.
		public static int    BiasEmaPeriod = 100;

		// ============ Dynamic (per-candle) long bias from volatility ============
		// When UseDynamicLongBias is on, LongBias is NOT a constant — each candle it is
		// derived from a running EWMA of realized volatility, so calm regimes lean more
		// long and volatile regimes lean flat/short. This layers a volatility-target flavor
		// onto the bias: in a calm uptrend it boosts the long lean (captures return); when
		// vol rises it de-leans (trims exposure). The static LongBias above is ignored.
		//
		//   logret   = ln(close_t / close_{t-1})
		//   volVar   = EWMA(logret^2, VolEmaPeriod)                 -- per-bar variance
		//   volPct   = sqrt(volVar) * sqrt(PeriodsPerYear) * 100    -- annualized HV %, same
		//                                                              scale as Volatility.cs
		//   dynLB    = clamp(VolBiasScale * ln(VolBiasPivot / volPct), VolBiasFloor, VolBiasCeil)
		//
		// Shape (monotone decreasing): volPct -> 0 gives dynLB -> +inf (clamped to Ceil,
		// the practical "infinite long lean"); dynLB = 0 at volPct = VolBiasPivot; dynLB < 0
		// above the pivot. So with Pivot = 100: vol 50 -> +0.69*Scale, vol 100 -> 0,
		// vol 200 -> -0.69*Scale. Only past closes feed the EWMA, so there is no look-ahead.
		public static bool   UseDynamicLongBias = false;
		// When on, dynBias is divided by its MAX POSSIBLE magnitude (BiasPeriod*max(|LB+1|,1))
		// instead of BiasPeriod, so it is naturally bounded to [-1, 1] and LongBias controls
		// the shape (short-side compression) rather than an unbounded amplitude.
		public static bool   NormalizeDynBiasToMax = false;
		public static int    VolEmaPeriod       = 30;
		public static double VolBiasPivot       = 100.0;   // vol (annualized %) where dyn LB crosses 0
		public static double VolBiasScale       = 1.0;     // slope of the log map
		public static double VolBiasFloor       = -2.0;    // clamp low  (most bearish lean)
		public static double VolBiasCeil        = 12.0;    // clamp high (the practical "infinite" long lean)

		// ============ Volatility-scaled exposure ============
		// A separate experiment from the dynamic long bias: instead of changing LongBias,
		// scale the adjusted EMA (adjEma) directly by volatility. On a POSITIVE adjEma
		// (net-long signal) multiply by VolScalePivot/vol (amplify longs as vol falls); on a
		// NEGATIVE adjEma (net-short signal) multiply by vol/VolScalePivot (dampen shorts as
		// vol falls). So calm -> lean harder long & shrink shorts; volatile -> trim longs &
		// lean harder short. Applied to adjEma before the drift band + clamp; the short-side
		// scaling only bites when MinExposurePercent < 0. Uses the same realized-vol EWMA
		// (VolEmaPeriod) as the dynamic long bias. At vol = VolScalePivot the factor is 1.
		public static bool   UseVolExposureScale = false;
		public static double VolScalePivot       = 100.0;

		// Number of bar-periods per year, used only to annualize the Sharpe ratio.
		// 252 trading days for daily bars; set to 52 for weekly, 12 for monthly, etc.
		public static double PeriodsPerYear = 252.0;

		// When on, Run records the per-bar applied position + buy&hold return on the result
		// (BarPositions / BarBhReturns). Off by default so the grid search stays lean.
		public static bool RecordBars = false;

		// Map a running annualized-vol reading (%) to a dynamic long bias. Monotone
		// decreasing: -> VolBiasCeil as vol -> 0, 0 at VolBiasPivot, negative above it.
		public static double DynamicLongBiasFromVol(double volAnnualizedPct)
		{
			double v = Math.Max(volAnnualizedPct, 1e-6);
			double lb = VolBiasScale * Math.Log(VolBiasPivot / v);
			return Clamp(lb, VolBiasFloor, VolBiasCeil);
		}

		// target exposure for a bucket (the eight-value map above)
		public static double TargetExposure(LongTermState lt, ShortTermState st) =>
			lt == LongTermState.Bull
				? st switch
				{
					ShortTermState.Bull        => BullBull,
					ShortTermState.BullNeutral => BullBullNeutral,
					ShortTermState.BearNeutral => BullBearNeutral,
					ShortTermState.Bear        => BullBear,
					_                          => 0.0
				}
				: st switch
				{
					ShortTermState.Bull        => BearBull,
					ShortTermState.BullNeutral => BearBullNeutral,
					ShortTermState.BearNeutral => BearBearNeutral,
					ShortTermState.Bear        => BearBear,
					_                          => 0.0
				};

		// clamp that tolerates min/max given in either order
		private static double Clamp(double x, double lo, double hi) =>
			Math.Min(Math.Max(x, Math.Min(lo, hi)), Math.Max(lo, hi));

		// Annualized Sharpe ratio of a per-bar return series, risk-free rate = 0:
		//   mean(r) / stddev(r) * sqrt(periodsPerYear), using the sample stddev.
		private static double Sharpe(List<double> rets, double periodsPerYear)
		{
			if (rets.Count < 2) return 0.0;
			double mean = rets.Average();
			double variance = rets.Sum(x => (x - mean) * (x - mean)) / (rets.Count - 1);
			double sd = Math.Sqrt(variance);
			return sd > 0.0 ? mean / sd * Math.Sqrt(periodsPerYear) : 0.0;
		}

		// Worst peak-to-trough drawdown (in %) of the equity curve implied by
		// compounding a per-bar return series from 1.0.
		private static double MaxDrawdown(List<double> rets)
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

		// Walks the bars bar-by-bar. On each candle:
		//   target   = the (LT, ST) map value
		//   ema      = EMA(target, ExposureEmaPeriod)          -- smooths the target
		//   adjEma   = |ema|*biasEma + ema                     -- EMA-smoothed dynamic skew
		//   held     = adjEma, but only re-set when it drifts past RebalanceDriftPercent
		//              (otherwise the previous held value persists -- the "deadband")
		//   position = clamp(held, Min/MaxExposurePercent)     -- what is actually applied
		// State is evaluated as of `prev` (bars[i-1]); the resulting position is held
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

			// per-bar return series for Sharpe (strategy = sized/signed, buy&hold = raw move),
			// over the exact same bars so the two ratios are comparable.
			var stratReturns = new List<double>();
			var bhReturns = new List<double>();

			double alpha = 2.0 / (ExposureEmaPeriod + 1);
			double biasAlpha = 2.0 / (BiasEmaPeriod + 1);
			double driftBand = RebalanceDriftPercent / 100.0;
			double minExp = MinExposurePercent / 100.0;
			double maxExp = MaxExposurePercent / 100.0;

			double ema = double.NaN;     // EMA of the per-candle target exposure
			double held = double.NaN;    // deadband follower of the EMA (unclamped)
			double position = 0.0;       // clamped signed exposure actually applied

			// rolling LT-direction window for the dynamic long bias
			var biasWindow = new Queue<double>(BiasPeriod);
			double biasSum = 0.0;
			double biasEma = double.NaN;   // EMA of the dynamic bias

			// realized-volatility EWMA feeding the vol-driven features (dynamic long bias
			// and/or vol-scaled exposure). EWMA of squared log returns -> annualized HV %.
			double volAlpha = 2.0 / (VolEmaPeriod + 1);
			double varEwma = double.NaN;   // EWMA of squared log returns (per-bar variance)

			// Update the vol EWMA with the latest close-to-close log return (the return that
			// LANDS on the state-decision bar `prev` — no look-ahead) and return annualized %.
			double UpdateVolPct(double logRet)
			{
				double r2 = logRet * logRet;
				varEwma = double.IsNaN(varEwma) ? r2 : volAlpha * r2 + (1.0 - volAlpha) * varEwma;
				return Math.Sqrt(varEwma) * Math.Sqrt(PeriodsPerYear) * 100.0;
			}

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

			// target -> EMA -> dynamic long-bias skew -> drift-band held -> clamped position
			// effLongBias is the LongBias to use for THIS candle: the static field normally,
			// or the volatility-derived value when UseDynamicLongBias is on.
			double StepExposure(LongTermState lt, ShortTermState st, double effLongBias, double volPct)
			{
				double target = TargetExposure(lt, st);
				ema = double.IsNaN(ema) ? target : alpha * target + (1.0 - alpha) * ema;

				// dynamic long bias: rolling LT-direction sum over BiasPeriod candles /
				// BiasPeriod, then EMA-smoothed. A Bull candle contributes (effLongBias + 1), a Bear candle -1.
				// Matches the Pine math.sum window.
				double sig = lt == LongTermState.Bull ? effLongBias + 1.0 : lt == LongTermState.Bear ? -1.0 : 0.0;
				biasWindow.Enqueue(sig);
				biasSum += sig;
				while (biasWindow.Count > BiasPeriod)
					biasSum -= biasWindow.Dequeue();
				// Normalizer: BiasPeriod (raw average, unbounded on the bull side) or, when
				// NormalizeDynBiasToMax is on, the MAX possible |sum| = BiasPeriod*max(|LB+1|,1)
				// so dynBias is naturally clamped to [-1, 1] (all-bull -> +1, all-bear -> -1/(LB+1)).
				// LB then sets the SHAPE (how much the short side compresses) rather than amplitude.
				double denom = NormalizeDynBiasToMax
					? BiasPeriod * Math.Max(Math.Abs(effLongBias + 1.0), 1.0)
					: BiasPeriod;
				double dynBias = biasSum / denom;
				biasEma = double.IsNaN(biasEma) ? dynBias : biasAlpha * dynBias + (1.0 - biasAlpha) * biasEma;

				double adjEma = Math.Abs(ema) * biasEma + ema;

				// volatility-scaled exposure: amplify longs / dampen shorts as vol falls
				// (positive adjEma *= pivot/vol; negative adjEma *= vol/pivot). Factor = 1 at pivot.
				if (UseVolExposureScale)
				{
					double v = Math.Max(volPct, 1e-6);
					adjEma *= adjEma >= 0 ? VolScalePivot / v : v / VolScalePivot;
				}

				if (double.IsNaN(held) || Math.Abs(held - adjEma) > driftBand)
					held = adjEma;
				return Clamp(held, minExp, maxExp);
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

				// vol as of `prev`: the return into prev (prevPrev.Close -> prev.Close).
				double volRet = prevPrev.Close > 0 ? Math.Log(prev.Close / prevPrev.Close) : 0.0;
				double volPct = UpdateVolPct(volRet);
				double effLb = UseDynamicLongBias ? DynamicLongBiasFromVol(volPct) : LongBias;
				position = StepExposure(lt, st.Value, effLb, volPct);
				var dir = position < 0 ? TradeDirection.Short : TradeDirection.Long;

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
						StakeStart = position,
						StakeEnd = position,
						BankrollBefore = bankroll,
					};
					curStockFactor = 1.0;
					curTradeFactor = 1.0;
				}

				// -------- P&L for this bar-step (prev.Close -> bar.Close) --------
				double r = (bar.Close - prev.Close) / prev.Close;
				double tradeReturn = position * r;   // position already carries the sign

				stratReturns.Add(tradeReturn);
				bhReturns.Add(r);

				if (RecordBars)
				{
					result.BarPositions.Add(position);
					result.BarBhReturns.Add(r);
				}

				bankroll *= (1.0 + tradeReturn);

				cur.ExitDate = bar.Date;
				cur.StakeEnd = position;
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

			// Sharpe ratios (risk-free = 0) and buy & hold drawdown over the same bars.
			result.SharpeRatio = Sharpe(stratReturns, PeriodsPerYear);
			result.BuyHoldSharpeRatio = Sharpe(bhReturns, PeriodsPerYear);
			result.BuyHoldMaxDrawdownPct = MaxDrawdown(bhReturns);

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
				double lastVolRet = bars[^2].Close > 0 ? Math.Log(bars[^1].Close / bars[^2].Close) : 0.0;
				double lastVolPct = UpdateVolPct(lastVolRet);
				double lastEffLb = UseDynamicLongBias ? DynamicLongBiasFromVol(lastVolPct) : LongBias;
				position = StepExposure(lastLt, lastSt.Value, lastEffLb, lastVolPct);
				result.OpenBucket = (lastLt, lastSt.Value);
				result.OpenStake = position;
				result.OpenDirection = position < 0 ? TradeDirection.Short : TradeDirection.Long;
			}

			// Buy & hold across the traded span: bars[1].Close -> last close.
			double entry = bars[1].Close;
			double exit = bars[^1].Close;
			result.BuyHoldFinal = entry > 0 ? initialBankroll * (exit / entry) : initialBankroll;

			return result;
		}
	}
}
