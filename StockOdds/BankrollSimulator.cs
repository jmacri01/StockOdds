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

		// per-bar series (aligned) so a walk-forward can score a sub-window after a warmup
		public List<DateTime> ReturnDates { get; set; } = new();
		public List<double>   StratReturns { get; set; } = new();
		// the actual traded exposure (clamped/RSI/smoothed/region-adjusted position) applied each bar,
		// aligned with ReturnDates/StratReturns. Consumed by the options-overlay simulator.
		public List<double>   Positions { get; set; } = new();
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
		public static double RebalanceDriftPercent = 30.0;
		public static double MinExposurePercent    =    0.0;
		public static double MaxExposurePercent    =  150.0;  // default 1.5x -- lever the strong-signal candles (UI ceiling 200)

		// Dynamic long bias: a directional skew applied to the EMA before the drift/clamp
		// stage, driven by how one-sided the recent LT trend has been.
		//   dynBias     = sum(LT dir over BiasPeriod) / BiasPeriod
		//   biasEma     = EMA(dynBias, BiasEmaPeriod)
		//   adjustedEma = |ema| * biasEma + ema
		// LT dir is (effLongBias + 1) on a Bull candle, -1 on a Bear candle. effLongBias
		// skews the Bull weight: 0 -> +1 (symmetric), -0.5 -> +0.5, +1 -> +2. So an
		// all-Bull window gives dynBias (effLongBias + 1) and all-Bear gives -1. dynBias is
		// then EMA-smoothed before skewing. Unclamped. effLongBias is the per-candle dynamic
		// bias computed below. Applies to the smoothed exposure, not the per-candle target.
		public static int    BiasPeriod    = 15;
		// The dynamic bias is smoothed by this EMA before it skews the exposure EMA.
		public static int    BiasEmaPeriod = 150;

		// ============ Dynamic (per-candle) LongBias ============
		// The per-candle bias is recomputed every candle from a combined trait z-score:
		//   z          = (rollingHV - HvRefMean)/HvRefStd + (rollingPersist - PersRefMean)/PersRefStd
		//   LongBias_t = clamp( DynBase * exp(-DynDecay * z),  DynMin, DynMax )
		// z is on an ABSOLUTE scale via FIXED reference constants, so a quiet/steady name reads
		// z<0 -> a large LongBias (leans toward staying long) and a hot high-HV, high-persistence
		// name reads z>0 -> a small LongBias (lets the active signal run). rollingHV = annualized
		// stdev of log returns over HvWindow (same convention as Volatility.AnnualizedHistoricalPct);
		// rollingPersist = Kaufman efficiency ratio of the RAW (LT,ST) target exposure over
		// PersistWindow (1 = the state sequence trends and holds, 0 = it round-trips) — measured
		// on the raw target, not the EMA, so it is independent of ExposureEmaPeriod. Finally the per-candle bias is
		// EMA-smoothed over DynSmoothPeriod so it can't whipsaw (the raw value jumps because the
		// persistence ratio is jumpy and the exp map is convex). All knobs are hand-set, not fit.
		public static int    HvWindow        = 60;
		public static int    PersistWindow   = 63;
		public static double HvRefMean       = 57.0;
		public static double HvRefStd        = 34.6;
		public static double PersRefMean     = 0.072;
		public static double PersRefStd      = 0.010;
		public static double DynBase         = 1.0;    // LongBias at z = 0
		public static double DynDecay        = 0.6;    // decay rate
		public static double DynMin          = 0.0;
		// The per-candle bias is just EMA-smoothed over DynSmoothPeriod so it can't whipsaw (the raw value jumps
		// because the persistence ratio is jumpy and the exp map is convex): effLongBias = MAX(EMA(raw), DynMin).
		// DynMax caps the raw bias before smoothing (rarely binds). NOTE: an earlier slow/fast-EMA "ceiling + ratio"
		// apparatus (BiasEmaRatio / DynSmoothSlow / DynSlowMult / clamps) was REMOVED -- an OOS test across four
		// random-500 samples showed this plain fast-EMA bias matched or slightly beat it in every mode, so the
		// machinery (4 knobs) didn't earn its weight. Keep the bias simple.
		public static double DynMax          = 150.0; // raw-bias cap (rarely binds)
		public static int    DynSmoothPeriod = 10;    // EMA smoothing of the per-candle bias (1 = off)
		// OUT-OF-REGION rule: a name is out of its edge regime whenever the RAW exposure signal is bearish -- i.e.
		// the EMA of the (LT,ST) target exposure (`ema`, before the bias skew) is < 0. Parameter-free (no windows).
		// 1 = go to CASH (default -- rotate capital to an in-region name); 0 = keep deploying; 2 = mirror buy&hold.
		// Chosen over an earlier trailing-persistence rule: cleaner (one condition, no tuned windows) and higher
		// out-of-sample Cash Sharpe (0.22 vs 0.11 on a broad ~1300-name universe).
		public static int    BearRegimeMode = 1;
		// RSI overbought-trim overlay on the FINAL position: posB *= min(RsiMultNumerator/RSI(period), 1) each bar --
		// trims when overbought and does NOTHING when oversold (capped at 1, never levers up). Applied after the drift
		// band and the out-of-region rule. A clamp ablation showed the ENTIRE edge is the overbought trim (the oversold
		// lever added nothing, so it's capped at 1). The period and numerator are the same "trim-aggressiveness" lever;
		// a SHORT period (2, Connors-style) beats 7. 0 = off; default period 2 (Wilder RSI on close). See RsiMultNumerator.
		public static int    RsiOverlayPeriod = 2;
		// Numerator N in the overbought-trim multiplier min(N/RSI, 1): trimming begins when RSI > N and the depth
		// is N/RSI (at RSI=100, exposure -> N%). This is the ONLY conditioning on the trim -- a single fixed number.
		// Default 40: paired with the 1.5x MaxExposure cap, N=40 is the return/max-drawdown sweet spot on the
		// momentum/flyer names (the curated-basket ratio peaks here at ~4.9) while keeping strong upside participation.
		// Lower N trims harder (more defensive, less participation); higher N / RsiOverlayPeriod=0 approaches no trim.
		// This is the single trim knob after volume/ATR/exposure-shaping conditioning was removed for failing to help
		// BOTH the curated basket AND the broad OOS sets simultaneously.
		public static double RsiMultNumerator = 40.0;
		// HV-conditioned overbought trim (DEFAULT ON). The RSI numerator is scaled DOWN by the candle's live
		// rolling HV(HvWindow): N_eff = min(RsiMultNumerator, max(HvTrimFloor, HvTrimSlope * rollingHV%)). So the
		// trim is HARDER on low-vol candles (their overbought spikes mean-revert -> cut them) and relaxes back up
		// to the RsiMultNumerator CAP as HV rises (reaching the cap around HV = RsiMultNumerator/HvTrimSlope). It
		// NEVER trims lighter than RsiMultNumerator -- so raising N raises the ceiling everywhere. Validated as a
		// genuine drawdown-reduction edge: return ~flat, drawdown down, replicated 4/4 on disjoint random-500 OOS
		// samples (broad + decliners); the "let high-vol run" upside was separated out as beta (return AND drawdown
		// up on the survivor names, not a real signal, and not rescued by persistence) so it is deliberately capped
		// out here. HvTrimSlope = 0 disables (fixed N everywhere).
		public static double HvTrimSlope = 0.6;   // N_eff ramps at this * rolling HV%; caps at RsiMultNumerator
		public static double HvTrimFloor = 8.0;   // hardest trim (floor on N_eff) for the quietest candles
		// Final-position EMA smoothing (DEFAULT ON, period 5). Smooths the FINAL traded position -- after clamp,
		// RSI trim, accurate-sizing, and the out-of-region override -- so the RSI-2 single-bar chatter (spike-down,
		// snap-back) is averaged out. Unlike lowering the RSI numerator (which cuts drawdown by holding LESS), this
		// cuts drawdown by holding STEADIER, so it preserves upside participation -- the one thing a lower N can't.
		// Validated: improves Sortino over the fixed-N=50 baseline (4/5 samples) and the basket on all metrics;
		// benefit concentrates in the mid-high HV band (50-100, the deployment sweet spot -- Sortino 0.63->0.89 at
		// 75-100). Period is stable p3-p8 (no overfit). CAVEAT: on low-HV names a harder trim (lower N) does better,
		// and on the most extreme (HV>100) names smoothing slightly lags the biggest bursts. 0 = off (raw position).
		public static int PositionSmoothPeriod = 5;

		// KAMA-distance smoothing (DEFAULT ON): smooth the traded position progressively HARDER the further the decision
		// close sits BELOW its Kaufman adaptive MA (KAMA), and stay at the light PositionSmoothPeriod (5) at or above it.
		// The EMA smoothing period ramps continuously from the P5 floor up to KamaSmoothMaxPeriod (50):
		//   below     = max(0, (kama - close) / kama)                                    // 0 when at/above the KAMA
		//   smoothPer = clamp( PositionSmoothPeriod + KamaSmoothSlope * below * KamaSmoothMaxPeriod,
		//                      PositionSmoothPeriod, KamaSmoothMaxPeriod )
		// The KAMA adapts by the engine's rolling price efficiency-ratio (curEr, over PersistWindow) as its smoothing
		// constant (fast 2 / slow 30). Rationale: a name pulling back below its KAMA chatters and heavy smoothing is
		// EFFICIENCY (return up + drawdown down); a name at/above its KAMA is trending, so it stays responsive at P5 and
		// participation is preserved. This REPLACES the earlier HV+ER "corner" smoother -- one continuous rule, no gate.
		// It matches the corner on broad OOS (4-sample median ret/DD 0.31 vs 0.29), beats it on the violent cohort, cuts
		// drawdown, and wins 14/18 basket names over full history; the one cost is giving back some explosive V-recovery
		// upside on the wildest names (ASST, IREN). A distance cap and an ER gate were both tried as guards on that cost
		// and BOTH degraded broad OOS without fixing it (the benefit and the cost share the trigger), so neither shipped.
		// KamaSmooth = false disables it (position stays at the flat PositionSmoothPeriod EMA).
		public static bool   KamaSmooth          = true;
		public static double KamaSmoothSlope     = 4.0;
		public static double KamaSmoothMaxPeriod = 50.0;   // ceiling on the smoothing EMA period (floor = PositionSmoothPeriod)

		// Blow-off / extension CAP (DEFAULT ON): when the decision close sits more than ExtCapPct% above its
		// ExtMaPeriod-bar SMA (an acute parabolic extension) AND the candle is NOT ST-Bull, cap exposure at
		// ExtCapCeil%. It only lowers the TOP (a min(): never raises exposure, never forces to cash), so it stops
		// the engine chasing the vertical tail without selling the trend. The extended tail carries near-zero
		// forward return but ~2x forward drawdown on the reverting cohort, so capping it is efficiency (return up
		// + drawdown down), not de-risk; the cut is eased by the final position smoothing. The ST-Bull EXCLUSION
		// is what makes it safe: the entire give-back lives in the non-ST-Bull states (a bull run's first crack
		// while extended -- the state machine's BullNeutral); the still-pushing ST-Bull bars are continuation, so
		// capping them only forfeits genuine-winner upside (a plain all-bars cap hurts the leveraged winners; the
		// gate erases that cost). A 50-bar MA isolates ACUTE spikes (a slower MA flags sustained trends). 0 = off.
		public static double ExtCapPct   = 55.0;
		public static double ExtCapCeil  = 60.0;
		public static int    ExtMaPeriod = 50;

		// ============ Drawdown-recovery exposure scaler (DEFAULT ON) ============
		// Two trailing drawdowns of the decision close are measured against its own rolling highs:
		//   dd60 = (max close over DdWindow bars      - close) / that max * 100
		//   dd30 = (max close over DdShortWindow bars - close) / that max * 100
		// A 60-bar high is always at least a 30-bar high, so dd60 >= dd30 ALWAYS and their relationship is a
		// pure "where in the drawdown are we" reading: dd30 ~ dd60 means the low is inside the last 30 bars
		// (price is still printing new short-window lows), while dd30 << dd60 means it has already climbed
		// well off an older low. The position is then multiplied by
		//   mult = clamp( DdRatioK * dd60 / max(dd30, DdRatioEps), DdRatioMin, DdRatioMax )
		// so at K = 0.5 the still-falling case scales toward 0.5x and a recovered one toward the 1.5x cap.
		// Applied before the position smoother (so the change eases in/out) and re-clamped to the exposure
		// band afterwards, so it can never breach MaxExposurePercent.
		//
		// WHY: a joint (dd30 x dd60) map of 2.6M bar-observations shows forward return tracks RECOVERY, not
		// depth. Holding dd30 at 0-2% and deepening dd60, mean fwd-20 runs 0.42 -> 14.4%; holding dd60 at
		// 30-45% and deepening dd30 (still falling) it runs 5.1 -> 1.3%. The still-falling diagonal is the
		// only region with a negative median forward return (30-40%: -1.21%, up-rate 47.0%), and the
		// shallow "at/near a 60-bar high" corner -- ~30% of all bars -- is the WEAKEST cell in the map
		// (median fwd-20 -0.04%, up-rate 49.4%) despite the engine previously holding 0.40 exposure there.
		// Scaling down both of those and up in the recovery cells cuts drawdown hard for a small return cost.
		//
		// VALIDATION: swept ~340 configs over 2,289 names (full >=$500M universe, 4 disjoint samples), scored
		// on the last 30% of each name's history. Broad median max-drawdown 17.9 -> 14.1 with Sharpe 0.38 ->
		// 0.40 at 26% less capital deployed; every one of 2,283/2,289 names ends shallower than buy-&-hold.
		// Two controls make the case that this is a signal and not just a smaller position:
		//   * a FLAT multiplier ignoring both drawdowns raises return/drawdown monotonically (to 0.398 at
		//     x0.4) but leaves Sharpe pinned at exactly 0.38 -- so return/drawdown is NOT comparable across
		//     configs with different exposure, and Sharpe at matched exposure is the honest metric. At
		//     matched exposure (~0.27) the flat control gets 0.38 and this scaler gets 0.40-0.43.
		//   * INVERTING the tilt (lever the still-falling bars) fails on all four samples (Sharpe 0.29-0.32)
		//     at unchanged exposure, so the direction carries real information.
		// The parameter choice also survives a walk-forward: chosen on the first 70% of history it picks the
		// same region (no gate, K 0.4-0.75) and still beats the baseline 4/4 on the untouched tail.
		// Complementary to the RSI-2 trim and the KAMA smoother -- ablating either leaves the scaler adding
		// value on top of what remains, and neither substitutes for it.
		//
		// KNOBS. DdRatioK is the participation dial: 0.4 is more defensive, 0.75 keeps more upside (0.4-0.75
		// is a flat plateau, so it is a preference, not a fit). DdRatioMin is the workhorse -- the de-lever
		// half does most of the work -- and DdRatioMax barely matters (1.5 ~ 2.0). DdRatioGate would apply
		// the scaler only past a minimum dd60; it is 0 (off) because gating it measurably HURT (de-levering
		// near the highs is a large part of the edge). WINDOWS: 30/60 is the shipped pair -- it is the best
		// on the concentrated high-vol basket; 15/45 and 20/60 score slightly higher on the broad universe
		// but weaker on the basket, while every long pair (45/90, 45/120, 60/120) fails outright.
		// DdRatioMode 2 is an equivalent bounded form, mult = 1 + K*(2*rec - 1) with rec = (dd60-dd30)/dd60,
		// which lands in the same place once K is tuned. DdRatioMode = 0 turns the scaler off.
		public static int    DdWindow      = 60;   // long drawdown window (bars)
		public static int    DdShortWindow = 30;   // short drawdown window (bars)
		public static int    DdRatioMode = 1;      // 1 = ratio form (shipped), 2 = recovered-fraction form, 0 = off
		public static double DdRatioK    = 0.5;
		public static double DdRatioMin  = 0.5;    // hardest de-lever (still making new short-window lows)
		public static double DdRatioMax  = 1.5;    // ceiling on the multiplier (recovered off an older low)
		public static double DdRatioGate = 0.0;    // require dd60 > this % before scaling (0 = always on)
		public static double DdRatioEps  = 1.0;    // floor on dd30 in mode 1 (keeps the ratio finite at a fresh high)
		public static bool   DdRatioPostSmooth = false;   // apply after the position smoother instead of before

		// The scaler as a pure function of the two drawdowns, so a harness or the Pine port can reproduce it.
		public static double DdRatioMult(double dd60Pct, double dd30Pct)
		{
			if (DdRatioMode == 0) return 1.0;
			if (dd60Pct <= DdRatioGate) return 1.0;
			double raw;
			if (DdRatioMode == 1)
				raw = DdRatioK * dd60Pct / Math.Max(dd30Pct, DdRatioEps);
			else
			{
				double rec = dd60Pct > 1e-9 ? (dd60Pct - dd30Pct) / dd60Pct : 0.0;
				raw = 1.0 + DdRatioK * (2.0 * rec - 1.0);
			}
			return Clamp(raw, DdRatioMin, DdRatioMax);
		}

		// Accurate full sizing (DEFAULT ON): when the TRUE target (pre-clamp adjEma) saturates full exposure, snap
		// the drift-band follower up to the clamp ceiling -- held = max(maxExp, max(maxExp-drift, adjEma-drift)) --
		// so the position sizes to 1.0 instead of being left stale-low (e.g. 0.7) by the rebalance deadband. This is
		// a correctness fix (the traded/displayed exposure matches the target at the full boundary), not an edge:
		// OOS performance is unchanged (the under-sizing window is narrow). false = legacy drift-band-only sizing.
		public static bool AccurateFullSizing = true;

		// Number of bar-periods per year, used only to annualize the Sharpe ratio.
		// 252 trading days for daily bars; set to 52 for weekly, 12 for monthly, etc.
		public static double PeriodsPerYear = 252.0;

		// target exposure for a bucket (the eight-value map above)
		private static double TargetExposure(LongTermState lt, ShortTermState st) =>
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
			var positions = new List<double>();
			var stratReturns = new List<double>();
			var bhReturns = new List<double>();
			var returnDates = new List<DateTime>();

			double alpha = 2.0 / (ExposureEmaPeriod + 1);
			double biasAlpha = 2.0 / (BiasEmaPeriod + 1);
			double driftBand = RebalanceDriftPercent / 100.0;
			double minExp = MinExposurePercent / 100.0;
			double maxExp = MaxExposurePercent / 100.0;

			double ema = double.NaN;     // EMA of the per-candle target exposure
			double held = double.NaN;    // deadband follower of the EMA (unclamped)
			double position = 0.0;       // clamped signed exposure actually applied
			double rsiAvgGain = 0.0, rsiAvgLoss = 0.0, rsiPrevClose = double.NaN, rsiMult = 1.0; int rsiCount = 0;
			double posSmooth = double.NaN;
			double kama = double.NaN;   // Kaufman adaptive MA of the decision close (uses curEr as the ER)

			// rolling SMA window of the decision close, for the extension cap
			var extMaWin = new Queue<double>(Math.Max(1, ExtMaPeriod));
			double extMaSum = 0.0;
			// rolling price efficiency ratio (Kaufman ER over PersistWindow), for corner smoothing
			var erCloseWin = new Queue<double>(); var erDiffWin = new Queue<double>();
			double erDiffSum = 0.0, erPrevClose = double.NaN, curEr = 1.0;

			// rolling highs of the decision close as monotonic-decreasing deques of (index, close) — the
			// front is always the window max, so both drawdowns are O(1) per bar.
			var ddWin = new LinkedList<(int Idx, double Close)>();        // DdWindow (long)
			var ddShortWin = new LinkedList<(int Idx, double Close)>();   // DdShortWindow (short)


			// rolling LT-direction window for the dynamic long bias
			var biasWindow = new Queue<double>(BiasPeriod);
			double biasSum = 0.0;
			double biasEma = double.NaN;   // EMA of the bias

			// per-candle dynamic-LongBias state: rolling HV (log-return sample stdev, annualized)
			// as of the decision bar, plus rolling exposure-EMA persistence over PersistWindow.
			var hvRetWindow = new Queue<double>(Math.Max(1, HvWindow));
			double hvSum = 0.0, hvSqSum = 0.0;
			double curHvPct = HvRefMean;              // rolling annualized HV %, refreshed each bar
			var perTgtWindow = new Queue<double>(Math.Max(1, PersistWindow) + 1);
			var perAbsWindow = new Queue<double>(Math.Max(1, PersistWindow));
			double perAbsSum = 0.0, perTgtPrev = double.NaN;
			double dynLbAlpha = 2.0 / (Math.Max(1, DynSmoothPeriod) + 1);
			double dynLbEma = double.NaN;             // EMA of the per-candle LongBias

			// updates curHvPct from the completed return into `latest` (no look-ahead)
			void UpdateHv(OhlcBar prevBar, OhlcBar latest)
			{
				if (prevBar.Close <= 0 || latest.Close <= 0) return;
				double lr = Math.Log(latest.Close / prevBar.Close);
				hvRetWindow.Enqueue(lr);
				hvSum += lr; hvSqSum += lr * lr;
				while (hvRetWindow.Count > HvWindow)
				{
					double old = hvRetWindow.Dequeue();
					hvSum -= old; hvSqSum -= old * old;
				}
				int n = hvRetWindow.Count;
				if (n >= 2)
				{
					double v = (hvSqSum - hvSum * hvSum / n) / (n - 1);
					curHvPct = Math.Sqrt(Math.Max(0.0, v)) * Math.Sqrt(PeriodsPerYear) * 100.0;
				}
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
			double StepExposure(LongTermState lt, ShortTermState st)
			{
				double target = TargetExposure(lt, st);
				ema = double.IsNaN(ema) ? target : alpha * target + (1.0 - alpha) * ema;

				// per-candle LongBias: the trait-scaled dynamic value (always on)
				double effLongBias;
				{
					// rolling persistence (Kaufman efficiency ratio) of the RAW target exposure —
					// deliberately NOT the exposure EMA, so it is independent of ExposureEmaPeriod
					// (measures how much the (LT,ST) state sequence trends vs. round-trips).
					if (!double.IsNaN(perTgtPrev))
					{
						double d = Math.Abs(target - perTgtPrev);
						perAbsWindow.Enqueue(d);
						perAbsSum += d;
						while (perAbsWindow.Count > PersistWindow)
							perAbsSum -= perAbsWindow.Dequeue();
					}
					perTgtPrev = target;
					perTgtWindow.Enqueue(target);
					while (perTgtWindow.Count > PersistWindow + 1)
						perTgtWindow.Dequeue();

					// each z-term only once its rolling window has warmed (else that term is 0)
					double zHv = hvRetWindow.Count >= 20 && HvRefStd > 0 ? (curHvPct - HvRefMean) / HvRefStd : 0.0;
					double zP = 0.0;
					if (perTgtWindow.Count > PersistWindow && PersRefStd > 0)
					{
						double pers = perAbsSum > 1e-9
							? Math.Min(1.0, Math.Abs(target - perTgtWindow.Peek()) / perAbsSum) : 1.0;
						zP = (pers - PersRefMean) / PersRefStd;
					}
					double z = zHv + zP;
					double raw = DynBase * Math.Exp(-DynDecay * z);
					raw = Clamp(raw, DynMin, DynMax);
					// EMA-smooth the per-candle bias so it can't whipsaw bar-to-bar, then floor at DynMin.
					dynLbEma = double.IsNaN(dynLbEma) ? raw : dynLbAlpha * raw + (1.0 - dynLbAlpha) * dynLbEma;
					effLongBias = Math.Max(dynLbEma, DynMin);
				}

				// dynamic long bias: rolling LT-direction sum over BiasPeriod candles / BiasPeriod, then EMA-smoothed.
				// Matches the Pine math.sum window.
				// Bull candle contributes (effLongBias + 1); a Bear candle a flat -1 (fully bearish, not propped up).
				double sig = lt == LongTermState.Bull ? effLongBias + 1.0 : lt == LongTermState.Bear ? -1.0 : 0.0;
				biasWindow.Enqueue(sig);
				biasSum += sig;
				while (biasWindow.Count > BiasPeriod)
					biasSum -= biasWindow.Dequeue();
				double dynBias = biasSum / BiasPeriod;
				biasEma = double.IsNaN(biasEma) ? dynBias : biasAlpha * dynBias + (1.0 - biasAlpha) * biasEma;

				double adjEma = Math.Abs(ema) * biasEma + ema;
				// Normal drift-band rebalance.
				if (double.IsNaN(held) || Math.Abs(held - adjEma) > driftBand)
					held = adjEma;
				// Accurate full sizing: if the target saturates full, snap the follower to the clamp ceiling so we
				// actually size to full (fix the drift-band leaving the position stale-low, e.g. 0.7 instead of 1).
				if (AccurateFullSizing && adjEma >= maxExp)
					held = Math.Max(maxExp, Math.Max(maxExp - driftBand, adjEma - driftBand));
				double posB = Clamp(held, minExp, maxExp);
				if (RsiOverlayPeriod > 0) posB = Clamp(posB * rsiMult, minExp, maxExp);
				return posB;
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

				UpdateHv(prevPrev, prev);   // rolling HV as of the decision bar

				// extension % of the decision close above its SMA (no look-ahead: uses prev), for the extension cap
				extMaWin.Enqueue(prev.Close); extMaSum += prev.Close;
				while (extMaWin.Count > ExtMaPeriod) extMaSum -= extMaWin.Dequeue();
				double extSma = extMaWin.Count > 0 ? extMaSum / extMaWin.Count : prev.Close;
				double extPct = extSma > 0 ? (prev.Close / extSma - 1.0) * 100.0 : 0.0;

				// rolling highs of the decision close (no look-ahead: uses prev) -> the two trailing drawdowns
				{
					int dw = Math.Max(2, DdWindow);
					while (ddWin.Count > 0 && ddWin.Last!.Value.Close <= prev.Close) ddWin.RemoveLast();
					ddWin.AddLast((i - 1, prev.Close));
					while (ddWin.First!.Value.Idx <= i - 1 - dw) ddWin.RemoveFirst();
				}
				double ddHigh = ddWin.First!.Value.Close;
				double ddPct = ddHigh > 0 ? (ddHigh - prev.Close) / ddHigh * 100.0 : 0.0;
				// the shorter window (how much of the long drawdown is still un-recovered)
				{
					int sw = Math.Max(2, DdShortWindow);
					while (ddShortWin.Count > 0 && ddShortWin.Last!.Value.Close <= prev.Close) ddShortWin.RemoveLast();
					ddShortWin.AddLast((i - 1, prev.Close));
					while (ddShortWin.First!.Value.Idx <= i - 1 - sw) ddShortWin.RemoveFirst();
				}
				double ddShortHigh = ddShortWin.First!.Value.Close;
				double ddShortPct = ddShortHigh > 0 ? (ddShortHigh - prev.Close) / ddShortHigh * 100.0 : 0.0;

				// rolling price efficiency ratio (no look-ahead: uses prev)
				if (!double.IsNaN(erPrevClose)) { double ed = Math.Abs(prev.Close - erPrevClose); erDiffWin.Enqueue(ed); erDiffSum += ed; while (erDiffWin.Count > PersistWindow) erDiffSum -= erDiffWin.Dequeue(); }
				erPrevClose = prev.Close;
				erCloseWin.Enqueue(prev.Close); while (erCloseWin.Count > PersistWindow + 1) erCloseWin.Dequeue();
				curEr = erCloseWin.Count > PersistWindow && erDiffSum > 1e-9 ? Math.Abs(prev.Close - erCloseWin.Peek()) / erDiffSum : 1.0;
					// Kaufman adaptive MA of the decision close (fast 2 / slow 30), used by the KAMA-distance smoother
					{ double sc = Math.Pow(curEr * (2.0/3.0 - 2.0/31.0) + 2.0/31.0, 2); kama = double.IsNaN(kama) ? prev.Close : kama + sc * (prev.Close - kama); }
				if (RsiOverlayPeriod > 0)   // Wilder RSI of the decision close -> rsiMult = min(RsiMultNumerator/RSI, 1)
				{
					if (!double.IsNaN(rsiPrevClose))
					{
						double ch = prev.Close - rsiPrevClose, g = ch > 0 ? ch : 0.0, ls = ch < 0 ? -ch : 0.0;
						rsiCount++;
						if (rsiCount < RsiOverlayPeriod) { rsiAvgGain += g; rsiAvgLoss += ls; }
						else if (rsiCount == RsiOverlayPeriod) { rsiAvgGain = (rsiAvgGain + g) / RsiOverlayPeriod; rsiAvgLoss = (rsiAvgLoss + ls) / RsiOverlayPeriod; }
						else { rsiAvgGain = (rsiAvgGain * (RsiOverlayPeriod - 1) + g) / RsiOverlayPeriod; rsiAvgLoss = (rsiAvgLoss * (RsiOverlayPeriod - 1) + ls) / RsiOverlayPeriod; }
						if (rsiCount >= RsiOverlayPeriod)
						{
							double rs = rsiAvgLoss > 1e-9 ? rsiAvgGain / rsiAvgLoss : 100.0;
							double rsi = 100.0 - 100.0 / (1.0 + rs);
							double numer = HvTrimSlope > 0
								? Math.Min(RsiMultNumerator, Math.Max(HvTrimFloor, HvTrimSlope * curHvPct))
								: RsiMultNumerator;
							rsiMult = rsi > 1e-6 ? Math.Min(numer / rsi, 1.0) : 1.0;
						}
					}
					rsiPrevClose = prev.Close;
				}
				position = StepExposure(lt, st.Value);
				// out-of-region when the raw exposure (ema) is bearish: 1=cash, 2=hold(B&H)
				if (BearRegimeMode != 0 && ema < 0.0)
					position = BearRegimeMode == 1 ? 0.0 : 1.0;
				// extension cap: stop chasing the parabolic top -- lower (never raise) exposure to the ceiling when
				// price is acutely extended above its SMA AND short-term momentum isn't bullish (ST != Bull).
				// Applied before smoothing so the cut eases in/out.
				if (ExtCapPct > 0 && extPct > ExtCapPct && st.Value != ShortTermState.Bull)
					position = Math.Min(position, ExtCapCeil / 100.0);
				// drawdown-recovery scaler: de-lever while still making new 30-bar lows, lever once recovered
				double ddRatioMult = DdRatioMult(ddPct, ddShortPct);
				if (DdRatioMode != 0 && !DdRatioPostSmooth)
					position = Clamp(position * ddRatioMult, minExp, maxExp);
				// KAMA-distance smoothing: heavier the further price sits below its KAMA, light (P5) at/above it.
				double smoothPer = PositionSmoothPeriod;
				if (KamaSmooth && !double.IsNaN(kama) && kama > 0)
				{ double below = Math.Max(0.0, (kama - prev.Close) / kama);
					smoothPer = Clamp(PositionSmoothPeriod + KamaSmoothSlope * below * KamaSmoothMaxPeriod, PositionSmoothPeriod, KamaSmoothMaxPeriod); }
				if (smoothPer > 0) { double aP = 2.0 / (smoothPer + 1); posSmooth = double.IsNaN(posSmooth) ? position : aP * position + (1.0 - aP) * posSmooth; position = posSmooth; }
				if (DdRatioMode != 0 && DdRatioPostSmooth)
					position = Clamp(position * ddRatioMult, minExp, maxExp);
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
				returnDates.Add(bar.Date);
				positions.Add(position);

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
			result.StratReturns = stratReturns;
			result.ReturnDates = returnDates;
			result.Positions = positions;
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
				UpdateHv(bars[^2], bars[^1]);
				position = StepExposure(lastLt, lastSt.Value);
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
