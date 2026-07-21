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
		// Split the long bias across BOTH LT directions in the rolling sum: a Bull candle contributes
		// 1 + bias/2 and a Bear candle -1 + bias/2 (instead of 1+bias / -1). The bias becomes a long
		// tilt on both sides, so a high-bias (quiet+choppy) name keeps its conviction elevated THROUGH
		// its LT-Bear stretches — a cleaner "hold through chop" than relying on a decaying biasEma. It
		// trades a little bull-amplification (halved) for that hold. Broad-validated on the random 500:
		// Sharpe up in EVERY HV bucket (0.17->0.20, 63% of names), edges buy&hold on Sharpe (0.20 vs 0.19),
		// at ~flat drawdown (only the >100% HV lottery bucket runs ~4pp deeper). On by default.
		public static bool   BiasSplit     = true;
		// OUT-OF-REGION rule: a name is out of its edge regime whenever the RAW exposure signal is bearish -- i.e.
		// the EMA of the (LT,ST) target exposure (`ema`, before the bias skew) is < 0. Parameter-free (no windows).
		// 1 = go to CASH (default -- rotate capital to an in-region name); 0 = keep deploying; 2 = mirror buy&hold.
		// Chosen over an earlier trailing-persistence rule: cleaner (one condition, no tuned windows) and higher
		// out-of-sample Cash Sharpe (0.22 vs 0.11 on a broad ~1300-name universe).
		public static int    BearRegimeMode = 1;
		// RSI overbought-trim overlay on the FINAL position: posB *= min(RsiMultNumerator/RSI(period), 1) each bar --
		// trims when overbought and does NOTHING when oversold (capped at 1, never levers up). Applied after the drift
		// band and the out-of-region rule. A clamp ablation showed the ENTIRE edge is the overbought trim (the oversold
		// lever added nothing, so it's capped at 1). Two sweeps set the shape: a SHORT period (2, Connors-style) beats 7,
		// and a LOW numerator (15, see below) beats 50 -- both replicate across four disjoint random-500 OOS samples and
		// both are the same "trim harder" lever. Broad-500 OOS at 2/15: Deploy 0.68 / Cash 0.30 / Hold 0.55 vs B&H 0.50,
		// at much lower drawdown. 0 = off; default period 2 (Wilder RSI on close).
		public static int    RsiOverlayPeriod = 2;
		// Numerator N in the trim multiplier min(N/RSI, 1): trimming begins when RSI > N and the depth is N/RSI
		// (at RSI=100, exposure -> N%). N is BOTH the overbought threshold and the trim depth. A sweep found a LOW N
		// wins -- default 15 (was 50): beats 50 on Sharpe AND drawdown in every out-of-region mode, replicating across
		// four disjoint random-500 OOS samples (broad Deploy 0.59->0.68, Cash MaxDD 23->~14). It is a deeper, earlier
		// mean-reversion trim: more defensive (avg exposure/return down ~30%, drawdown down ~40%). CAVEAT: N and the RSI
		// period are the same "trim-aggressiveness" lever, and all OOS windows are the mean-reverting 2023-26 period --
		// a low N leans harder on that regime and would under-participate in a sustained momentum/trend regime.
		public static double RsiMultNumerator = 15.0;

		// Exposure-shaped numerator (IN-REGION ONLY). Instead of a fixed N, scale the trim by how
		// exposed the position already is: N = clamp((1 - ema) * RsiExposureMult, 1, 100), where ema is
		// the (lag-1) raw exposure EMA. Near-full-long (ema->1) => N->1 (fade overbought hard, since a
		// pullback hurts most when you're most exposed); lightly long (ema->0) => N->RsiExposureMult
		// (~the old fixed baseline, little to protect). This is a RISK-SIZING rule, not a trait bet:
		// trim in proportion to exposure. OUT-OF-REGION (ema<0) N stays fixed at RsiMultNumerator --
		// that trim is load-bearing in Deploy (dropping it blows Deploy MaxDD 28->38) and must NOT be
		// softened by the (1-ema)>1 blow-up. Set RsiExposureMult=0 to disable (fixed N everywhere, the
		// pre-2026-07-20 behavior -- the DEFAULT). Shaping (mult 20) does reduce the drawdown TAIL
		// (Cash p90 MaxDD 23.5->16.1) and Pareto-beats a fixed N on Sharpe, but a layer ablation on the
		// Calmar lens showed it trims return in ~proportion to drawdown (Calmar flat-to-negative: Deploy
		// 0.677->0.650, Hold 0.462->0.379) -- it makes worst-case drawdowns shallower without lifting
		// return-per-unit-risk, and it costs the Hold benchmark. So it is kept as an OPT-IN tail-DD tool,
		// OFF by default; the shipped default is the fixed RsiMultNumerator (15) trim. Set >0 to enable
		// exposure shaping (20 was the OOS-optimal mult if you do).
		public static double RsiExposureMult = 0.0;

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
			double held = double.NaN;    // deadband follower of the EMA (unclamped)
			double position = 0.0;       // clamped signed exposure actually applied
			double rsiAvgGain = 0.0, rsiAvgLoss = 0.0, rsiPrevClose = double.NaN, rsiMult = 1.0; int rsiCount = 0;

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

				double adjEma = Math.Abs(ema) * biasEma + ema;
				// Normal drift-band rebalance.
				if (double.IsNaN(held) || Math.Abs(held - adjEma) > driftBand)
					held = adjEma;
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
							double numer = RsiMultNumerator;
							if (RsiExposureMult > 0.0 && !double.IsNaN(ema) && ema >= 0.0)   // in-region: scale trim by exposure
								numer = Clamp((1.0 - ema) * RsiExposureMult, 1.0, 100.0);
							rsiMult = rsi > 1e-6 ? Math.Min(numer / rsi, 1.0) : 1.0;
						}
					}
					rsiPrevClose = prev.Close;
				}
				position = StepExposure(lt, st.Value);
				// out-of-region when the raw exposure (ema) is bearish: 1=cash, 2=hold(B&H)
				if (BearRegimeMode != 0 && ema < 0.0)
					position = BearRegimeMode == 1 ? 0.0 : 1.0;
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
