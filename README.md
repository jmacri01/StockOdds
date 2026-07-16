# StockOdds

**A three-level trend-following exposure engine — a risk-adjusted overlay that beats buy-&-hold on Sharpe across volatile names while cutting drawdown ~a third.**

> Companion write-up: [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

StockOdds classifies each candle, rolls that up into a short-term and a long-term trend state, and maps the combined state to a **target market exposure** (0–100%), then scales how hard it leans long by each name's volatility and trend-persistence (the [dynamic long bias](#long-bias-fixed-or-dynamic-per-candle)). It sits in cash through sustained downtrends and re-enters as trends confirm — trading a slice of raw bull-market return for a **higher Sharpe and materially lower drawdown**.

---

## Goal

Deliver **better risk-adjusted return than buy-and-hold** on volatile names, while keeping drawdowns well below simply holding:

- **Raise Sharpe** and **return-per-drawdown** (Calmar) versus buy-&-hold.
- **Cut max drawdown** by stepping aside during confirmed downtrends (roughly a third shallower, on average).
- Do this **without shorting** — bearish states mean "go to cash," not "go short."

It is most useful on **high-volatility names** where the trend timing and drawdown control both pay off. On calm, low-volatility names it tends to *underperform* buy-&-hold (there's no deep drawdown to dodge, so leaning long just tracks it at best), so deploy selectively — see [When to deploy](#when-to-deploy-it).

---

## How it works

Three stacked layers turn raw OHLC bars into a single exposure number:

### 1. Candle classification
Each bar is labeled relative to the previous bar:
- **Bull** — close above the prior bar's high
- **Bear** — close below the prior bar's low
- **Neutral** — anything in between (ignored by the state machines)

### 2. Short-term state (ST) — a 4-state machine
Tracks short runs of bull/bear candles:

| State | Enters when |
|---|---|
| **Bull** | two consecutive bull candles |
| **BullNeutral** | first bear candle interrupting a bull run |
| **BearNeutral** | first bull candle interrupting a bear run |
| **Bear** | two consecutive bear candles |

### 3. Long-term state (LT) — an anchor-based regime
A **Bull / Bear** regime driven by trailing anchors (the low/high of the 2nd-to-last candle in the current run). The regime flips to **Bull** when price closes above the bear anchor after a confirmed run, and to **Bear** when it closes below the bull anchor — a lagging, noise-resistant trend filter.

### 4. Exposure map → smoothing → position
The `(LT, ST)` pair maps to a signed target exposure (a gradient from most-bullish to most-bearish):

| | ST Bull | ST BullNeutral | ST BearNeutral | ST Bear |
|---|---|---|---|---|
| **LT Bull** | +100% | +50% | 0% | −50% |
| **LT Bear** | +50% | 0% | −50% | −100% |

That raw target is then:
1. **EMA-smoothed** (avoids whipsawing on single-bar state flips),
2. skewed by a **dynamic long-bias** (leans with the recent trend),
3. only **rebalanced when it drifts past a deadband** (cuts churn),
4. **clamped to `[0%, 100%]`** — so negative targets simply become **cash** (no short).

**Default parameters** (`Program.cs`): Exposure EMA `12`, Bias period `15`, Bias EMA `150`, Rebalance drift `30%`, exposure clamp `0–100%`. The **long bias is dynamic by default** — recomputed per candle from each name's volatility and exposure-persistence (see [Long bias](#long-bias-fixed-or-dynamic-per-candle) below); set `DynamicLongBias = false` for a single fixed `LongBias`. Smoothing knobs were validated as near-optimal and robust — see [Notes on tuning](#notes-on-tuning).

---

## Long bias: fixed or dynamic (per-candle)

The **long bias** controls how hard a bullish LT regime is leaned into: a Bull candle contributes `(LongBias + 1)` to the running trend sum, a Bear candle `−1`. A larger `LongBias` pushes exposure up harder in uptrends.

### Dynamic long bias (the default)

Rather than one `LongBias` for every stock, the bias is **recomputed each candle** from a combined trait z-score, so quiet names lean long and hot names ease off — automatically, per name and over time:

```
z          = z(rolling HV) + z(rolling exposure-persistence)          // vs FIXED universe refs
raw        = DynBase · e^(−DynDecay · z)   (Exponential, default)     // or  DynBase + DynSlope · z  (Linear)
LongBias_t = EMA_smooth( clamp(raw, DynMin, DynMax) , DynSmoothPeriod )
```

- **`z` is absolute, not relative** — it uses fixed reference constants for the mean/std of HV and persistence, calibrated to the cross-sectional distribution of a ~110-name universe. So a stock's z reflects "how volatile / persistent is this name in absolute terms," not "vs its own recent history" (a chronically quiet name must read low, not drift to zero).
- **What it does:** a **quiet, steady** name (`z < 0`) gets a **large** bias — lean toward staying long, since it grinds up. A **hot** name (`z > 0`) gets a **small** bias — let the active signal do the work. `rolling HV` = annualized log-return stdev over `HvWindow`; `rolling persistence` = Kaufman efficiency ratio of the **raw `(LT,ST)` target exposure** over `PersistWindow` (1 = the state sequence trends and holds, 0 = it round-trips). It's measured on the raw target, *not* the exposure EMA, so the bias is **independent of `ExposureEmaPeriod`** — changing one smoothing knob doesn't move the other.
- **Smoothed:** the raw per-candle bias is jumpy (the persistence ratio moves fast on state transitions and the exponential is convex), so it's EMA-smoothed over `DynSmoothPeriod` to avoid whipsaw.

**Knobs** (all on `BankrollSimulator`, hand-set — *not* fitted to returns): `DynScale` (default **Exponential**), `DynBase` (bias at `z = 0`, default **1**), `DynDecay` (default **0.6**) / `DynSlope`, `DynSmoothPeriod` (default **10**), `DynMin`/`DynMax` (default `[0, 15]`), `HvWindow`/`PersistWindow` (**60 / 63**), refs `HvRefMean`/`HvRefStd` (**57 / 34.6**), `PersRefMean`/`PersRefStd` (**0.072 / 0.010**), and **`BiasBlend`** (default **0.75**) — see below. Set `DynamicLongBias = false` to fall back to a single fixed `BankrollSimulator.LongBias`.

**Defensive ↔ dynamic blend (`BiasBlend`).** The engine computes *two* exposure skews each candle — a **defensive** one from the fixed `LongBias` (halves drawdown but lags the rockets) and the **dynamic** one above (captures the rockets, gives up some protection) — and blends them: `adjEma = |ema| · (BiasBlend·dynamic + (1−BiasBlend)·defensive) + ema`. It's a **risk dial**, not a free lunch: Sharpe and drawdown both rise smoothly with the weight. The default **0.75** keeps essentially all of pure-dynamic's Sharpe while buying back a few points of drawdown. Dial toward **0** for capital preservation, **1** for maximum capture.

**Why it's the default — and its honest limits.** On a 110-name walk-forward (~5y OOS) the default `exp / 1 / 0.6` **beats a fixed `LongBias = 0.5`** (mean OOS Sharpe **0.43 vs 0.16**, return **+17% vs +10%**) *and* cuts drawdown — a real, out-of-sample improvement, so it's on by default. It also finally handles the high-vol, low-persistence names (e.g. AEHR/SMCI) that a fixed low bias whipsawed: their low persistence pulls z negative, so they get a high bias and stay long through their runs. Two caveats kept in view: (1) it does **not** beat a *high* fixed bias (~10) on raw Sharpe/return — that captures more of a bull run but gives back drawdown protection; (2) the piece that **provably generalizes OOS is the drawdown reduction**, not return outperformance vs buy-&-hold. This is a risk overlay, not an alpha engine — consistent with the strategy's stated goal.

The dynamic bias is mirrored in the Pine scripts (on by default): watch `LongBias` change per candle via the orange stepline, the table row, and the Data Window (`DBG Dyn LongBias` / `DBG z`).

---

## What to expect

Backtested over each name's full available history (~5 years, **including the 2022 bear market**), default parameters with the **dynamic long bias on**, no per-symbol tuning:

| Symbol | HV | Strat Sharpe | B&H Sharpe | Strat MaxDD | B&H MaxDD | Strat Ret/DD | B&H Ret/DD |
|---|---:|---:|---:|---:|---:|---:|---:|
| KO | 16 | 0.18 | **0.54** | −21% | −21% | 0.4 | **2.3** |
| ^GSPC | 17 | **1.03** | 0.74 | **−17%** | −25% | **4.9** | 3.1 |
| NVDA | 51 | **1.35** | 1.18 | **−47%** | −66% | **19.7** | 15.5 |
| MSTR | 91 | **1.09** | 0.53 | **−53%** | −84% | **14.5** | 1.1 |
| ASTS | 104 | **1.07** | 0.83 | **−44%** | −86% | **31.4** | 6.0 |
| SMR | 99 | **0.76** | 0.45 | **−67%** | −87% | **3.8** | −0.2 |
| OPEN | 109 | **0.60** | 0.33 | **−62%** | −98% | **2.9** | −0.7 |

**Basket aggregate (18 symbols):** mean Sharpe **0.65 vs 0.49** (strategy higher on **14/18**), mean max drawdown **−47.3% vs −70.1%** — a **shallower drawdown on 17 of 18 names** (≈ a third less, on average).

### The trade-off, honestly
- **It now captures the high-vol runs rather than sitting them out.** The bias leans long into volatile names that trend (ASTS: Sharpe **1.07 vs 0.83**, drawdown **−44% vs −86%**, Ret/DD **31.4 vs 6.0**) — where a fixed low bias used to lag buy-&-hold, it now matches or beats it.
- **The drawdown cut is real but more modest than a pure cash-heavy config.** ≈ a third shallower vs buy-&-hold (was ~half with a pure defensive config). The default blends 75% dynamic / 25% defensive (`BiasBlend`), which recovers a few points of drawdown at no Sharpe cost. If you want maximum capital preservation instead, dial `BiasBlend` down (0 = pure defensive) or set `DynamicLongBias = false` with a low fixed `LongBias`.
- **On low-vol names it still lags** (KO: 0.38 vs 0.54): no deep drawdown to dodge, so leaning long just tracks buy-&-hold at best. Don't run it there.
- **This 18-name set is high-vol-favorable.** Across a broad ~110-name universe the strategy *ties* buy-&-hold on Sharpe (≈0.43) while still cutting drawdown — the drawdown edge generalizes; the Sharpe outperformance is strongest on volatile names.

### When to deploy it
- **Deploy on high-volatility names (roughly HV ≥ 50).** That's where the dynamic bias, trend timing, and drawdown control all pay off — the strategy beats buy-and-hold on Sharpe *and* Calmar there.
- **Skip low-volatility names.** Below ~HV 25 it tracks or lags buy-&-hold; there's no drawdown to protect against.
- **Long-or-cash only.** Allowing shorts (`MinExposure = −100%`) was tested and made every metric *worse* — bearish signals are best expressed as cash.

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
- `SYMBOL`, `START_DATE` — the single-symbol run (default output: per-state stats, bankroll ledger, strategy-vs-buy-&-hold Sharpe & drawdown).
- `RUN_GRID_SEARCH = true` + `GRID_MODE` — switch to a validation/analysis mode over a basket (`GRID_SYMBOLS`).

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
The Pine scripts in `pine/` reproduce the C# engine bar-for-bar (the strategy plots a **synthetic-equity** line that mirrors the C# `BankrollSimulator`). Defaults are kept in sync with `Program.cs`.

---

## Notes on tuning

This project was stress-tested for overfitting, and the findings shaped the defaults:

- **Parameter tuning does not survive out-of-sample.** Per-symbol grid search *lost* to a fixed global default on held-out data (overfit decay ~1.3 Sharpe). Rolling walk-forward over both the smoothing knobs and the exposure map showed no durable *alpha*.
- **The smoothing knobs are second-order.** Sharpe barely moves across a wide range of them; the current values sit in the ~92nd percentile and are treated as fixed.
- **The strategy's real, robust value is drawdown reduction** — which *is* consistent out-of-sample and across a full market cycle.
- **Don't tune to a single symbol.** Individual names vary widely around the average; that dispersion is expected noise, not a defect to be fit away.

---

## Disclaimer

This is a research backtest, not investment advice. Past performance does not guarantee future results. Backtests use adjusted daily data from Yahoo Finance and idealized fills; live results will differ. Use at your own risk.
