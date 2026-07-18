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
		// The skew adjEma = |ema|*biasEma + ema. When biasEma > 1 (which the dynamic bias
		// allows — it can reach ~3+), a NEGATIVE (bearish) ema is inverted into a growing LONG
		// (ema*(1-biasEma) flips sign and scales with how bearish the signal is). With
		// BiasNoInvert = true the skew may pull a bearish ema toward flat but never past it into
		// a net long (and symmetrically for a bullish ema) — it can't fight its own sign. When it
		// fires it also BYPASSES the drift band to snap the position flat (a decisive exit, not a
		// within-band residual) — held goes to 0, so re-entry stays symmetric and every other
		// bar's drift-band rebalance is unchanged.
		public static bool   BiasNoInvert  = false;
		// Level for direction, window for timing. The level-amplified skew (|ema|*biasEma + ema)
		// decides HOW bullish (biasEma high -> more bullish -> rides the runs). It is then modulated
		// by the latest window vs its long-run norm: m = clamp(dynBias/biasEma, Min, Max). >1 when the
		// recent window runs hot (accelerating) nudges exposure up; <1 (decelerating) nudges it down.
		// The band is kept TIGHT (0.75..1.25) so it only trims/adds at the margin and never de-levers
		// a real trend (whose skew clamps to max regardless). Guarded off when biasEma<=~0 (bearish
		// regime — the level already de-risks there).
		// DEFAULT OFF (2026-07-17): timing's sign flips with the bias-cap regime. At the NEUTRAL config
		// (DynMax=15, mult=1.0, uncapped bias) it's a win — it ADDS on hot windows and lifts the big-winner
		// compounds (SMCI 526->733, NVDA 928->981) at equal drawdown. But at the SHIPPED screening default
		// (DynMax=150, slow150, mult0.5) the ceiling already hard-caps the bias, so timing only TRIMS an
		// already-capped skew and mildly de-levers the winners (NVDA/OPEN/ASST/MSTR worse on both return and
		// Sharpe; aggregate Sharpe/DD ~flat). So it's default-OFF for the capped default — turn it ON if you
		// revert to the neutral (uncapped) config, where it earns its keep.
		public static bool   BiasTiming    = false;
		public static double BiasTimingMin = 0.75;
		public static double BiasTimingMax = 1.25;

		// ============ Dynamic (per-candle) LongBias ============
		// Leave DynamicLongBias = false to use the fixed LongBias above. Set it true and the
		// LongBias is instead recomputed every candle from a combined trait z-score:
		//   z          = (rollingHV - HvRefMean)/HvRefStd + (rollingPersist - PersRefMean)/PersRefStd
		//   LongBias_t = clamp( Exponential : DynBase * exp(-DynDecay * z)
		//                       Linear      : DynBase + DynSlope * z ,  DynMin, DynMax )
		// z is on an ABSOLUTE scale via FIXED reference constants, so a quiet/steady name reads
		// z<0 -> a large LongBias (leans toward staying long) and a hot high-HV, high-persistence
		// name reads z>0 -> a small LongBias (lets the active signal run). rollingHV = annualized
		// stdev of log returns over HvWindow (same convention as Volatility.AnnualizedHistoricalPct);
		// rollingPersist = Kaufman efficiency ratio of the RAW (LT,ST) target exposure over
		// PersistWindow (1 = the state sequence trends and holds, 0 = it round-trips) — measured
		// on the raw target, not the EMA, so it is independent of ExposureEmaPeriod. Finally the per-candle bias is
		// EMA-smoothed over DynSmoothPeriod so it can't whipsaw (the raw value jumps because the
		// persistence ratio is jumpy and the exp map is convex). All knobs are hand-set, not fit.
		public enum DynScaleMode { Linear, Exponential }
		public static bool         DynamicLongBias = true;
		public static DynScaleMode DynScale        = DynScaleMode.Exponential;
		public static int    HvWindow        = 60;
		public static int    PersistWindow   = 63;
		public static double HvRefMean       = 57.0;
		public static double HvRefStd        = 34.6;
		public static double PersRefMean     = 0.072;
		public static double PersRefStd      = 0.010;
		public static double DynBase         = 1.0;    // LongBias at z = 0
		public static double DynDecay        = 0.6;    // exponential mode: decay rate
		public static double DynSlope        = -1.28;  // linear mode: per unit z
		public static double DynMin          = 0.0;
		// ===== High-vol screening default (see README "screening preset") =====
		// The bias cap is a SLOW EMA (150) of the raw bias, scaled to half: effLongBias =
		// MAX(MIN(fast EMA, slow EMA * DynSlowMult), DynMin). DynMax is raised to 150 so the raw
		// bias isn't pre-clamped and the slow-EMA*mult IS the effective, scale-aware ceiling.
		// This is a deliberate DEFENSIVE tilt for volatile names: it captures the runs (per-name
		// compounds preserved/improved) and is more robust through a real bear (full-window incl.
		// 2022: HV Sharpe 0.43->0.47, drawdown flat) at the cost of some bull-ONLY OOS Sharpe
		// (0.46->0.38). It does NOT dominate on the bull-only walk-forward — it's a risk-appetite
		// choice, not a free win, and the full-window edge leans on the one in-sample 2022 bear.
		// NOTE: this default REQUIRES DynMax raised — at DynMax=15 the same slow/mult over-clamps
		// and craters names (SMCI 733%->94%). To revert to the neutral baseline: DynMax=15,
		// DynSmoothSlow=DynSmoothPeriod (10), DynSlowMult=1.0.
		public static double DynMax          = 150.0; // raised so the slow-EMA*mult is the real ceiling
		public static int    DynSmoothPeriod = 10;     // fast EMA smoothing of the per-candle bias (1 = off)
		public static int    DynSmoothSlow   = 150;    // slow EMA; MIN(fast, slow*mult) caps transient spikes
		public static double DynSlowMult     = 0.5;    // scales the slow-EMA ceiling (proportional bias cap)
		// Scale the bias by longEMA/shortEMA = slowBiasEMA / fastBiasEMA (clamped), on a fixed CEILING base:
		//   effLongBias = (slow * DynSlowMult) * clamp(slow/fast, lo, hi).
		// This is monotonic-decreasing in the fast EMA — lift the bias as the fast dips below the slow (a
		// recent pullback in the bias) and damp it as the fast rises above (a recent spike); neutral (= the
		// plain ceiling) when fast == slow. Using the ceiling (not MIN(fast, ceiling)) as the base removes the
		// non-monotonic "tent" (which peaked at fast = slow/2 and fell back for very low fast). Validated on a
		// broad random 500-name US-common-stock sample AND vs buy-&-hold: closes most of the Sharpe gap to B&H
		// (0.13->0.17 vs 0.19), keeps ~the entire drawdown edge (shallower than B&H on 82% of names), and is
		// best across the 50-100% HV deployment band; the give-up vs the old tent is only on >100% HV lottery
		// names. On by default. Set false for the plain ceiling'd bias.
		public static bool   BiasEmaRatio    = true;
		// Split the long bias across BOTH LT directions in the rolling sum: a Bull candle contributes
		// 1 + bias/2 and a Bear candle -1 + bias/2 (instead of 1+bias / -1). The bias becomes a long
		// tilt on both sides, so a high-bias (quiet+choppy) name keeps its conviction elevated THROUGH
		// its LT-Bear stretches — a cleaner "hold through chop" than relying on a decaying biasEma. It
		// trades a little bull-amplification (halved) for that hold. Broad-validated on the random 500:
		// Sharpe up in EVERY HV bucket (0.17->0.20, 63% of names), edges buy&hold on Sharpe (0.20 vs 0.19),
		// at ~flat drawdown (only the >100% HV lottery bucket runs ~4pp deeper). On by default.
		public static bool   BiasSplit     = true;
		public static double BiasEmaRatioLo  = 0.25;   // clamp floor on slow/fast (max damp)
		public static double BiasEmaRatioHi  = 2.0;    // clamp ceiling on slow/fast (max lift)
		// Blend of two exposure skews: the DEFENSIVE leg (fixed LongBias — halves drawdown but
		// lags the rockets) and the DYNAMIC leg (trait-scaled effLongBias — captures the rockets
		// but gives up some protection). adjEma = |ema| * (BiasBlend*biasEmaDyn +
		// (1-BiasBlend)*biasEmaFixed) + ema. 1 = pure dynamic, 0 = pure defensive.
		public static double BiasBlend       = 1.0;
		// BlendSlope: modulate the defensive/dynamic blend by the GRADE of the plotted-position momentum. Track a fast
		// (EMA12) and slow (EMA-BlendSlowPeriod) EMA of the FINAL position; when the fast EMA is rolling over (falling
		// over BlendSlopeBars) AND above the slow (a confirmed late-run top, the empirically low-forward-return zone),
		// pull the blend toward defensive proportional to the descent's steepness: effBlend = clamp(1 + K*grade, Floor, 1)
		// where grade = fastEMA_t - fastEMA_{t-BlendSlopeBars} (< 0). Uses lagged position EMAs (non-circular). Gentle:
		// mild rollovers barely change the blend; only steep tops de-risk. OOS-neutral broadly, trims drawdown and
		// preserves/adds return on volatile/trending names (its benefit concentrates there; a no-op on quiet names).
		public static bool   BlendSlope      = true;
		public static int    BlendSlowPeriod = 120;   // slow position-EMA period (fast is fixed EMA12)
		public static int    BlendSlopeBars  = 10;    // lookback for the fast-EMA slope grade
		public static double BlendSlopeK     = 6.0;    // sensitivity: blend reduction per unit of negative grade
		public static double BlendSlopeFloor = 0.0;    // minimum blend the modulation can reach
		public static bool   BlendSlopeGate  = true;   // require fastEMA > slowEMA (only de-risk confirmed tops)
		// FinalSmooth: EMA-smooth the FINAL plotted position (the last output), on top of the drift band. The position
		// was previously only smoothed indirectly (the drift band gives discrete hysteresis) — a light EMA on the output
		// damps residual whipsaw/turnover. Broad random-500 validated: raises OOS Sharpe (0.27->0.28, the only change to
		// improve broad OOS rather than tie) and trims ~1pt drawdown, giving up only marginal return (whipsaw churn, not
		// trend). Short period only — heavy smoothing over-lags and craters return (EMA20 gave it all back).
		public static bool   FinalSmooth       = true;
		public static int    FinalSmoothPeriod = 3;   // paired with ExposureEmaPeriod 24: longer raw EMA wants a lighter final smooth
		// The defensive leg's fixed bias (independent of LongBias, which is only the
		// dynamic-OFF fallback — so LongBias has no effect while DynamicLongBias is on).
		public static double DefensiveBias   = 0.5;

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
			var stratReturns = new List<double>();
			var bhReturns = new List<double>();
			var returnDates = new List<DateTime>();

			double alpha = 2.0 / (ExposureEmaPeriod + 1);
			double biasAlpha = 2.0 / (BiasEmaPeriod + 1);
			double driftBand = RebalanceDriftPercent / 100.0;
			double minExp = MinExposurePercent / 100.0;
			double maxExp = MaxExposurePercent / 100.0;

			double ema = double.NaN;     // EMA of the per-candle target exposure
			double posFast = double.NaN, posSlow = double.NaN;   // fast/slow EMAs of the FINAL plotted position
			double finalEma = double.NaN; double aFS = 2.0 / (FinalSmoothPeriod + 1);
			double aPF = 2.0 / (12 + 1), aPS = 2.0 / (BlendSlowPeriod + 1);
			var posFastBuf = new List<double>();
			double held = double.NaN;    // deadband follower of the EMA (unclamped)
			double position = 0.0;       // clamped signed exposure actually applied

			// rolling LT-direction window for the dynamic long bias
			var biasWindow = new Queue<double>(BiasPeriod);
			double biasSum = 0.0;
			double biasEma = double.NaN;   // EMA of the dynamic-leg bias
			// defensive leg: same machinery but on the fixed LongBias (for the BiasBlend)
			var biasWindowFix = new Queue<double>(BiasPeriod);
			double biasSumFix = 0.0;
			double biasEmaFix = double.NaN;

			// per-candle dynamic-LongBias state: rolling HV (log-return sample stdev, annualized)
			// as of the decision bar, plus rolling exposure-EMA persistence over PersistWindow.
			var hvRetWindow = new Queue<double>(Math.Max(1, HvWindow));
			double hvSum = 0.0, hvSqSum = 0.0;
			double curHvPct = HvRefMean;              // rolling annualized HV %, refreshed each bar
			var perTgtWindow = new Queue<double>(Math.Max(1, PersistWindow) + 1);
			var perAbsWindow = new Queue<double>(Math.Max(1, PersistWindow));
			double perAbsSum = 0.0, perTgtPrev = double.NaN;
			double dynLbAlpha = 2.0 / (Math.Max(1, DynSmoothPeriod) + 1);
			double dynLbAlphaSlow = 2.0 / (Math.Max(1, DynSmoothSlow) + 1);
			double dynLbEma = double.NaN;             // fast EMA of the per-candle LongBias
			double dynLbEmaSlow = double.NaN;         // slow EMA — MIN(fast, slow) caps transient spikes

			// updates curHvPct from the completed return into `latest` (no look-ahead)
			void UpdateHv(OhlcBar prevBar, OhlcBar latest)
			{
				if (!DynamicLongBias || prevBar.Close <= 0 || latest.Close <= 0) return;
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

				// per-candle LongBias: the fixed LongBias, or the trait-scaled dynamic value
				double effLongBias = LongBias;
				if (DynamicLongBias)
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
					double raw = DynScale == DynScaleMode.Exponential
						? DynBase * Math.Exp(-DynDecay * z)
						: DynBase + DynSlope * z;
					raw = Clamp(raw, DynMin, DynMax);
					// EMA-smooth the per-candle bias so it can't whipsaw bar-to-bar. A fast and a slow
					// EMA; effLongBias = MIN of the two, so the lagging slow EMA caps transient spikes
					// (bias climbs only if sustained) while MIN still lets it drop fast.
					dynLbEma = double.IsNaN(dynLbEma) ? raw : dynLbAlpha * raw + (1.0 - dynLbAlpha) * dynLbEma;
					dynLbEmaSlow = double.IsNaN(dynLbEmaSlow) ? raw : dynLbAlphaSlow * raw + (1.0 - dynLbAlphaSlow) * dynLbEmaSlow;
					if (BiasEmaRatio)
					{
						// monotonic: ceiling base scaled by the clamped slow/fast ratio (see comment above)
						double m = Clamp(dynLbEmaSlow / Math.Max(dynLbEma, 1e-6), BiasEmaRatioLo, BiasEmaRatioHi);
						effLongBias = Math.Max(dynLbEmaSlow * DynSlowMult * m, DynMin);
					}
					else
					{
						effLongBias = Math.Max(Math.Min(dynLbEma, dynLbEmaSlow * DynSlowMult), DynMin);
					}
				}

				// dynamic long bias: rolling LT-direction sum over BiasPeriod candles /
				// BiasPeriod, then EMA-smoothed. A Bull candle contributes (effLongBias + 1), a Bear candle -1.
				// Matches the Pine math.sum window.
				double sig = BiasSplit
					? (lt == LongTermState.Bull ? 1.0 + effLongBias / 2.0 : lt == LongTermState.Bear ? -1.0 + effLongBias / 2.0 : 0.0)
					: (lt == LongTermState.Bull ? effLongBias + 1.0 : lt == LongTermState.Bear ? -1.0 : 0.0);
				biasWindow.Enqueue(sig);
				biasSum += sig;
				while (biasWindow.Count > BiasPeriod)
					biasSum -= biasWindow.Dequeue();
				double dynBias = biasSum / BiasPeriod;
				biasEma = double.IsNaN(biasEma) ? dynBias : biasAlpha * dynBias + (1.0 - biasAlpha) * biasEma;

				// DEFENSIVE leg: same accumulation on the fixed LongBias
				double sigFix = lt == LongTermState.Bull ? DefensiveBias + 1.0 : lt == LongTermState.Bear ? -1.0 : 0.0;
				biasWindowFix.Enqueue(sigFix);
				biasSumFix += sigFix;
				while (biasWindowFix.Count > BiasPeriod)
					biasSumFix -= biasWindowFix.Dequeue();
				double dynBiasFix = biasSumFix / BiasPeriod;
				biasEmaFix = double.IsNaN(biasEmaFix) ? dynBiasFix : biasAlpha * dynBiasFix + (1.0 - biasAlpha) * biasEmaFix;

				// blend the dynamic and defensive skews (BiasBlend: 1=dynamic, 0=defensive)
				double effBlend = BiasBlend;
				if (BlendSlope && posFastBuf.Count > BlendSlopeBars && !double.IsNaN(posSlow))
				{
					double gPrev = posFastBuf[posFastBuf.Count - 1 - BlendSlopeBars];
					double grade = posFast - gPrev;
					if (grade < 0 && (!BlendSlopeGate || posFast > posSlow))
						effBlend = Clamp(1.0 + BlendSlopeK * grade, BlendSlopeFloor, 1.0);
				}
				double blendedBiasEma = DynamicLongBias ? effBlend * biasEma + (1.0 - effBlend) * biasEmaFix : biasEma;
				double adjEma = Math.Abs(ema) * blendedBiasEma + ema;
				// Window-vs-norm timing modulation (see BiasTiming above): trims/adds at the margin.
				if (BiasTiming && biasEma > 0.05)
					adjEma *= Clamp(dynBias / biasEma, BiasTimingMin, BiasTimingMax);
				// A long bias may neutralize a bearish EMA toward flat but not INVERT its sign
				// into a net long (guards against biasEma > 1 flipping a bearish signal to long).
				bool noInvertExit = false;
				if (BiasNoInvert)
				{
					if (ema < 0 && adjEma > 0) { adjEma = 0; noInvertExit = true; }
					else if (ema > 0 && adjEma < 0) { adjEma = 0; noInvertExit = true; }
				}
				// Normal drift-band rebalance — but when no-invert fires, bypass the band and snap
				// held to the target (0) so a within-band residual can't keep us leaning the wrong
				// way. held goes to 0 (not negative), so re-entry stays symmetric and every other
				// bar's rebalance behaviour is unchanged.
				if (double.IsNaN(held) || Math.Abs(held - adjEma) > driftBand || noInvertExit)
					held = adjEma;
				double posB = Clamp(held, minExp, maxExp);
				// Track fast/slow EMAs of the FINAL position for BlendSlope (read one bar lagged above, so non-circular).
				posFast = double.IsNaN(posFast) ? posB : aPF * posB + (1.0 - aPF) * posFast;
				posSlow = double.IsNaN(posSlow) ? posB : aPS * posB + (1.0 - aPS) * posSlow;
				posFastBuf.Add(posFast); if (posFastBuf.Count > BlendSlopeBars + 2) posFastBuf.RemoveAt(0);
				if (FinalSmooth)
				{
					finalEma = double.IsNaN(finalEma) ? posB : aFS * posB + (1.0 - aFS) * finalEma;
					return finalEma;
				}
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
				position = StepExposure(lt, st.Value);
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
