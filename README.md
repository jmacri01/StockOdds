# StockOdds

**A risk-adjustment overlay for equity exposure.** It reads each stock's trend, sizes a 0–100% long position, and — the part that matters — **steps aside when the trend breaks** and trims hard into overbought strength. The result: **drawdowns cut by roughly a third to a half, while risk-adjusted return holds up or edges buy-&-hold.** It's deliberately defensive — you give up some raw upside to take far less pain. Three selectable risk modes let you dial exactly how defensive.

> Companion write-up (the origin of the trend model): [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

This is **not an alpha engine** and doesn't pretend to be. It's a drawdown-control overlay: on a random 500-stock universe it **edges buy-&-hold on risk-adjusted return while roughly halving the drawdown**, and on the stocks that hurt you most — the ones that are falling, or ripping higher with gut-wrenching pullbacks — it takes **far less pain to get there.** No shorting: a bearish signal means *cash*, never short.

---

## What to expect

The proof is out-of-sample. Every table below is scored on the **last 30% of each name's ~5-year history** (data the parameters never saw), on a **random 500-name US-common-stock universe** with the recommended **≥ $500M market-cap floor** applied. Drawdowns are shown as positive magnitudes (smaller = better).

### The whole universe (296 names)

| Mode | OOS Sharpe | OOS Max DD | OOS Return |
|---|---:|---:|---:|
| **Deploy** | 0.70 | 19.4% | +23% |
| **Cash** *(default)* | 0.32 | **10.6%** | +6% |
| **Hold** | 0.55 | 28.1% | +24% |
| *Buy & hold* | *0.49* | *34.8%* | *+34%* |

Deploy and Hold **edge buy-&-hold on Sharpe** (0.70 / 0.55 vs 0.49) while running *far* shallower drawdowns. The default **Cash** mode is the risk dial turned to max: it trades return for protection, and its drawdown is **shallower than buy-&-hold on 99% of all names.** This is a deliberately defensive engine — the RSI-2 overbought trim (numerator 15, tightened to 8 on quiet-volume bull bars) trims exposure hard into strength, cutting drawdown roughly a third versus buy-&-hold at the cost of some upside. The real value shows up in the two cohorts that matter most.

### When the stock is falling (98 names with a negative buy-&-hold return)

This is what a risk overlay is *for.* These names lost money over the test window — and the system barely participates in the loss:

| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Cash** *(default)* | **−3%** | **13.0%** | −0.19 |
| **Deploy** | 0% | 24.9% | 0.08 |
| **Hold** | −10% | 38.2% | −0.08 |
| *Buy & hold* | *−23%* | *48.1%* | *−0.24* |

Buy-&-hold loses **−23% with a −48% drawdown.** The default Cash mode ends **near-flat (−3%) at just a −13% drawdown** — shallower than buy-&-hold on **all 98** names — and even **Deploy stays flat (≈0%)** on these losers, at a −25% drawdown. The overlay sidesteps most of the decline instead of riding it down. *(Sharpe is unstable when returns hug zero — read the Return and Max-DD columns here; they are the story.)*

### When the stock rips — but violently (27 names, +return but ≥ 50% buy-&-hold drawdown)

The high-flyers. The system gives up a chunk of the upside but takes a *much* smaller beating:

| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Deploy** | +75% | 33.5% | 1.02 |
| **Hold** | +74% | 47.3% | 0.84 |
| **Cash** *(default)* | +29% | **17.3%** | 0.70 |
| *Buy & hold* | *+145%* | *56.2%* | *0.99* |

Buy-&-hold makes **+145% but suffers a −56% drawdown.** Deploy captures **+75% at −34%**; Cash keeps **+29% at just −17%** — shallower drawdown than buy-&-hold on **all 27** names. The aggressive RSI-2 trim (numerator 15, tightened to 8 on quiet-volume bull bars) leaves real upside on the table here — it trims into every pop — so on relentless rockets Deploy trails B&H on return while roughly matching its Sharpe at far lower drawdown.

### The three modes

When a name's own signal turns bearish (its raw exposure drops below zero — "out of region"), `BearRegimeMode` decides what happens:

| Mode | Out-of-region action | Character | Choose it when… |
|---|---|---|---|
| **`1` Cash** *(default)* | flatten to 0% | **maximum drawdown protection** | you preserve capital and **rotate it to another in-region name** — "go to cash" means "go find another stock to trade" |
| **`2` Hold** | force full long (mirror B&H) | ride through the dip | you have **conviction in the specific name** and don't want the rule to exit a position you mean to hold |
| **`0` Deploy** | keep running the strategy | signal everywhere | you want the raw signal applied continuously; behaves ≈ Hold |

The single-name backtest **understates Cash** — it sits in cash instead of redeploying to another opportunity, which a real portfolio would. To judge one name's continuous behaviour end-to-end, score it with `BearRegimeMode = 0` (Deploy).

### On a hand-picked high-vol basket

A curated 18-name basket, **no per-symbol tuning**, over each name's *full* history. This is **partly in-sample** (it includes the 2022 bear the strategy dodges), so treat the broad OOS tables above as the honest expectation — this just shows per-name texture. Drawdown, default **Cash** mode vs doing nothing:

| Symbol | HV | Cash Max DD | B&H Max DD | Cash Return | B&H Return |
|---|---:|---:|---:|---:|---:|
| ^GSPC | 17 | **7%** | 25% | +12% | +71% |
| KO | 16 | **6%** | 21% | +7% | +51% |
| NVDA | 51 | **24%** | 66% | +34% | +904% |
| COIN | 85 | **34%** | 91% | +10% | −28% |
| MSTR | 90 | **24%** | 84% | +79% | +73% |
| ASTS | 104 | **36%** | 86% | +73% | +376% |
| SMR | 99 | **33%** | 88% | +15% | −15% |
| OPEN | 109 | **40%** | 98% | +30% | −74% |

Cash cuts the drawdown on **every** name, and the aggressive RSI-2 trim (numerator 15, tightened to 8 on quiet-volume bull bars) cuts it *hard* on the volatile ones — MSTR 24% vs B&H 84%, ASTS 36% vs 86%, NVDA 24% vs 66%. The flip side is visible too: it caps the rockets (NVDA +34% vs B&H +904%, ASTS +73% vs +376%), so on this in-sample high-flyer basket the aggressive trim **lowers** Sharpe even as it slashes drawdown. **Basket aggregate (all 18):** mean Sharpe **Deploy 0.40 / Cash 0.39 / Hold 0.32 vs B&H 0.47**; mean Max DD **Deploy 48% / Cash 26% / Hold 62% / B&H 70%.** This is the honest cost of the defensive tilt — it trims winners as well as losers; the broad OOS tables above (where it's Sharpe-neutral-to-positive) are the fair expectation.

### The trade-off, honestly

- **It is a risk overlay, not alpha.** The edge is drawdown reduction, not return — averaged over a random floored universe it gives up raw return (Deploy +23% vs B&H +34%) to roughly halve the drawdown, and comes out *ahead* on risk-adjusted return (Deploy/Hold Sharpe 0.70/0.55 vs B&H 0.49). The parts that **generalize out-of-sample are drawdown reduction and screening**; don't expect return outperformance.
- **The drawdown cut is the durable, provable edge — and it's large.** The aggressive RSI-2 trim pulls Deploy drawdown to ~19% (vs B&H ~35%) and the default **Cash** to ~11%, far below buy-&-hold, cutting deepest on the volatile names. The cost is participation: on relentless one-way winners the overlay trims into every pop and trails B&H on return. Pick the mode that matches your risk appetite — Cash for maximum protection, Deploy/Hold to stay more invested.
- **A regime caveat.** The overlay is a short-horizon mean-reversion trim tuned (period 2, numerator 15) on the 2023–26 window, which was mean-reverting. It cuts drawdown robustly, but in a sustained momentum/trending regime the aggressive trim would under-participate more than these numbers suggest.

---

## What to screen for

The single biggest lever is **which stocks you point it at.** In priority order:

1. **Market-cap floor — the primary gate.** Screen at **~$500M** (the floor these backtests use; **~$100M** is the absolute minimum). Sub-$100M microcaps are net-negative OOS in *every* HV bucket — and buy-&-hold is equally bad there, so it's the *stocks*, not the strategy. The floor lifts every bucket and makes even HV > 100 deployable.
2. **Do *not* screen out high volatility.** HV is a poor exclusion criterion. A high-vol **large** cap is the single best case (HV 75–100, ≥ $500M → OOS Sharpe ~0.8); a high-vol **micro** cap is garbage. Let market cap, not HV, filter.
3. **Where it actually *beats* buy-&-hold** (if outperformance is the goal): the one reliable pocket is **moderate-HV (≈25–50%) small-to-mid caps ($100M–$500M)** — ~73% of those beat B&H on OOS Sharpe. On **large caps ($10B+) you *match* B&H** — deploy there for the drawdown cushion, not for outperformance.
4. **Two zones to avoid:** sub-$100M microcaps (lose outright), and **mid-caps ($500M–$10B) at HV 50–100**, where in-region trimming loses to B&H.

**In one line:** *US common stock, market cap ≥ $500M (≥ $100M absolute minimum); chase the edge on moderate-HV small/mid caps, hold large caps for the drawdown cushion, skip sub-$100M names entirely.*

---

## When to deploy it

Once a name passes the screen, two decisions remain: **when** to run it, and **which mode** to use out of region.

- **Deploy in-region.** The edge is built for a name **in its bull-dominant region** — LT & ST persistence ratios ≥ 1 over their trailing windows (LT 50 / ST 10 bars, shown live in the Pine table).
- **Pick your mode** from the [three-modes table](#the-three-modes) above — a genuine risk-appetite choice, not a fixed best. Cash for preservation/rotation, Hold for single-name conviction, Deploy for continuous signal.
- **Long-or-cash only.** Allowing shorts (`MinExposure = −100%`) was tested and made every metric *worse* — bearish signals are best expressed as cash.

---

## How it works

Raw OHLC bars become a single exposure number through a stack of layers. The first three build the **trend signal**; the rest are the **risk overlay**.

### 1. Candle classification
Each bar is labeled relative to the previous bar:
- **Bull** — close above the prior bar's high
- **Bear** — close below the prior bar's low
- **Neutral** — anything in between (ignored by the state machines)

### 2. Short-term state (ST) — a 4-state machine
| State | Enters when |
|---|---|
| **Bull** | two consecutive bull candles |
| **BullNeutral** | first bear candle interrupting a bull run |
| **BearNeutral** | first bull candle interrupting a bear run |
| **Bear** | two consecutive bear candles |

### 3. Long-term state (LT) — an anchor-based regime
A **Bull / Bear** regime driven by trailing anchors (the low/high of the 2nd-to-last candle in the current run). It flips to **Bull** when price closes above the bear anchor after a confirmed run, and to **Bear** when it closes below the bull anchor — a lagging, noise-resistant trend filter.

### 4. Exposure map
The `(LT, ST)` pair maps to a signed target exposure (a gradient from most-bullish to most-bearish):

| | ST Bull | ST BullNeutral | ST BearNeutral | ST Bear |
|---|---|---|---|---|
| **LT Bull** | +100% | +50% | 0% | −50% |
| **LT Bear** | +50% | 0% | −50% | −100% |

### 5. From target to position (the overlay)
That raw target is then:
1. **EMA-smoothed** (avoids whipsawing on single-bar state flips),
2. skewed by a **[dynamic long-bias](#the-dynamic-long-bias)** (leans harder with the recent trend, scaled per name),
3. **rebalanced only when it drifts past a deadband** (cuts churn),
4. **clamped to `[0%, 100%]`** — negative targets simply become **cash** (no short),
5. scaled by an **RSI overbought-trim overlay** (position × min(N / RSI(2), 1) — trims exposure when overbought, never levers. A short **RSI-2** (Connors-style) and a low numerator **N = 15** are both best, each validated across four disjoint random-500 samples: together they cut drawdown ~a third and lift Sharpe out-of-sample. The numerator **tightens to N = 8 on *quiet-volume bull bars*** — when the long-term regime is Bull *and* the bar's volume is below its trailing-15-bar average (`relVol < 1`) — trimming harder into low-participation strength. Volume is the one *non-price* signal that survived four-sample replication (relative volume has a U-shaped tie to forward risk; both extremes precede worse outcomes), and this quiet-bull rule was the only conditioning that beat fixed N on **both** Sharpe and drawdown across all four samples. An ablation showed the trim is the entire edge — the oversold-lever half added nothing — so the overlay only de-risks. It's a deliberately defensive, mean-reversion tilt),
6. and finally, if the **raw exposure signal turns bearish** (out of region), overridden per the chosen **[mode](#the-three-modes)** — cash by default.

**Default parameters** (`Program.cs`): Exposure EMA `5`, Bias period `15`, Bias EMA `150`, Rebalance drift `30%`, exposure clamp `0–100%`, RSI overlay period `2` / numerator `15` (tightened to `8` on quiet-volume bull bars — `RsiQuietVolWindow = 15`, `RsiQuietBullN = 8`). The long bias is dynamic by default. Smoothing knobs were validated as near-optimal and robust — see [Notes on tuning](#notes-on-tuning).

---

## The dynamic long-bias

The **long bias** controls how hard a bullish LT regime is leaned into. In the running trend sum, a Bull candle contributes `1 + bias/2` and a Bear candle `−1 + bias/2` (the `BiasSplit` default — a long tilt on *both* sides so conviction persists through chop; with `BiasSplit = false` it reverts to `1 + bias` / `−1`). A larger bias pushes exposure up harder in uptrends.

### How the per-candle bias is computed

Rather than one fixed bias for every stock, it's **recomputed each candle** from a combined trait z-score, so quiet names lean long and hot names ease off — automatically, per name and over time:

```
z          = z(rolling HV) + z(rolling exposure-persistence)          // vs FIXED universe refs
raw        = DynBase · e^(−DynDecay · z)                              // saturates, never < 0
LongBias_t = EMA_smooth( clamp(raw, DynMin, DynMax) , DynSmoothPeriod )
```

- **`z` is absolute, not relative** — fixed reference constants for the mean/std of HV and persistence (calibrated to a ~110-name universe). So z reflects "how volatile / persistent is this name in absolute terms," not "vs its own recent history."
- **What it does:** a **quiet, steady** name (`z < 0`) gets a **large** bias — lean toward staying long, since it grinds up. A **hot** name (`z > 0`) gets a **small** bias — let the active signal do the work. `rolling HV` = annualized log-return stdev over `HvWindow`; `rolling persistence` = Kaufman efficiency ratio of the **raw `(LT,ST)` target exposure** over `PersistWindow` (measured on the raw target, so the bias is **independent of `ExposureEmaPeriod`**).
- **Smoothed:** the raw per-candle bias is jumpy (the persistence ratio moves fast and the exponential is convex), so it's EMA-smoothed over `DynSmoothPeriod` — that is the whole smoothing: `effLongBias = max(EMA(raw), DynMin)`.

**No slow/fast ratio machinery (removed).** An earlier version scaled the bias by a slow/fast-EMA *ratio* riding on a slow-EMA *ceiling* (`BiasEmaRatio`, `DynSmoothSlow`, `DynSlowMult`, plus clamps). An OOS test across four disjoint random-500 samples showed the **plain fast-EMA bias matched or slightly beat it in every mode**, so the whole apparatus (~4 knobs) was dropped for parsimony — it wasn't earning its weight. `DynMax` (150) now just caps the raw bias before smoothing and rarely binds; the defensive posture comes from the RSI-2 trim, not a bias ceiling.

**Split bias across LT directions (`BiasSplit`, default on).** A Bull candle contributes `1 + bias/2` and a Bear candle `−1 + bias/2`, so a high-bias (quiet + choppy) name keeps conviction elevated *through* its LT-Bear stretches — a cleaner "hold through chop." Validated on the broad 500: Sharpe up in every HV bucket (0.17→0.20), edges B&H (0.20 vs 0.19) at ~flat drawdown. Set `false` for the classic long-only rolling sum.



**Knobs** (all on `BankrollSimulator`, hand-set — *not* fitted to returns): `DynBase` (**1**), `DynDecay` (**0.6**), `DynSmoothPeriod` (**10**), `DynMin`/`DynMax` (`[0, 150]`), `HvWindow`/`PersistWindow` (**60 / 63**), refs `HvRefMean`/`HvRefStd` (**57 / 34.6**), `PersRefMean`/`PersRefStd` (**0.072 / 0.010**), `BiasSplit` (**on**), the out-of-region rule `BearRegimeMode` (**1 = cash**), `RsiOverlayPeriod` (**2**, 0 = off), and `RsiMultNumerator` (**15** — the trim threshold/depth in `min(N/RSI,1)`; lower = more defensive).

**Out-of-region rule (`BearRegimeMode`).** A name is out of region **whenever its raw exposure signal is bearish** — the EMA of the (LT, ST) target (before the bias skew) is < 0. One condition, no windows to tune. `BearRegimeMode` then picks the [mode](#the-three-modes). This replaced an earlier trailing-persistence rule (two tuned windows): raw < 0 is cleaner *and* scores a higher OOS Cash Sharpe (0.22 vs 0.11 on a broad ~1,300-name universe). It's a **reactive** signal — it can't tell a recoverable pullback from a real decline in advance.

The dynamic bias is mirrored in the Pine scripts: the per-candle bias (orange `Dyn LongBias` stepline), the table row, and the Data Window (`DBG Dyn LongBias` / `DBG z`). The table also shows the LT / ST persistence ratios and the **Region** status (IN / OUT → cash), and the exposure line drops to 0 when the cash exit fires.

---

## Repository layout

```
StockOdds/                  C# console backtester (.NET)
├─ Program.cs               Config (symbol, dates, parameters) + entry point
├─ LongTermStateEngine.cs   Anchor-based LT regime machine
├─ CandleStateEngine.cs     4-state ST machine + candle classification
├─ BankrollSimulator.cs     Exposure model, bankroll sim, Sharpe / drawdown metrics
├─ Volatility.cs            Annualized historical volatility
├─ YahooClient.cs           OHLC data fetch
├─ GridSearch.cs            Validation harness (see modes below)
└─ GridSearchPrinter.cs     Console reports for each mode

pine/                       TradingView Pine v6 ports — engine-identical to the C#
├─ ExposureEngine_Indicator.pine
└─ ExposureEngine_Strategy.pine
```

## Running it

```bash
dotnet run --project StockOdds
```

Configure in `Program.cs`:
- `SYMBOL`, `START_DATE` — the single-symbol run (per-state stats, bankroll ledger, strategy-vs-buy-&-hold Sharpe & drawdown).
- `RUN_GRID_SEARCH = true` + `GRID_MODE` — a validation/analysis mode over a basket (`GRID_SYMBOLS`).

### Analysis modes (`GRID_MODE`)
| Mode | What it answers |
|---|---|
| `FullWindow` | Strategy vs buy-&-hold per symbol (Sharpe / drawdown / Calmar) |
| `VolDeploy` | Short-side A/B + volatility-threshold deployment sweep |
| `KnobRank` | Where the current parameters rank in the full grid |
| `BiasSweep` | 2-D sweep of Bias period × Bias EMA |
| `Rolling` / `RollingBuckets` | Rolling walk-forward over smoothing knobs / bucket weights |
| `WalkForward` | Single train/test split: per-symbol tuned vs global default |
| `VolStudy` | Per-symbol optimal knobs vs volatility (correlation) |

### TradingView
The Pine scripts in `pine/` reproduce the C# engine bar-for-bar (the strategy plots a **synthetic-equity** line mirroring the C# `BankrollSimulator`). Defaults are kept in sync with `Program.cs`.

---

## Notes on tuning

Stress-tested for overfitting, and the findings shaped the defaults:

- **Parameter tuning does not survive out-of-sample.** Per-symbol grid search *lost* to a fixed global default on held-out data (overfit decay ~1.3 Sharpe). Rolling walk-forward showed no durable *alpha*.
- **The smoothing knobs are second-order.** Sharpe barely moves across a wide range; the current values sit in the ~92nd percentile and are treated as fixed.
- **The real, robust value is drawdown reduction** — consistent out-of-sample and across a full market cycle.
- **Don't tune to a single symbol.** Individual names vary widely around the average; that dispersion is expected noise, not a defect to fit away.

---

## Disclaimer

This is a research backtest, not investment advice. Past performance does not guarantee future results. Backtests use adjusted daily data from Yahoo Finance and idealized fills; live results will differ. Use at your own risk.
