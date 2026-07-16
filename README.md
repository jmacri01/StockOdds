# StockOdds

**A three-level trend-following exposure engine — a defensive overlay that roughly halves drawdown on volatile names while holding risk-adjusted return.**

> Companion write-up: [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

StockOdds classifies each candle, rolls that up into a short-term and a long-term trend state, and maps the combined state to a **target market exposure** (0–100%). The result is not a return-maximizing signal — it's a **capital-preservation overlay**: it sits in cash through sustained downtrends and re-enters as trends confirm, trading some bull-market upside for materially smaller drawdowns.

---

## Goal

The strategy is **not** trying to beat buy-and-hold on raw return. Its objective is **risk reduction**:

- **Cut max drawdown** by stepping aside during confirmed downtrends.
- **Preserve risk-adjusted return** (Sharpe) and improve **return-per-drawdown** (Calmar) versus simply holding.
- Do this **without shorting** — bearish states mean "go to cash," not "go short."

It is most useful on **high-volatility, drawdown-prone names** where tail risk actually hurts. On calm, low-volatility names it tends to *underperform* (sitting in cash just forfeits steady gains), so it should be deployed selectively — see [When to deploy](#when-to-deploy-it).

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

**Default parameters** (`Program.cs`): Exposure EMA `12`, Bias period `15`, Long bias `0.5`, Bias EMA `150`, Rebalance drift `30%`, exposure clamp `0–100%`. These were validated as near-optimal and robust — see [Notes on tuning](#notes-on-tuning).

---

## Long bias: fixed or dynamic (per-candle)

The **long bias** controls how hard a bullish LT regime is leaned into: a Bull candle contributes `(LongBias + 1)` to the running trend sum, a Bear candle `−1`. A larger `LongBias` pushes exposure up harder in uptrends.

You can run it two ways:

- **Fixed** *(default)* — one `LongBias` for the whole run (`BankrollSimulator.LongBias`, default `0.5`). `DynamicLongBias = false`.
- **Dynamic** *(per-candle)* — set `BankrollSimulator.DynamicLongBias = true` and the bias is recomputed every candle from a **combined trait z-score**:

  ```
  z          = z(rolling HV) + z(rolling exposure-persistence)
  LongBias_t = clamp( DynBase + DynSlope · z      ,  DynMin, DynMax )     // Linear (default)
             = clamp( DynBase · e^(−DynDecay · z) ,  DynMin, DynMax )     // Exponential
  ```

  `z` is on an **absolute** scale (fixed reference constants), so it reflects a name's volatility/persistence in absolute terms, not relative to its own recent history. A **quiet, steady** name reads `z < 0` → a **large** bias (lean toward staying long); a **hot, high-volatility, high-persistence** name reads `z > 0` → a **small** bias (let the active signal do the work). `rolling HV` is annualized log-return stdev over `HvWindow`; `rolling persistence` is the Kaufman efficiency ratio of the pre-bias exposure EMA over `PersistWindow` (1 = the exposure trends and holds, 0 = it round-trips).

  Knobs (all on `BankrollSimulator`, hand-set — *not* fitted): `DynScale` (`Linear`/`Exponential`, default `Linear`), `DynBase` (bias at `z = 0`, default `0` — so high-vol names go to `0` and only quiet names get a bias), `DynSlope`/`DynDecay`, `DynMin`/`DynMax` (default `[0, 15]`), `HvWindow`/`PersistWindow`, and the `HvRef*` / `PersRef*` reference constants.

  > Note: on the tested basket over a bull-heavy window this did **not** beat a well-chosen *fixed* bias out-of-sample; it is provided as an option, off by default. The regime where a per-candle bias should help (dialing risk down on hot names into a drawdown) isn't covered by the available ~5-year history.

The dynamic bias is mirrored in the Pine scripts — enable **"Use dynamic long bias"** in the indicator to watch `LongBias` change per candle (the orange stepline, the table row, and the Data Window).

---

## What to expect

Backtested over each name's full available history (~5 years, **including the 2022 bear market**), fixed parameters, no per-symbol tuning:

| Symbol | HV | Strat Sharpe | B&H Sharpe | Strat MaxDD | B&H MaxDD | Strat Ret/DD | B&H Ret/DD |
|---|---:|---:|---:|---:|---:|---:|---:|
| KO | 16 | 0.09 | 0.57 | **−13%** | −21% | 0.14 | 2.48 |
| ^GSPC | 17 | 0.77 | 0.75 | **−8%** | −25% | 3.39 | 2.89 |
| NVDA | 51 | **1.31** | 1.19 | **−28%** | −66% | 13.1 | 14.2 |
| MSTR | 91 | **0.82** | 0.57 | **−33%** | −84% | 6.81 | 0.75 |
| ASTS | 104 | 0.76 | 0.84 | **−48%** | −86% | 5.85 | 5.70 |
| SMR | 99 | **0.66** | 0.47 | **−60%** | −88% | 2.57 | −0.12 |
| OPEN | 109 | **0.69** | 0.33 | **−40%** | −98% | 5.38 | −0.71 |

**Basket aggregate (18 symbols):** mean Sharpe **0.57 vs 0.49** (strategy higher on 12/18), mean max drawdown **−34.5% vs −70.1%** — the strategy had a **shallower drawdown on 18 of 18 names.**

### The trade-off, honestly
- **Expect lower raw returns in a straight bull run.** Example — ASTS full history: strategy **+279%** vs buy-&-hold **+488%**. The strategy sat out pullbacks and missed part of the run… but cut max drawdown from **−86% to −48%** and slightly *beat* buy-&-hold on return-per-drawdown.
- **The win is drawdown, and it's consistent.** Roughly half the drawdown across the board.
- **On low-vol names it lags** (KO, AAPL): no deep drawdowns to avoid, so cash just costs you return. Don't run it there.

### When to deploy it
- **Deploy on high-volatility names (roughly HV ≥ 50).** That's where drawdown protection is valuable and where the strategy matches or beats buy-and-hold on Sharpe *and* Calmar.
- **Skip low-volatility names.** The strategy's edge grows monotonically with volatility; below ~HV 25 it's only a drawdown reducer with no risk-adjusted benefit.
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
