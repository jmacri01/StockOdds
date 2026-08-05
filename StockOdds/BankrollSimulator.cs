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
		// RESEARCH diagnostic: was the decision close at/above its KAMA on that bar?
		public List<bool>     KamaAbove { get; set; } = new();
		// RESEARCH diagnostic: SIGNED distance of the decision close from its KAMA, in % ((close-kama)/kama*100).
		// Exported rather than recomputed in a harness so a study cannot drift from the engine's own KAMA.
		public List<double>   KamaDist { get; set; } = new();
		// The per-bar SHORT-TERM state, so a downstream consumer (the options overlay) can condition on it.
		public List<ShortTermState> StState { get; set; } = new();
		// The per-bar LONG-TERM state, so a harness can score a single (LT, ST) bucket without re-deriving it.
		public List<LongTermState>  LtState { get; set; } = new();
		// RESEARCH diagnostic: bars spent in the (Bear, Bear) bucket, and how many of those the
		// BearBearMaxExposure ceiling actually bound on (position would have exceeded the cap).
		public int BearBearBars { get; set; }
		public int BearBearCapBound { get; set; }
		// Mean position the engine held on (Bear, Bear) bars BEFORE the ceiling was applied.
		public double BearBearMeanRaw { get; set; }
		// RESEARCH diagnostic: bars whose decision candle closed below the PRIOR candle's low.
		public int BearCandleBars { get; set; }
		// RESEARCH diagnostic: mean smoothing period the SHIPPED depth ramp asks for on ST Bear bars, so a
		// multiplier like "half smoothing" can be read in bars rather than as a ratio of an unknown.
		public double StBearMeanPeriod { get; set; }
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
		public static bool   BiasEnabled  = true;   // RESEARCH: false drops the LT-direction skew (see StepExposure)
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
		// BUCKET CEILING: a hard cap on the FINAL traded position on candles in the (LT Bear, ST Bear) bucket --
		// the most bearish cell of the map, whose raw target is -1.0. That target is already floored at 0 by
		// MinExposurePercent, and BearRegimeMode sends the position to cash whenever the raw EMA is negative, so
		// nothing here is about the STEADY state. What it targets is the LAG: on the bars just after a bullish run
		// rolls into Bear/Bear, the exposure EMA is still positive and the KAMA smoother then blends the cash
		// decision in gradually, so the engine is still carrying real exposure through the worst bucket. This caps
		// what can be carried there, applied LAST so no later layer can lift it back.
		// Percent of full exposure; < 0 = off (unbounded, the historical behaviour). 0 = fully flat in Bear/Bear.
		public static double BearBearMaxExposure = -1.0;
		// PER-ST-STATE ABLATION of the KAMA-distance smoother. When the current candle's ST state matches, the
		// KAMA ramp is skipped and that bar smooths at the flat PositionSmoothPeriod floor instead. Isolates which
		// short-term state the smoother is actually earning its keep in, one bucket at a time.
		// ======== DURATION AXIS ON THE SMOOTHER ========
		// The shipped KAMA ramp keys on DEPTH alone: smoothPer = clamp(P5 + slope*below*maxPer, P5, maxPer). A
		// pullback two bars old and one forty bars old smooth identically at equal depth. This adds elapsed time
		// as a second axis, in both directions, since it is not obvious which way it should run:
		//   DurSlope > 0  start light and smooth HARDER the longer the episode runs (a pullback that won't
		//                 resolve is more likely a real decline -- damp it)
		//   DurSlope < 0  start fully smoothed and become MORE RESPONSIVE with age (the hold-through-the-dip
		//                 case is strongest early; after that stop fighting the move)
		//   DurMode  1 = duration REPLACES the distance ramp (is time a better axis than depth?)
		//            2 = duration ADDS to it       (does time carry anything depth doesn't?)
		//            3 = ST-BEAR STREAK, replace: on an ST Bear candle the period is set by how many CONSECUTIVE
		//                ST Bear candles have run (streak resets on any non-Bear state); every other bar keeps the
		//                shipped depth ramp untouched. Isolates the streak from the rest of the engine.
		//            4 = ST-BEAR STREAK, dial the SHIPPED period: on an ST Bear candle, interpolate the depth
		//                ramp's own period toward the base as the streak lengthens (negative slope) or toward the
		//                ceiling (positive slope). Keeps the depth information and only dials it with streak age --
		//                the most literal reading of "dial smoothing down with consecutive ST Bear candles".
		//                Modes 3 and 4 ignore DurSource and always use the ST-Bear streak.
		//   DurSource 0 = consecutive bars below the KAMA (resets at/above -- shipped behaviour is preserved above)
		//             1 = consecutive bars in the current ST state
		//   DurFull  = bars at which the duration effect saturates
		public static int    DurMode   = 0;      // 0 = off
		public static int    DurSource = 0;
		public static double DurSlope  = 1.0;
		public static double DurFull   = 20.0;
		// HV-SCALED DECAY RATE: relax the smoothing FAST on high-vol names and SLOWLY on low-vol ones, off the
		// engine's own rolling HV -- `curHvPct`, which is already HV(60) since HvWindow = 60, updated per bar with
		// no look-ahead.  durFullEff = clamp(DurFull * (DurHvRef / hv)^DurHvExp, DurHvMin, DurHvMax)
		// so a name sitting at DurHvRef behaves exactly as the flat rule does, and DurHvExp sets the direction:
		//   +1  fast decay when volatile, slow when quiet   (the proposal)
		//   -1  the opposite, kept as the sign control
		// Motivated by the flat rule's bimodal basket result: it helped the high-vol compounders and hurt the
		// steady names, so the decay rate may simply want to be per-name rather than global.
		// DurHvRef = 0 turns the scaling off and DurFull applies flat to every name.
		public static double DurHvRef = 0.0;
		public static double DurHvExp = 1.0;
		public static double DurHvMin = 5.0;
		public static double DurHvMax = 250.0;

		// SMOOTHING PERIOD APPLIED ON ST BEAR CANDLES, overriding the depth ramp on those bars only.
		// This is the clean parameterisation of what the streak sweep actually found: the streak counter turned out
		// to carry NOTHING (saturating it on the first bar scored identically to every streak-dependent variant, and
		// adding streak dependence only degraded it), while the amount of smoothing applied on ST Bear candles
		// mattered a lot and monotonically. Deliberately NOT clamped to KamaSmoothMaxPeriod so periods beyond the
		// depth ramp's ceiling can be tested without moving the ceiling for every other bar.
		//   0 = off (shipped depth ramp everywhere). PositionSmoothPeriod here == "skip smoothing on ST Bear".
		public static double StBearSmoothPeriod = 0.0;
		// MULTIPLIER on the depth ramp's own period, applied on ST Bear candles. 1.0 = off (shipped), 0.5 = "half
		// smoothing", 2.0 = double. Multiplicative rather than absolute because the depth ramp already varies the
		// period bar to bar (5..50 depending on how far below the KAMA price sits), so halving it preserves that
		// shape instead of flattening it to one number the way StBearSmoothPeriod does. Floored at the base period.
		public static double StBearSmoothMult = 1.0;

		// SKIP THE KAMA RAMP ON A BEAR CANDLE -- a decision candle that CLOSES BELOW THE PREVIOUS CANDLE'S LOW.
		// Not a state: one decisive down bar. On those bars the smoother drops to the base period so the position
		// can react at once, on the theory that a close through the prior low is exactly the information heavy
		// below-KAMA smoothing suppresses. Much rarer than ST Bear (~30% of bars), so the firing rate is reported
		// alongside the result -- a null result on a rule that fires on 4% of bars means something different from
		// a null result on one that fires on 30%.
		//   0 = off | 1 = skip on bear candles | 2 = INVERSE CONTROL, skip on everything except bear candles
		public static int KamaBearCandleMode = 0;

		// BITMASK over ShortTermState (bit s = 1 << (int)state): 0 = off, i.e. KAMA smoothing everywhere (shipped).
		// bit0 Bull, bit1 BullNeutral, bit2 BearNeutral, bit3 Bear. A mask lets combinations be tested too
		// (e.g. 0b1001 = the two DIRECTIONAL states, 0b0110 = the two NEUTRAL ones).
		public static int KamaSmoothOffMask = 0;
		// The same bucket in the other direction: multiply the (Bear, Bear) position by this before the ceiling.
		// Only worth testing because the ceiling measured WORSE than an exposure-matched flat haircut at every
		// level, which says the exposure carried in that bucket is more productive than the average bar, not less.
		// 1.0 = off.
		public static double BearBearMult = 1.0;
		// CONTROL ONLY (see the flat-haircut method): a signal-free constant multiplier on the final position.
		// Any rule that reduces exposure moves return/drawdown by that fact alone, so a candidate must be scored
		// against a haircut that holds the SAME mean exposure with no signal in it. 1.0 = off.
		public static double FlatHaircut = 1.0;
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
		// SHIPPED: effectively UNCAPPED below the extension threshold. The cap only binds when 0.6*HV exceeds it, so
		// 1000 means it never binds in practice (it would need HV > 1667) and the trim is purely HV-slope-driven
		// there. The binding cap now lives in the extended zone only -- see ExtTrimCap below.
		public static double RsiMultNumerator = 1000.0;
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
		// RESEARCH ONLY (default off): ER-BANDED numerator cap. When RsiErBandN > 0, bars whose Kaufman efficiency
		// ratio (curEr, over PersistWindow = 63) falls in [RsiErBandLo, RsiErBandHi) use RsiErBandN as the cap
		// instead of RsiMultNumerator -- i.e. those bars can be selectively "uncapped" (N = 100 leaves the cap
		// inert below HV 167, so the trim becomes purely slope-driven there). Note the cap only BINDS at all when
		// 0.6 * HV exceeds the cap, so a band populated by low-HV bars is a no-op no matter what N is set to.
		public static double RsiErBandN  = 0.0;
		public static double RsiErBandLo = 0.0;
		public static double RsiErBandHi = 1.0;
		// RESEARCH ONLY (default off): the same idea banded by SIGNED KAMA DISTANCE, (close-kama)/kama*100.
		// Applied after the ER band, so if both are set the KAMA band wins on overlapping bars.
		public static double RsiKamaBandN  = 0.0;
		public static double RsiKamaBandLo = -1e9;
		public static double RsiKamaBandHi =  1e9;
		// RESEARCH ONLY (default off): a cleaner parameterisation of the same idea. A bar is EXTENDED when its
		// signed KAMA distance (close-kama)/kama*100 >= ExtTrimThreshold. Then
		//     numer = min(cap_zone, max(HvTrimFloor, slope_zone * HV))
		// with the extended zone free to use its own slope and/or cap. This expresses three different shapes:
		//   * a flat CEILING when extended            -> ExtTrimCap = 30, ExtTrimSlope = 0 (inherit 0.6)
		//   * a PROPORTIONAL harder trim when extended -> ExtTrimSlope = 0.25, ExtTrimCap = 0
		//   * NO HV conditioning at all                -> HvTrimSlope = 0, then numer is just the cap per zone
		// ExtTrimThreshold = 1e9 disables. ExtTrimSlope/Cap = 0 means "inherit the normal one".
		// SHIPPED 2026-07-31 -- the KAMA trim adapter. A bar is EXTENDED when its signed KAMA distance
		// (close-kama)/kama*100 >= ExtTrimThreshold, and there the numerator is capped at ExtTrimCap:
		//     numer = min(cap_zone, max(HvTrimFloor, slope_zone * HV))
		// So the shipped stack is: NO cap below +12% (trim = 0.6*HV, floored at 8), cap 30 at/above +12%.
		//
		// WHY. The KAMA-distance map (2.75M bars, 4 samples) shows median forward-20 return falling MONOTONICALLY
		// with distance -- +1.08% at -20..-12% below the KAMA down to -0.27% at +12..+20% above -- with 4/4 sign
		// replication in every populated bucket, while the engine's own exposure ran INVERTED to it (0.25 in the best
		// buckets, 0.57-0.66 in the worst). This lifts the cap where forward returns are best and imposes a tighter
		// one where they are worst. Because the numerator is a CAP and the trim is min(cap, 0.6*HV), it does nothing
		// at all to names under HV 50 (68% of the universe) and acts progressively on the high-vol tail: at HV 116
		// the below-zone numerator goes 40 -> 68, at HV 199 it goes 40 -> 88, while the extended zone goes 40 -> 30.
		//
		// MEASURED (2,289 names, last 30% OOS, means; against a baseline with the extension cap OFF, which is now
		// its permanent state -- the cap was removed the same day, see the note below): Sharpe 0.379 -> 0.383 on
		// 4 of 4 disjoint samples, excess ret/dd over a matched-exposure flat curve +0.005, walk-forward IN
		// 0.157 -> 0.159, decliners -0.238 -> -0.230, violent 0.858 -> 0.863, steady 0.711 -> 0.713, curated basket
		// 281 -> 309 with basket Sharpe 0.674 -> 0.685, at mean exposure 0.387 -> 0.389 (i.e. essentially UNCHANGED
		// capital, so this is reshaping rather than sizing -- and Sharpe is mathematically invariant to a pure
		// exposure change, verified by scaling the shipped position by k = 0.85..1.05 and getting 0.380 every time).
		// Cap 35 rather than 30 because 30 gives more broad Sharpe (+0.007) but goes NEGATIVE on excess ret/dd;
		// 35 is the only setting in a 42-cell grid where every metric improves and none regresses.
		//
		// CONTROLS. (a) A flat CEILING beats a PROPORTIONAL harder slope on the basket and violent cohorts, though
		// the proportional form (ExtTrimSlope 0.45) wins broad Sharpe by a further 0.007 -- it was rejected because
		// it degrades violent 0.867 -> 0.849. (b) Removing the cap entirely instead ("no zone rule") gives similar
		// return at +3.6 drawdown, MORE capital (0.398 vs 0.384) and LOWER Sharpe (0.379), so the extended-zone cap
		// is what pays for lifting the one below it. (c) Dropping the HV slope and using a fixed numerator per zone
		// fails outright: all 12 tuned combinations score 0/4 with excess ret/dd of -0.11 to -0.40. The slope is
		// load-bearing and cannot be replaced by zone-conditioning.
		//
		// HONEST WEAK POINTS -- read these before citing the basket numbers.
		//   * The basket gain is FOUR NAMES. Of a +803 total per-name return change, ASTS is +392, IREN +160,
		//     MSTR +152 and ASST +148 -- the four highest-HV names, i.e. exactly where cap removal bites hardest.
		//     The other fifteen net NEGATIVE. Per-name it is a coin flip: 10 of 19 better on return, ratio and
		//     Sharpe alike, and the MEDIAN name is worse on all three (ret 76 -> 72, dd 44.2 -> 46.6, ratio
		//     1.71 -> 1.55). The broad 4/4 result across 2,289 names is the real evidence, not the basket mean.
		//   * It COSTS the options expression: PMCC + short puts falls 137 -> 123 mean return with Sharpe
		//     0.431 -> 0.414, better on only 4 of 19 names (MSTR 95 -> 17, ASTS 603 -> 494).
		//   * Buy-&-hold comparison is unaffected and still clean: 19 of 19 shallower drawdown, 13 of 19 higher return.
		// ExtTrimThreshold = 1e9 disables the adapter; ExtTrimSlope/Cap = 0 mean "inherit the normal one".
		public static double ExtTrimThreshold = 12.0;
		public static double ExtTrimSlope     = 0.0;
		public static double ExtTrimCap       = 35.0;
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

		// REMOVED 2026-07-31: the blow-off / extension CAP (pin exposure to a 60% ceiling when the close sat more
		// than 55% above its 50-bar SMA and the candle was not ST-Bull). It had been default-off since earlier the
		// same day and the feature is now deleted -- knobs ExtCapPct/ExtCapCeil/ExtMaPeriod and the rolling SMA that
		// fed them are gone. NOT removed for being wrong: it was a small but consistent broad-universe positive
		// (turning it off lost 24 of 24 sample-comparisons across all three modes, and the violent/decliners/steady
		// cohorts each preferred it 6 of 6). It was removed because the margin was only 0.001-0.002 Sharpe while it
		// cost the concentrated high-vol basket 14-19 points of return (267 -> 281 shipped, 281 -> 294 alongside the
		// KAMA trim adapter) and materially damaged the options expression (IREN PMCC + short puts 71 -> 44 when the
		// layer was switched on in a layer-by-layer build) -- a bad trade for a basket/overlay-focused deployment,
		// and not worth carrying a dead code path for. Its original rationale still stands on its own terms and is
		// preserved in git history: the acutely extended tail carries near-zero forward return but ~2x the forward
		// drawdown on the reverting high-vol cohort, and the ST-Bull EXCLUSION was what made it safe (the give-back
		// lives entirely in the non-ST-Bull states; capping still-pushing ST-Bull bars only forfeits winner upside).
		// Do NOT confuse this with the shipped KAMA trim adapter (ExtTrimThreshold/ExtTrimCap/ExtTrimSlope above),
		// which is a different mechanism on the same "extension" theme and remains DEFAULT ON.

		// ============ Peak-age exposure scaler, BELOW THE KAMA ONLY (DEFAULT ON) ============
		// Two trailing drawdowns of the decision close are measured against its own rolling highs:
		//   dd60 = (max close over DdWindow bars      - close) / that max * 100
		//   dd30 = (max close over DdShortWindow bars - close) / that max * 100
		// A 60-bar high is always at least a 30-bar high, so dd60 >= dd30 ALWAYS. The position is multiplied by
		//   mult = clamp( DdRatioK * dd60 / max(dd30, DdRatioEps), DdRatioMin, DdRatioMax )
		// ...but ONLY on bars where close < KAMA (DdRatioKamaMode = 1). Applied before the position smoother so
		// the change eases in and out, and re-clamped to the exposure band afterwards, so it can never breach
		// MaxExposurePercent.
		//
		// WHAT THE RATIO ACTUALLY MEASURES -- state this precisely, because getting it wrong once already cost
		// a shipped release (see the disabled-then-refined history below). dd30 == dd60 exactly when
		// hi30 == hi60, i.e. when the 60-bar peak falls INSIDE the last 30 bars. So:
		//   ratio == 1  <=>  the peak is RECENT (a fresh pullback from a high made in the last 30 bars)
		//                    -> mult pins to K, the hardest de-lever, AT ANY DEPTH
		//   ratio > 1   <=>  the 60-bar peak is OLDER than 30 bars, and it grows with the gap between the two
		//                    highs -> price is grinding back up toward a recent high that still sits well under
		//                    an older peak -> mult toward the DdRatioMax cap
		// The expression encodes PEAK AGE, and being scale-free it discards depth entirely. It does NOT
		// "de-lever while price makes new short-window lows" -- that description was wrong, and the difference
		// is the whole reason for the KAMA confinement below.
		//
		// WHY BELOW THE KAMA ONLY. Peak age means opposite things in the two regimes. Above the KAMA a recent
		// peak is just an ordinary uptrend pullback and cutting it is pure cost; below the KAMA a recent peak is
		// a fresh break down and cutting it is protection. Measured (2,289 names, last 30% OOS, means, each
		// bucket's bars scored as their own return series):
		//   above KAMA: return/drawdown 0.558 -> 0.475 with the scaler on   (it DESTROYS this bucket, -15%)
		//   below KAMA: return/drawdown 0.514 -> 0.540, Sharpe 0.70 -> 0.76 (it helps)
		// Bars split 50.9% above / 49.1% below. Running it above-KAMA-only is the single worst config tested.
		//
		// VALIDATION. ~190 configs, 2,289 names (full >=$500M universe, 4 disjoint samples), scored on the last
		// 30% of each name's history, MEANS across names. Because a signal-free flat haircut raises
		// return/drawdown from 0.757 to 0.849 purely by holding more capital, raw return/drawdown is NOT
		// comparable across configs; every candidate is scored as `ratio - flatRatio(exposure)` against a
		// matched-exposure flat-haircut curve (DdRatioMode = 3 builds it).
		//   shipped:  return 18.0 -> 19.0, drawdown 21.2 -> 21.6, ratio 0.849 -> 0.878, EXCESS +0.029,
		//             Sharpe 0.36 -> 0.38, mean exposure 0.38 -> 0.39 -- i.e. UNCHANGED capital deployed, so
		//             this reshapes when it holds rather than holding less. Better on all 4 samples.
		//   cohorts:  violent 2.20 -> 2.30, decliners -0.32 -> -0.30, curated basket 5.84 -> 6.28
		//             (13 of 19 basket names higher return, 13 of 19 better return/drawdown).
		//   controls: the same scaler run EVERYWHERE scores +0.001 (and -0.018 at the old K), above-KAMA-only
		//             -0.045, and the INVERTED tilt -0.022/-0.032 on 0 of 4 samples. So the confinement -- not
		//             the softer K -- is what does the work, and the direction carries real information.
		//   walk-forward: chosen on the first 70% of history it still wins on the untouched tail
		//             (in-sample Sharpe 0.13 vs 0.12, out-of-sample 0.41 vs 0.38).
		//
		// HISTORY. The unconfined form shipped 2026-07-30, was disabled 2026-07-31 on a live-chart report, and
		// returns here confined. The report: IREN 2025-09-08 had run 16.58 -> 26.19 (+58%) and sat 10% below a
		// peak six sessions old, so dd30 == dd60 == 10.03%, ratio 1.000, and it took the MAXIMUM de-lever
		// (0.6982 traded vs 1.2850 with the scaler off) -- identically to a name 40% down and still falling.
		// Over 208k scored bars, 54% of all bars had the peak inside the last 30 bars, ~41% of ALL bars took the
		// maximum cut, and 26.5% of ALL bars were uptrend bars (close > 50-bar SMA) being halved at a median
		// depth of just 6.3%. Confining to below the KAMA removes exactly that population: the same IREN bar now
		// prices at 1.2850, untouched, because it is above its KAMA.
		//
		// KNOBS. DdRatioK is the participation dial, on a flat plateau from 0.6 to 0.9 (excess +0.027 to +0.030,
		// falling to +0.005 by K = 0.3), so 0.75 is a preference inside a plateau, not a fit. Clamps barely
		// matter. DdRatioGate (require a minimum dd60 first) stays 0 -- gating measurably HURTS. DdRatioMinDd is
		// now REDUNDANT rather than load-bearing: the fresh-high pathology it guarded only fires above the KAMA,
		// so the confinement already excludes it and the knob is flat within +/-0.003 from 0 to 5; it is kept at
		// 1 as a cheap belt-and-braces. WINDOWS: 30/60 is best (+0.025) ahead of 30/120 (+0.019), 30/90 (+0.017)
		// and 20/60 (+0.013); 60/120 fails outright (-0.019). DdRatioMode 2 is an equivalent bounded form,
		// mult = 1 + K*(2*rec - 1) with rec = (dd60-dd30)/dd60, landing in the same place once K is tuned.
		// DdRatioMode = 0 turns the scaler off and restores the pre-scaler engine.
		public static int    DdWindow      = 60;   // long drawdown window (bars)
		public static int    DdShortWindow = 30;   // short drawdown window (bars)
		public static int    DdRatioMode = 1;      // 0 = off, 1 = ratio form (default), 2 = recovered-fraction form
		public static double DdRatioK    = 0.75;   // multiplier when the peak is recent; plateau 0.6-0.9
		public static double DdRatioMin  = 0.5;    // hardest de-lever (fresh break down from a recent peak)
		public static double DdRatioMax  = 2.0;    // ceiling on the multiplier (recovering under an older peak)
		public static double DdRatioGate = 0.0;    // require dd60 > this % before scaling (0 = always on)
		public static double DdRatioEps  = 1.0;    // floor on dd30 in mode 1 (keeps the ratio finite at a fresh high)
		public static bool   DdRatioPostSmooth = false;   // apply after the position smoother instead of before

		// ---- MINIMUM DRAWDOWN ON BOTH WINDOWS (DEFAULT ON, 1%) ----
		// The scaler is neutral (mult = 1) unless BOTH dd60 and dd30 exceed DdRatioMinDd, so it only acts when
		// there is an actual drawdown on both horizons. Originally this closed a real defect: with no drawdown
		// dd60 -> 0, so the raw ratio -> 0 and the clamp FLOORS it at DdRatioMin -- the HARDEST de-lever firing
		// at a fresh 60-bar high, where there is nothing to protect against (19.5% of bars sat within 2% of the
		// 60-bar high and 57.7% of those were being cut -- 11.2% of ALL bars). Under the KAMA confinement that
		// population is already excluded, since a bar at a fresh high is above its KAMA by construction, so this
		// knob is now REDUNDANT: excess is flat within +/-0.003 across DdRatioMinDd 0 to 5. Kept at 1 as cheap
		// insurance for anyone who sets DdRatioKamaMode = 0. Do NOT raise it far in that unconfined case:
		// requiring a SUBSTANTIAL drawdown walks the feature back to a plain haircut. 0 disables the minimum.
		public static double DdRatioMinDd = 1.0;
		// ---- THIRD (MICRO) DRAWDOWN WINDOW ----
		// The shipped scaler is a ratio of TWO trailing drawdowns, which reads PEAK AGE: dd30 == dd60 exactly when
		// the 60-bar peak sits inside the last 30 bars. A third, shorter window reads the same thing at finer
		// resolution -- dd15 == dd30 when the 30-bar peak sits inside the last 15 -- so it can distinguish "the
		// peak is 20 bars old" from "the peak is 5 bars old", which dd60/dd30 alone cannot.
		//   DdMicroMode 0 = off (shipped two-window form)
		//               1 = SECOND STAGE: mult *= clamp(DdMicroK * dd30/dd15, DdRatioMin, DdRatioMax)
		//                   -- the literal "add a 15-bar drawdown to the scaler"
		//               2 = REPLACE the short window: the ratio becomes dd60/dd15
		//               3 = REPLACE the long window:  the ratio becomes dd30/dd15
		// Modes 2 and 3 are re-parameterisations of the existing form and overlap the earlier window re-sweep;
		// mode 1 is the genuinely new shape, since it stacks two independent age reads.
		public static int    DdMicroWindow = 15;
		public static int    DdMicroMode   = 0;
		public static double DdMicroK      = 1.0;
		// ---- KAMA CONFINEMENT (DEFAULT 1 = below only) ----
		// Restrict the scaler by position relative to the KAMA (the same KAMA the position smoother uses).
		//   0 = everywhere, 1 = only when close < KAMA (DEFAULT), 2 = only when close >= KAMA
		// See the block above for why: the scaler's effect flips sign across the KAMA, so this is the difference
		// between a feature worth shipping (+0.029 excess) and one that is worse than a flat haircut (-0.018).
		public static int DdRatioKamaMode = 1;

		// The scaler as a pure function of the two drawdowns, so a harness or the Pine port can reproduce it.
		// Three-window form. dd15 is only consulted when DdMicroMode != 0, so the two-arg overload below still
		// reproduces the shipped scaler exactly (and the Pine port keeps working).
		public static double DdRatioMult(double dd60Pct, double dd30Pct, double dd15Pct)
		{
			if (DdMicroMode == 0) return DdRatioMult(dd60Pct, dd30Pct);
			if (DdMicroMode == 2) return DdRatioMult(dd60Pct, dd15Pct);          // short window -> 15
			if (DdMicroMode == 3) return DdRatioMult(dd30Pct, dd15Pct);          // both windows shortened
			// mode 1: stack a second, finer age read on top of the shipped one
			double stage1 = DdRatioMult(dd60Pct, dd30Pct);
			if (DdRatioMinDd > 0 && dd15Pct < DdRatioMinDd) return stage1;       // no real drawdown on the micro window
			double stage2 = Clamp(DdMicroK * dd30Pct / Math.Max(dd15Pct, DdRatioEps), DdRatioMin, DdRatioMax);
			return Clamp(stage1 * stage2, DdRatioMin, DdRatioMax);
		}

		public static double DdRatioMult(double dd60Pct, double dd30Pct)
		{
			if (DdRatioMode == 0) return 1.0;
			if (dd60Pct <= DdRatioGate) return 1.0;
			// only act when BOTH horizons show a real drawdown
			if (DdRatioMinDd > 0 && (dd60Pct < DdRatioMinDd || dd30Pct < DdRatioMinDd)) return 1.0;
			// mode 3 = FLAT control (ignores both drawdowns): the yardstick any reshaping must beat at
			// matched exposure, since Sharpe is scale-invariant.
			if (DdRatioMode == 3) return Clamp(DdRatioK, DdRatioMin, DdRatioMax);
			double raw;
			if (DdRatioMode == 1)
				raw = DdRatioK * dd60Pct / Math.Max(dd30Pct, DdRatioEps);
			else
			{
				double rec = dd60Pct > 1e-9 ? (dd60Pct - dd30Pct) / dd60Pct : 0.0;
				// mode 4 = INVERTED control (lever the still-falling bars, de-lever the recovered ones). If a
				// candidate's mirror image also beats the baseline, the direction carries nothing.
				raw = DdRatioMode == 4
					? 1.0 + DdRatioK * (1.0 - 2.0 * rec)
					: 1.0 + DdRatioK * (2.0 * rec - 1.0);
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
			var kamaAbove = new List<bool>();
			var kamaDist = new List<double>();
			var stStates = new List<ShortTermState>();
			var ltStates = new List<LongTermState>();
			int bbBars = 0, bbBound = 0; double bbRawSum = 0.0;   // (Bear, Bear) ceiling diagnostics
			int bcBars = 0;                                       // bear candles (close < prior low)
			double sbPerSum = 0; int sbPerN = 0;                  // shipped smoothing period on ST Bear bars
			// run lengths for the duration axis: consecutive bars below the KAMA, and consecutive bars in the
			// current ST state. Both count the bar being decided ON (so a fresh break reads 1, not 0).
			int belowRun = 0, stRun = 0, stBearRun = 0;
			ShortTermState? prevSt = null;
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

			// rolling price efficiency ratio (Kaufman ER over PersistWindow), for corner smoothing
			var erCloseWin = new Queue<double>(); var erDiffWin = new Queue<double>();
			double erDiffSum = 0.0, erPrevClose = double.NaN, curEr = 1.0;

			// rolling highs of the decision close as monotonic-decreasing deques of (index, close) — the
			// front is always the window max, so both drawdowns are O(1) per bar.
			var ddWin = new LinkedList<(int Idx, double Close)>();        // DdWindow (long)
			var ddShortWin = new LinkedList<(int Idx, double Close)>();   // DdShortWindow (short)
			var ddMicroWin = new LinkedList<(int Idx, double Close)>();   // DdMicroWindow (third/finest)


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

				// RESEARCH: BiasEnabled = false removes the LT-direction skew entirely, so a layer-by-layer
				// ablation can start from the bare (LT,ST) map. Default true = shipped.
				double adjEma = BiasEnabled ? Math.Abs(ema) * biasEma + ema : ema;
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
				// the third, finest window -- same monotonic-deque pattern
				{
					int mw = Math.Max(2, DdMicroWindow);
					while (ddMicroWin.Count > 0 && ddMicroWin.Last!.Value.Close <= prev.Close) ddMicroWin.RemoveLast();
					ddMicroWin.AddLast((i - 1, prev.Close));
					while (ddMicroWin.First!.Value.Idx <= i - 1 - mw) ddMicroWin.RemoveFirst();
				}
				double ddMicroHigh = ddMicroWin.First!.Value.Close;
				double ddMicroPct = ddMicroHigh > 0 ? (ddMicroHigh - prev.Close) / ddMicroHigh * 100.0 : 0.0;

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
							double nCap = (RsiErBandN > 0 && curEr >= RsiErBandLo && curEr < RsiErBandHi)
								? RsiErBandN : RsiMultNumerator;
							if (RsiKamaBandN > 0 && !double.IsNaN(kama) && kama > 0)
							{
								double kdPct = (prev.Close - kama) / kama * 100.0;
								if (kdPct >= RsiKamaBandLo && kdPct < RsiKamaBandHi) nCap = RsiKamaBandN;
							}
							double effSlope = HvTrimSlope, effCap = nCap;
							if (ExtTrimThreshold < 1e8 && !double.IsNaN(kama) && kama > 0
								&& (prev.Close - kama) / kama * 100.0 >= ExtTrimThreshold)
							{
								if (ExtTrimSlope > 0) effSlope = ExtTrimSlope;
								if (ExtTrimCap > 0) effCap = ExtTrimCap;
							}
							double numer = effSlope > 0
								? Math.Min(effCap, Math.Max(HvTrimFloor, effSlope * curHvPct))
								: effCap;
							rsiMult = rsi > 1e-6 ? Math.Min(numer / rsi, 1.0) : 1.0;
						}
					}
					rsiPrevClose = prev.Close;
				}
				position = StepExposure(lt, st.Value);
				// out-of-region when the raw exposure (ema) is bearish: 1=cash, 2=hold(B&H)
				if (BearRegimeMode != 0 && ema < 0.0)
					position = BearRegimeMode == 1 ? 0.0 : 1.0;
				// run lengths, updated BEFORE the smoother reads them so the current bar is included
				belowRun = (!double.IsNaN(kama) && kama > 0 && prev.Close < kama) ? belowRun + 1 : 0;
				stRun = (prevSt != null && prevSt.Value == st.Value) ? stRun + 1 : 1;
				// consecutive ST BEAR candles; resets to 0 on any non-Bear state
				stBearRun = st.Value == ShortTermState.Bear ? stBearRun + 1 : 0;
				prevSt = st.Value;

				// peak-age scaler, below the KAMA only: de-lever a fresh break down from a recent peak, lever a
				// name grinding back toward a recent high that still sits under an older one.
				double ddRatioMult = DdRatioMult(ddPct, ddShortPct, ddMicroPct);
				if (DdRatioKamaMode != 0 && !double.IsNaN(kama))
				{
					bool above = prev.Close >= kama;
					if (DdRatioKamaMode == 1 && above) ddRatioMult = 1.0;    // only act BELOW the KAMA
					if (DdRatioKamaMode == 2 && !above) ddRatioMult = 1.0;   // only act AT/ABOVE the KAMA
				}
				if (DdRatioMode != 0 && !DdRatioPostSmooth)
					position = Clamp(position * ddRatioMult, minExp, maxExp);
				// KAMA-distance smoothing: heavier the further price sits below its KAMA, light (P5) at/above it.
				double smoothPer = PositionSmoothPeriod;
				bool kamaOffHere = KamaSmoothOffMask != 0 && (KamaSmoothOffMask & (1 << (int)st.Value)) != 0;
				// bear candle = the decision candle closed below the PRIOR candle's low
				bool bearCandle = prev.Close < prevPrev.Low;
				if (bearCandle) bcBars++;
				if (KamaBearCandleMode == 1 && bearCandle) kamaOffHere = true;
				if (KamaBearCandleMode == 2 && !bearCandle) kamaOffHere = true;
				if (KamaSmooth && !kamaOffHere && !double.IsNaN(kama) && kama > 0)
				{ double below = Math.Max(0.0, (kama - prev.Close) / kama);
					smoothPer = Clamp(PositionSmoothPeriod + KamaSmoothSlope * below * KamaSmoothMaxPeriod, PositionSmoothPeriod, KamaSmoothMaxPeriod); }
				// DURATION axis (see DurMode). The shipped ramp keys on DEPTH only, so a 2-bar-old pullback and a
				// 40-bar-old one at the same depth smooth identically. This makes elapsed time a second axis.
				if (DurMode != 0 && !kamaOffHere)
				{
					double dur = DurMode >= 3 ? stBearRun : (DurSource == 1 ? stRun : belowRun);
					// per-name decay length off rolling HV(60), or the flat DurFull when scaling is off
					double durFullEff = DurFull;
					if (DurHvRef > 0 && curHvPct > 1e-6)
						durFullEff = Clamp(DurFull * Math.Pow(DurHvRef / curHvPct, DurHvExp), DurHvMin, DurHvMax);
					double durFrac = durFullEff > 0 ? Clamp(dur / durFullEff, 0.0, 1.0) : 0.0;
					double travel = KamaSmoothMaxPeriod - PositionSmoothPeriod;
					if (DurMode == 1)
					{
						// duration REPLACES the distance ramp. Positive slope starts at the base period and
						// smooths harder the longer the episode runs; negative slope starts fully smoothed and
						// becomes more responsive with age.
						// OUTSIDE an episode (source 0, price at/above its KAMA) fall back to the base period --
						// otherwise a negative slope would read durFrac = 0 as "maximum smoothing" and smooth
						// hardest on exactly the trending bars the shipped rule keeps responsive, which turns
						// this into a global heavy-smoothing change wearing a duration costume.
						bool inEpisode = DurSource == 1 || belowRun > 0;
						double start = DurSlope >= 0 ? PositionSmoothPeriod : KamaSmoothMaxPeriod;
						smoothPer = inEpisode
							? Clamp(start + DurSlope * durFrac * travel, PositionSmoothPeriod, KamaSmoothMaxPeriod)
							: PositionSmoothPeriod;
					}
					else if (DurMode == 2)
					{
						// duration ADDS to the distance ramp -- tests whether time carries anything depth doesn't
						smoothPer = Clamp(smoothPer + DurSlope * durFrac * travel, PositionSmoothPeriod, KamaSmoothMaxPeriod);
					}
					else if (stBearRun > 0)   // modes 3 and 4 act ONLY on ST Bear candles; all else keeps the shipped ramp
					{
						if (DurMode == 3)
						{
							double start = DurSlope >= 0 ? PositionSmoothPeriod : KamaSmoothMaxPeriod;
							smoothPer = Clamp(start + DurSlope * durFrac * travel, PositionSmoothPeriod, KamaSmoothMaxPeriod);
						}
						else
						{
							// dial the depth ramp's OWN period with streak age, preserving the depth information
							smoothPer = DurSlope >= 0
								? smoothPer + (KamaSmoothMaxPeriod - smoothPer) * durFrac * DurSlope
								: smoothPer - (smoothPer - PositionSmoothPeriod) * durFrac * (-DurSlope);
							smoothPer = Clamp(smoothPer, PositionSmoothPeriod, KamaSmoothMaxPeriod);
						}
					}
				}
				// ST-Bear period override, applied last so it wins over the depth ramp and the duration modes
				if (st.Value == ShortTermState.Bear)
				{
					sbPerSum += smoothPer; sbPerN++;    // what the shipped ramp actually asks for on these bars
					if (StBearSmoothMult != 1.0)
						smoothPer = Math.Max(PositionSmoothPeriod, smoothPer * StBearSmoothMult);
					if (StBearSmoothPeriod > 0) smoothPer = StBearSmoothPeriod;
				}
				if (smoothPer > 0) { double aP = 2.0 / (smoothPer + 1); posSmooth = double.IsNaN(posSmooth) ? position : aP * position + (1.0 - aP) * posSmooth; position = posSmooth; }
				if (DdRatioMode != 0 && DdRatioPostSmooth)
					position = Clamp(position * ddRatioMult, minExp, maxExp);
				// (Bear, Bear) ceiling -- applied LAST so nothing downstream can lift the position back over it.
				if (lt == LongTermState.Bear && st.Value == ShortTermState.Bear)
				{
					bbBars++; bbRawSum += position;
					if (BearBearMult != 1.0) position = Clamp(position * BearBearMult, minExp, maxExp);
					if (BearBearMaxExposure >= 0.0)
					{
						double bbCap = BearBearMaxExposure / 100.0;
						if (position > bbCap) { bbBound++; position = bbCap; }
					}
				}
				if (FlatHaircut != 1.0) position *= FlatHaircut;   // exposure-matched control, no signal
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
				kamaAbove.Add(!double.IsNaN(kama) && prev.Close >= kama);
				kamaDist.Add(double.IsNaN(kama) || kama <= 0 ? double.NaN : (prev.Close - kama) / kama * 100.0);
				stStates.Add(st.Value);
				ltStates.Add(lt);

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
			result.KamaAbove = kamaAbove;
			result.KamaDist = kamaDist;
			result.StState = stStates;
			result.LtState = ltStates;
			result.BearBearBars = bbBars;
			result.BearBearCapBound = bbBound;
			result.BearBearMeanRaw = bbBars > 0 ? bbRawSum / bbBars : 0.0;
			result.BearCandleBars = bcBars;
			result.StBearMeanPeriod = sbPerN > 0 ? sbPerSum / sbPerN : 0.0;
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
