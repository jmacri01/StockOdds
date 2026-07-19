# StockOdds

**A three-level trend-following exposure engine — a screening + risk-control overlay that matches buy-&-hold on risk-adjusted return while cutting drawdown, on the right stocks at the right times.**

> Companion write-up: [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

StockOdds classifies each candle, rolls that up into a short-term and a long-term trend state, and maps the combined state to a **target market exposure** (0–100%), then scales how hard it leans long by each name's volatility and trend-persistence (the [dynamic long bias](#long-bias-per-candle-dynamic)). It sits in cash through sustained downtrends and re-enters as trends confirm — trading a slice of raw bull-market return for **materially lower drawdown at buy-&-hold-level risk-adjusted return**.

---

## Goal

Deliver **buy-&-hold-level risk-adjusted return with materially lower drawdown**, by screening the right stocks and stepping aside in the wrong regimes:

- **Match buy-&-hold on Sharpe** (and beat it in select pockets — see [What to screen for](#what-to-screen-for)), at a **better return-per-drawdown** (Calmar).
- **Cut max drawdown** by stepping aside during confirmed downtrends (roughly a fifth shallower on the volatile basket, and shallower on ~84% of a broad random universe).
- Do this **without shorting** — bearish states mean "go to cash," not "go short."

It is meant to be deployed **selectively — on certain stocks, at certain times.** A broad out-of-sample test found **market cap, not volatility, is what separates winners from losers:** apply a **market-cap floor** (**≥ $500M** recommended; **≥ $100M** the absolute minimum) and high HV is *no longer* a reason to exclude a name — a high-vol *large* cap is the single best case, a high-vol *micro* cap is garbage under any mode. Then run it **in-region** (its bull-dominant regime), with the out-of-region behaviour left to you. See [When to deploy](#when-to-deploy-it).

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
4. **clamped to `[0%, 100%]`** — so negative targets simply become **cash** (no short),
5. finally, if the name has fallen **out of its bull-dominant region**, overridden per the [out-of-region rule](#long-bias-per-candle-dynamic) — **cash** by default.

**Default parameters** (`Program.cs`): Exposure EMA `24`, Bias period `15`, Bias EMA `150`, Rebalance drift `30%`, exposure clamp `0–100%`. The **long bias is dynamic by default** — recomputed per candle from each name's volatility and exposure-persistence (see [Long bias](#long-bias-per-candle-dynamic) below). Smoothing knobs were validated as near-optimal and robust — see [Notes on tuning](#notes-on-tuning).

---

## Long bias (per-candle, dynamic)

The **long bias** controls how hard a bullish LT regime is leaned into. In the running trend sum, a Bull candle contributes `1 + bias/2` and a Bear candle `−1 + bias/2` (the [`BiasSplit`](#long-bias-per-candle-dynamic) default — the bias is a long tilt on *both* sides so conviction persists through chop; with `BiasSplit = false` it reverts to the classic `1 + bias` / `−1`). A larger bias pushes exposure up harder in uptrends.

### How the per-candle bias is computed

Rather than one fixed bias for every stock, the bias is **recomputed each candle** from a combined trait z-score, so quiet names lean long and hot names ease off — automatically, per name and over time:

```
z          = z(rolling HV) + z(rolling exposure-persistence)          // vs FIXED universe refs
raw        = DynBase · e^(−DynDecay · z)                              // saturates, never < 0
LongBias_t = EMA_smooth( clamp(raw, DynMin, DynMax) , DynSmoothPeriod )
```

- **`z` is absolute, not relative** — it uses fixed reference constants for the mean/std of HV and persistence, calibrated to the cross-sectional distribution of a ~110-name universe. So a stock's z reflects "how volatile / persistent is this name in absolute terms," not "vs its own recent history" (a chronically quiet name must read low, not drift to zero).
- **What it does:** a **quiet, steady** name (`z < 0`) gets a **large** bias — lean toward staying long, since it grinds up. A **hot** name (`z > 0`) gets a **small** bias — let the active signal do the work. `rolling HV` = annualized log-return stdev over `HvWindow`; `rolling persistence` = Kaufman efficiency ratio of the **raw `(LT,ST)` target exposure** over `PersistWindow` (1 = the state sequence trends and holds, 0 = it round-trips). It's measured on the raw target, *not* the exposure EMA, so the bias is **independent of `ExposureEmaPeriod`** — changing one smoothing knob doesn't move the other.
- **Smoothed:** the raw per-candle bias is jumpy (the persistence ratio moves fast on state transitions and the exponential is convex), so it's EMA-smoothed over `DynSmoothPeriod` to avoid whipsaw. Optionally a second, slower EMA (`DynSmoothSlow`, default **10** = off) can be added — `LongBias = min(fast, slow)` — so the lagging slow EMA caps *transient* spikes (the bias climbs only if the raw value is sustained). It's a **defensive dial**: `min(fast, slow) ≤ fast`, so it can only *trim* the bias. `DynSlowMult` multiplies the slow EMA before the `min`, so `< 1` lowers that ceiling *proportionally* — a cleaner, scale-aware cap than a fixed `DynMax`.

**Slow/fast EMA ratio (`BiasEmaRatio`, default on).** The bias is set to `(slowBiasEMA · DynSlowMult) · clamp(slowBiasEMA / fastBiasEMA, 0.25, 2.0)` — the *ceiling* scaled by a clamped slow/fast ratio. It's a *mean-reverting tilt on the bias's own level*, **monotonic** in the fast EMA: when the fast runs **above** the slow (the bias just spiked) the ratio is `< 1` and **damps** it; when the fast dips **below** (a recent pullback) the ratio is `> 1` and **lifts** it; `fast = slow` is neutral (= the plain ceiling). Using the ceiling as the base (rather than `min(fast, ceiling)`) keeps the curve monotonic — an earlier "tent" form peaked oddly at `fast = slow/2`. Unlike double-weighting the current window (which makes the level *more* reactive and hurt), this leans *against* recent bias moves and helps. Validated on a **broad random 500-name US-common-stock sample** *and against buy-&-hold*: it **closes most of the Sharpe gap to B&H** (0.13→0.17 vs 0.19) while keeping **~the entire drawdown edge** (shallower than B&H on **82%** of names, vs 83% without it), and it's best across the **50–100% HV deployment band** (at 75–100% it even edges B&H on Sharpe). The only give-up is on the extreme **>100% HV** names, where a couple pp more drawdown shows up. Set `BiasEmaRatio = false` for the plain ceiling'd bias.

**Split bias across LT directions (`BiasSplit`, default on).** In the rolling-sum that builds `biasEma`, a Bull candle contributes `1 + bias/2` and a Bear candle `−1 + bias/2` (instead of `1 + bias` / `−1`). The bias becomes a *long tilt on both sides*: it adds a constant `+bias/2` to the window regardless of how bull-heavy it is, so a high-bias (quiet + choppy) name keeps its conviction elevated *through* its LT-Bear stretches — a cleaner "hold through chop" than relying on a lingering `biasEma` from the prior bull. It trades a little bull-amplification (halved) for that hold. Validated on the **broad random 500** *and* against buy-&-hold: Sharpe up **in every HV bucket** (all-500 **0.17→0.20**, higher on 63% of names), and it **edges B&H on Sharpe overall** (0.20 vs 0.19) — which the strategy otherwise doesn't do on the broad universe — at **~flat drawdown** (only the extreme >100% HV bucket runs ~4pp deeper). Set `BiasSplit = false` for the classic long-only rolling sum.

**Screening preset (the shipped default): `DynMax = 150`, `DynSmoothSlow = 150`, `DynSlowMult = 0.5`.** The bias ceiling is a *slow* 150-bar EMA of the raw bias, scaled to half — and `DynMax` is raised to 150 so the raw bias isn't pre-clamped and the slow-EMA×mult *is* the effective ceiling. This is a deliberate **defensive tilt tuned for the high-vol names you'd screen**: it still captures the runs (per-name compounds preserved/improved — SMCI 733%→765%, IREN 945%→1123%, HOOD 500%→524%) and is more robust through a real bear (full-window incl. 2022, HV-set Sharpe **0.43→0.47** at ~flat drawdown), at the cost of some **bull-only** OOS Sharpe (0.46→0.38). Two honest caveats: (1) it does **not** dominate on the bull-only walk-forward — no `DynMax`/slow/mult combination beats the neutral baseline on *both* the bull-only OOS *and* the full window; it's a Pareto frontier (aggression ⇄ drawdown / bull ⇄ bear), a risk-appetite choice; (2) the full-window edge leans on the **single, in-sample 2022 bear**, so the bear-robustness can't be confirmed out-of-sample. It also **requires the raised `DynMax`** — at `DynMax = 15` the same slow/mult over-clamps and craters names (SMCI 733%→94%). **Revert to the neutral baseline** with `DynMax = 15`, `DynSmoothSlow = 10` (= `DynSmoothPeriod`), `DynSlowMult = 1.0`.

**Knobs** (all on `BankrollSimulator`, hand-set — *not* fitted to returns): `DynBase` (bias at `z = 0`, default **1**), `DynDecay` (default **0.6**), `DynSmoothPeriod` (default **10**), `DynSmoothSlow` (default **150**) / `DynSlowMult` (default **0.5**), **`BiasEmaRatio`** (default **on**, clamp `0.25–2.0`), `DynMin`/`DynMax` (default `[0, 150]`), `HvWindow`/`PersistWindow` (**60 / 63**), refs `HvRefMean`/`HvRefStd` (**57 / 34.6**), `PersRefMean`/`PersRefStd` (**0.072 / 0.010**), **`BiasSplit`** (default **on**), and the out-of-region rule **`BearRegimeMode`** (default **1 = cash**) / **`RegimeWindowLt`** (**50**, slow context) / **`RegimeWindowSt`** (**10**, fast trigger) — see below.

**Out-of-region rule (`BearRegimeMode`, default cash).** The strategy's measurable edge lives in the **bull-dominant region** — where the LT regime is Bull more than half of a **slow context window** (`RegimeWindowLt`, **50** bars) *and* the ST state is bullish (Bull / BullNeutral) more than half of a **short trigger window** (`RegimeWindowSt`, **10** bars). The two windows are deliberately **asymmetric**: the slow LT window sets the bear-regime *context*, the short ST window is the fast exit *trigger*. A fresh, disjoint broad-500 out-of-sample test found **LT=50 / ST=10 lifts Cash-mode OOS Sharpe (0.21 → 0.26)** over a single 50/50 window — broadly, positive in every HV bucket below 100 (the short trigger over-exits only in the thin, noisy HV > 100 tail). When a name falls out of that region (both fractions < 50%), `BearRegimeMode` decides what to do: **`1` = go to cash** (the default — flatten and *rotate the capital to another in-region name*), `2` = mirror buy-&-hold (force full exposure), or `0` = keep deploying the strategy. Cash is the default because the goal is to *squeeze gains out of a name while it's in its edge regime*, not to beat any single name end-to-end — "go to cash" means "go find another stock to trade." A caveat for reading backtests: on a **single-name** run the cash default **understates** results (it sits out the out-of-region stretches instead of redeploying the capital elsewhere), so to see a name's continuous full-history behavior set `BearRegimeMode = 0`. One thing the rule *can't* do — and neither can any other knob — is separate a recoverable pullback from a real decline **in advance**: at the decision bar those look identical in every available feature (z, HV, persistence, regime), so it stays a coarse, *trailing* region test rather than a predictive one — the in-advance call is an irreducible judgment left to you.

**Why it's the default — and its honest limits.** On a 110-name walk-forward (~5y OOS) the default `exp / 1 / 0.6` **beats a fixed `LongBias = 0.5`** (mean OOS Sharpe **0.43 vs 0.16**, return **+17% vs +10%**) *and* cuts drawdown — a real, out-of-sample improvement, so it's on by default. It also finally handles the high-vol, low-persistence names (e.g. AEHR/SMCI) that a fixed low bias whipsawed: their low persistence pulls z negative, so they get a high bias and stay long through their runs. Two caveats kept in view: (1) it does **not** beat a *high* fixed bias (~10) on raw Sharpe/return — that captures more of a bull run but gives back drawdown protection; (2) the piece that **provably generalizes OOS is the drawdown reduction**, not return outperformance vs buy-&-hold. This is a risk overlay, not an alpha engine — consistent with the strategy's stated goal.

The dynamic bias is mirrored in the Pine scripts: watch the per-candle bias change via the orange stepline (`Dyn LongBias`), the table row, and the Data Window (`DBG Dyn LongBias` / `DBG z`). The table also shows the **LT / ST persistence ratios** and the **Region** status (IN / OUT → cash) so the out-of-region cash exit is visible, and the exposure line drops to 0 when it fires.

---

## What to expect

**The honest, broad read (out-of-sample).** On a **random 500-name US-common-stock universe**, scored out-of-sample (the last 30% of each name's ~5y history) with the recommended **$500M market-cap floor** (299 names), the strategy **matches buy-&-hold on risk-adjusted return** — it is a screening + risk-control overlay, not an alpha engine:

| Out-of-region mode | OOS Sharpe | OOS Max DD |
|---|---:|---:|
| **Deploy** (run everywhere) | 0.50 | 35.9% |
| **Cash** (default) | 0.36 | **32.3%** |
| **Hold** (mirror B&H out of region) | 0.49 | 36.4% |
| *Buy & hold (reference)* | *0.50* | *deepest (always full-long)* |

The two durable contributions, both confirmed out-of-sample: **(1) screening** — a cap floor lifts every HV bucket and flips HV > 100 from −0.07 to +0.43 Sharpe (see [What to screen for](#what-to-screen-for)); and **(2) drawdown reduction** — the Cash / in-region policies cut mean OOS drawdown several points. Sub-$100M microcaps lose under *every* mode *and* under buy-&-hold, so the floor is the single most important gate.

**On a hand-picked high-vol basket it looks stronger — but that is partly in-sample.** Over each name's *full* history (which includes the 2022 bear the strategy dodges), a curated 17-name volatile basket, no per-symbol tuning (shown with `BearRegimeMode = 0` so each row is the name in isolation):

| Symbol | HV | Strat Sharpe | B&H Sharpe | Strat MaxDD | B&H MaxDD | Strat Ret/DD | B&H Ret/DD |
|---|---:|---:|---:|---:|---:|---:|---:|
| KO | 17 | **0.54** | 0.52 | **−20%** | −21% | 2.1 | **2.2** |
| ^GSPC | 17 | **0.85** | 0.72 | **−18%** | −25% | **4.3** | 2.9 |
| NVDA | 51 | **1.32** | 1.17 | **−54%** | −66% | **19.2** | 14.9 |
| MSTR | 91 | **0.60** | 0.52 | −87% | **−84%** | **1.5** | 1.1 |
| ASTS | 104 | **0.93** | 0.80 | **−75%** | −86% | **11.7** | 4.8 |
| SMR | 99 | **0.74** | 0.43 | **−79%** | −87% | **2.8** | −0.3 |
| OPEN | 109 | **0.87** | 0.32 | **−69%** | −98% | **10.3** | −0.7 |

**Basket aggregate (17 volatile names):** mean Sharpe **0.73 vs 0.58** (higher on **16/17**), mean max drawdown **−57% vs −70%**. ⚠️ This table is **full-window and hand-picked**, so it flatters the strategy — part of the Sharpe lead is the in-sample 2022 bear (which Yahoo's ~5y history can't re-confirm out-of-sample). **Treat the broad OOS table above as the honest expectation, and this basket as an illustration of per-name behaviour on names that suit it.**

### The trade-off, honestly
- **It captures the high-vol runs rather than sitting them out, but it does not beat buy-&-hold on Sharpe broadly.** On names that trend hard (ASTS, OPEN) it looks great; averaged over a random floored universe it *ties* B&H. The Sharpe outperformance is basket-selective; the parts that generalize are screening and drawdown.
- **The drawdown cut is real but modest.** Cash trims mean OOS drawdown ~6 pts (and in-region-only ~12 pts) vs running everywhere — at the cost of Sharpe and return. On a few extreme names it can still run *deeper* than B&H (MSTR: −87% vs −84%) when it holds a crashing name long. For maximum preservation, keep `BearRegimeMode` on cash or lower the bias ceiling (`DynSlowMult` / `DynMax`).

---

## What to screen for

The single biggest lever is **which stocks you point it at.** In priority order:

1. **Market-cap floor — the primary gate.** Screen at **~$500M** — the operating floor these backtests use (**~$100M** is the absolute minimum). Sub-$100M microcaps are net-negative OOS in *every* HV bucket — and buy-&-hold is equally bad there, so it is the *stocks*, not the strategy. The floor is the one filter that moves the needle: it lifts every bucket and makes even HV > 100 deployable.
2. **Do *not* screen out high volatility.** HV is not a good exclusion criterion. A high-vol **large** cap is the single best cell (HV 75–100, ≥ $500M → OOS Sharpe ~0.8); a high-vol **micro** cap is garbage. Let market cap, not HV, do the filtering.
3. **Where it actually beats buy-&-hold** (if outperformance, not just matching, is the goal): the one reliable pocket is **moderate-HV (≈25–50%) small-to-mid caps ($100M–$500M)** — ~73% of those names beat B&H on OOS Sharpe. On **large caps ($10B+) you *match* B&H** (both ~0.6–1.1 Sharpe) — deploy there for the drawdown cushion, not for outperformance.
4. **Two zones to avoid:** sub-$100M microcaps (lose outright), and **mid-caps ($500M–$10B) at HV 50–100**, where the in-region trimming actually *loses* to B&H.

**The screen, in one line:** *US common stock, market cap ≥ $500M (≥ $100M absolute minimum); chase the edge on moderate-HV small/mid caps, hold large caps for the drawdown cushion, and skip sub-$100M names entirely.*

---

## When to deploy it

Once a name passes the screen, two decisions remain: **when** to run it, and **what to do out of region.**

- **Deploy in-region.** The edge is built for a name **in its bull-dominant region** — LT & ST persistence ratios ≥ 1 over their trailing windows (LT 50 / ST 10 bars, shown live in the Pine table). Out of region is the decision point below.
- **Long-or-cash only.** Allowing shorts (`MinExposure = −100%`) was tested and made every metric *worse* — bearish signals are best expressed as cash.

### Choosing the out-of-region mode (`BearRegimeMode`)

This is a genuine **risk-appetite choice, not a fixed best.** When a name drops out of its region, `BearRegimeMode` decides what happens (OOS figures on the $500M-floored universe):

| Mode | Out-of-region action | OOS Sharpe | OOS Max DD | Choose it when… |
|---|---|---:|---:|---|
| **`1` Cash** *(default)* | flatten to 0; redeploy the capital elsewhere | 0.36 | **32.3%** | running a **rotation portfolio**, or capital preservation leads — "go to cash" means "go find another stock to trade" |
| **`2` Hold** | mirror buy-&-hold (force full long) | 0.49 | 36.4% | you have **high conviction in the specific name** and do not want the region rule to exit a position you mean to ride through the dip |
| **`0` Deploy** | keep running the strategy | 0.50 | 35.9% | you want the raw signal everywhere; behaves ≈ Hold on Sharpe |

**Cash** gives the lowest drawdown (~−4 pts) but sacrifices ~0.14 Sharpe and ~7 pts of return by sitting out. **Hold** matches B&H (and Deploy) on Sharpe while keeping you invested — the right pick when exiting is not an option. Single-name-backtest caveat: the cash default **understates** any single name (it sits out instead of redeploying), so score a name's continuous full-history behaviour with `BearRegimeMode = 0`.

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
