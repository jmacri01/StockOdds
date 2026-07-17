# StockOdds

**A three-level trend-following exposure engine — a risk-adjusted overlay that beats buy-&-hold on Sharpe across volatile names while cutting drawdown ~a quarter.**

> Companion write-up: [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

StockOdds classifies each candle, rolls that up into a short-term and a long-term trend state, and maps the combined state to a **target market exposure** (0–100%), then scales how hard it leans long by each name's volatility and trend-persistence (the [dynamic long bias](#long-bias-fixed-or-dynamic-per-candle)). It sits in cash through sustained downtrends and re-enters as trends confirm — trading a slice of raw bull-market return for a **higher Sharpe and materially lower drawdown**.

---

## Goal

Deliver **better risk-adjusted return than buy-and-hold** on volatile names, while keeping drawdowns well below simply holding:

- **Raise Sharpe** and **return-per-drawdown** (Calmar) versus buy-&-hold.
- **Cut max drawdown** by stepping aside during confirmed downtrends (roughly a quarter shallower, on average).
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
- **Smoothed:** the raw per-candle bias is jumpy (the persistence ratio moves fast on state transitions and the exponential is convex), so it's EMA-smoothed over `DynSmoothPeriod` to avoid whipsaw. Optionally a second, slower EMA (`DynSmoothSlow`, default **10** = off) can be added — `LongBias = min(fast, slow)` — so the lagging slow EMA caps *transient* spikes (the bias climbs only if the raw value is sustained). It's a **defensive dial**: `min(fast, slow) ≤ fast`, so it can only *trim* the bias. `DynSlowMult` multiplies the slow EMA before the `min`, so `< 1` lowers that ceiling *proportionally* — a cleaner, scale-aware cap than a fixed `DynMax`.

**Screening preset (the shipped default): `DynMax = 150`, `DynSmoothSlow = 150`, `DynSlowMult = 0.5`.** The bias ceiling is a *slow* 150-bar EMA of the raw bias, scaled to half — and `DynMax` is raised to 150 so the raw bias isn't pre-clamped and the slow-EMA×mult *is* the effective ceiling. This is a deliberate **defensive tilt tuned for the high-vol names you'd screen**: it still captures the runs (per-name compounds preserved/improved — SMCI 733%→765%, IREN 945%→1123%, HOOD 500%→524%) and is more robust through a real bear (full-window incl. 2022, HV-set Sharpe **0.43→0.47** at ~flat drawdown), at the cost of some **bull-only** OOS Sharpe (0.46→0.38). Two honest caveats: (1) it does **not** dominate on the bull-only walk-forward — no `DynMax`/slow/mult combination beats the neutral baseline on *both* the bull-only OOS *and* the full window; it's a Pareto frontier (aggression ⇄ drawdown / bull ⇄ bear), a risk-appetite choice; (2) the full-window edge leans on the **single, in-sample 2022 bear**, so the bear-robustness can't be confirmed out-of-sample. It also **requires the raised `DynMax`** — at `DynMax = 15` the same slow/mult over-clamps and craters names (SMCI 733%→94%). **Revert to the neutral baseline** with `DynMax = 15`, `DynSmoothSlow = 10` (= `DynSmoothPeriod`), `DynSlowMult = 1.0`.

**Knobs** (all on `BankrollSimulator`, hand-set — *not* fitted to returns): `DynScale` (default **Exponential**), `DynBase` (bias at `z = 0`, default **1**), `DynDecay` (default **0.6**) / `DynSlope`, `DynSmoothPeriod` (default **10**), `DynSmoothSlow` (default **150**) / `DynSlowMult` (default **0.5**), `DynMin`/`DynMax` (default `[0, 150]`), `HvWindow`/`PersistWindow` (**60 / 63**), refs `HvRefMean`/`HvRefStd` (**57 / 34.6**), `PersRefMean`/`PersRefStd` (**0.072 / 0.010**), **`BiasBlend`** (default **1.0** = pure dynamic), **`DefensiveBias`** (default **0.5**), **`BiasTiming`** (default **off**, band `0.75–1.25`) and **`BiasNoInvert`** (default **off**) — see below. Set `DynamicLongBias = false` to fall back to a single fixed `BankrollSimulator.LongBias`.

**Defensive ↔ dynamic blend (`BiasBlend`).** The engine computes *two* exposure skews each candle — a **defensive** one from `DefensiveBias` (default 0.5 — halves drawdown but lags the rockets) and the **dynamic** one above (captures the rockets, gives up some protection) — and blends them: `adjEma = |ema| · (BiasBlend·dynamic + (1−BiasBlend)·defensive) + ema`. It's a **risk dial**, not a free lunch: Sharpe and drawdown both rise smoothly with the weight (a frontier interpolation — no dominating middle). The default is **1.0 (pure dynamic)**; dial **down toward 0** to trade Sharpe for capital preservation. (Resist the urge to push it low to erase one name's worst drawdown — that's overfitting a tail; the dynamic default already turns a −95% buy-&-hold into ~−30%.) The blend only applies with dynamic on; `LongBias` is purely the dynamic-*off* fallback and has **no effect** while `DynamicLongBias = true`.

**No-invert guard (`BiasNoInvert`, default off).** The skew is `adjEma = |ema|·biasEma + ema`. For a *bullish* `ema` that's `ema·(1+biasEma)` — amplify the long. For a *bearish* `ema` it's `ema·(1−biasEma)`, which is fine while `biasEma ≤ 1` (trims the short toward flat) but **inverts** once `biasEma > 1` — and the dynamic bias routinely reaches ~3, so a bearish signal gets flipped into a *long that grows as the signal gets more bearish* (e.g. a name in a confirmed LT-Bear downtrend can sit pinned at 100% long the whole way down). `BiasNoInvert = true` caps this: the bias may pull a bearish `ema` to **flat (0)** but never past zero into a net long (bullish bars are untouched, so rocket-capture is unchanged). When the guard fires it also **bypasses the drift band** and snaps the position flat — a decisive exit rather than a within-band residual (`held` goes to 0, so re-entry stays symmetric and every other bar's rebalance is unchanged). It does **not** dominate on the broad universe (it's a Sharpe-for-drawdown trade there, 0.40→0.32 OOS), but on the **high-vol deployment set it's near-free** (0.42 vs 0.43 Sharpe, ~4 pts less drawdown) and it removes a genuine tail hazard — leveraging *into* a crash — that a bull-only walk-forward can't punish. Left off by default; worth turning on if you deploy on volatile names.

**Timing modulation (`BiasTiming`, default off).** *Level for direction, window for timing.* The level-amplified skew above decides **how bullish** (a high `biasEma` rides the runs); `BiasTiming` then nudges it by the **latest 15-bar window vs its own long-run norm**: `adjEma ×= clamp(dynBias / biasEma, 0.75, 1.25)`. When the recent window runs hot vs its norm (accelerating) exposure ticks up; when it cools (decelerating) it ticks down. The band is deliberately **tight** — on a real trender the skew already clamps to max, so the modulation is moot there; it only trims/adds in the *marginal* cases. (Widen it and it slides into the failure mode of a pure ratio model, which puts `biasEma` in the denominator and de-levers exactly the persistent winners.) **Its value flips with the bias-cap regime, which is why it's now default-off:** at the **neutral config** (`DynMax = 15`, `DynSlowMult = 1.0`, uncapped bias) it *adds* on hot windows and lifts the big-winner compounds at unchanged drawdown (NVDA 928%→981%, SMCI 526%→733%, AVGO 473%→527%) — a genuine win, so **turn it on there**. But at the **shipped screening default** (`DynMax = 150`, `DynSmoothSlow = 150`, `DynSlowMult = 0.5`) the slow-EMA ceiling *already* hard-caps the bias, so timing only trims an already-capped skew and mildly de-levers the winners (NVDA/OPEN/ASST/MSTR give back some compound return at ~flat Sharpe/drawdown). So it ships **off** under the capped default. Guarded off when `biasEma ≤ 0` (bearish regime — the level de-risks there already). Set `BiasTiming = true` to re-enable it (recommended only with the uncapped neutral config).

**Which knob when — `BiasBlend` vs `BiasNoInvert`.** They are *not* interchangeable. `BiasBlend` is the **global risk dial**: a smooth, monotonic control (OOS the whole frontier is clean — e.g. HV names 0.43/−33% at 1.0 → 0.34/−26% at 0.25) that trades Sharpe for drawdown across the *whole* book — dial it for your overall risk appetite. `BiasNoInvert` is a **surgical, binary per-name override**: it removes *only* the pathological leverage-into-a-crash (a bearish signal flipped long by `biasEma > 1`) while leaving genuine upside untouched — so on a name you've judged is truly rolling over it can protect the downside *without* sacrificing the run if you're wrong (it took one such name to a *higher* return than the default, not lower). Use `BiasBlend` to set book-wide defensiveness; reach for `BiasNoInvert` only as a targeted switch on a specific name. Note the one thing *neither* can do: separate a recoverable pullback from a real decline *in advance* — at the decision bar those look identical in every available feature (z, HV, persistence, regime), so it's an irreducible judgment call, which is exactly why the surgical override is left to you rather than automated.

**Why it's the default — and its honest limits.** On a 110-name walk-forward (~5y OOS) the default `exp / 1 / 0.6` **beats a fixed `LongBias = 0.5`** (mean OOS Sharpe **0.43 vs 0.16**, return **+17% vs +10%**) *and* cuts drawdown — a real, out-of-sample improvement, so it's on by default. It also finally handles the high-vol, low-persistence names (e.g. AEHR/SMCI) that a fixed low bias whipsawed: their low persistence pulls z negative, so they get a high bias and stay long through their runs. Two caveats kept in view: (1) it does **not** beat a *high* fixed bias (~10) on raw Sharpe/return — that captures more of a bull run but gives back drawdown protection; (2) the piece that **provably generalizes OOS is the drawdown reduction**, not return outperformance vs buy-&-hold. This is a risk overlay, not an alpha engine — consistent with the strategy's stated goal.

The dynamic bias is mirrored in the Pine scripts (on by default): watch `LongBias` change per candle via the orange stepline, the table row, and the Data Window (`DBG Dyn LongBias` / `DBG z`).

---

## What to expect

Backtested over each name's full available history (~5 years, **including the 2022 bear market**), the **shipped default** (the high-vol screening preset — dynamic long bias on, bias ceiling = half the slow 150-bar EMA; see below), no per-symbol tuning:

| Symbol | HV | Strat Sharpe | B&H Sharpe | Strat MaxDD | B&H MaxDD | Strat Ret/DD | B&H Ret/DD |
|---|---:|---:|---:|---:|---:|---:|---:|
| KO | 16 | 0.45 | **0.60** | −21% | −21% | 1.5 | **2.5** |
| ^GSPC | 17 | **0.88** | 0.76 | **−21%** | −25% | **3.8** | 3.0 |
| NVDA | 51 | **1.34** | 1.19 | **−53%** | −66% | **20.0** | 15.1 |
| MSTR | 91 | **0.90** | 0.59 | **−70%** | −84% | **7.4** | 1.1 |
| ASTS | 104 | **0.89** | 0.81 | **−58%** | −86% | **12.0** | 4.8 |
| SMR | 99 | **0.76** | 0.43 | **−64%** | −87% | **3.9** | −0.3 |
| OPEN | 109 | **0.68** | 0.33 | **−68%** | −98% | **4.0** | −0.7 |

**Basket aggregate (18 symbols):** mean Sharpe **0.64 vs 0.48** (strategy higher on **14/18**), mean max drawdown **−51.6% vs −70.4%** — a **shallower drawdown on 17 of 18 names** (≈ a quarter less, on average).

> ⚠️ **In-sample vs out-of-sample — read this.** The table above is *full-window*, so it includes the **2022 bear**, which the strategy dodges — that's where its Sharpe edge comes from. A stricter **rolling walk-forward** (train on each window, score the held-out next ~9 months — all of which land in the 2023–2026 bull) tells a different story: on the high-vol deployment set, **buy-&-hold *beats* the strategy on Sharpe out-of-sample (≈0.91 vs 0.43, 0/4 folds)**, because no bear falls inside an OOS test window for the de-risking to pay off (Yahoo caps history at ~5y). **What *does* survive OOS is the drawdown reduction — ~a third shallower (32% vs 47%), every fold.** So the honest read: this is a **capital-preservation overlay whose Sharpe advantage is bear-market-dependent**, not a reliable standalone alpha. Deploy it for drawdown control, not to out-Sharpe buy-&-hold in a bull run.

### The trade-off, honestly
- **It captures the high-vol runs rather than sitting them out.** The bias leans long into volatile names that trend (ASTS: Sharpe **0.89 vs 0.81**, drawdown **−58% vs −86%**, Ret/DD **12.0 vs 4.8**) — where a fixed low bias used to lag buy-&-hold, it now matches or beats it.
- **The drawdown cut is real but more modest than a pure cash-heavy config.** ≈ a quarter shallower vs buy-&-hold (was ~half with a pure defensive config). Running more long buys return at the cost of some protection — a deliberate trade. If you want maximum capital preservation instead, dial `BiasBlend` down toward 0, or set `DynamicLongBias = false` with a low fixed `LongBias`.
- **On low-vol names it still lags** (KO: 0.45 vs 0.60): no deep drawdown to dodge, so leaning long just tracks buy-&-hold at best. Don't run it there.
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
