# StockOdds

**A three-level trend-following exposure engine — a risk-adjusted overlay that beats buy-&-hold on Sharpe across volatile names while cutting drawdown ~a fifth.**

> Companion write-up: [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

StockOdds classifies each candle, rolls that up into a short-term and a long-term trend state, and maps the combined state to a **target market exposure** (0–100%), then scales how hard it leans long by each name's volatility and trend-persistence (the [dynamic long bias](#long-bias-per-candle-dynamic)). It sits in cash through sustained downtrends and re-enters as trends confirm — trading a slice of raw bull-market return for a **higher Sharpe and materially lower drawdown**.

---

## Goal

Deliver **better risk-adjusted return than buy-and-hold** on volatile names, while keeping drawdowns well below simply holding:

- **Raise Sharpe** and **return-per-drawdown** (Calmar) versus buy-&-hold.
- **Cut max drawdown** by stepping aside during confirmed downtrends (roughly a fifth shallower on the volatile basket, and shallower on ~84% of a broad random universe).
- Do this **without shorting** — bearish states mean "go to cash," not "go short."

It is meant to be deployed **selectively — on certain stocks, at certain times.** A broad out-of-sample test found **market cap, not volatility, is what separates winners from losers:** apply a **market-cap floor** (roughly **≥ $100M**, ideally **≥ $500M**) and high HV is *no longer* a reason to exclude a name — a high-vol *large* cap is the single best case, a high-vol *micro* cap is garbage under any mode. Then run it **in-region** (its bull-dominant regime), with the out-of-region behaviour left to you. See [When to deploy](#when-to-deploy-it).

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

Backtested over each name's full available history (~5 years, **including the 2022 bear market**), the **shipped default** (dynamic long bias on — z-scaled, slow/fast-modulated, and split across both LT directions; see below), no per-symbol tuning. These per-name figures are shown with `BearRegimeMode = 0` (deploy continuously end-to-end) so each row reflects the name in isolation; the shipped **cash** default instead sits out each name's out-of-region stretches to rotate capital elsewhere — see [the out-of-region rule](#long-bias-per-candle-dynamic):

| Symbol | HV | Strat Sharpe | B&H Sharpe | Strat MaxDD | B&H MaxDD | Strat Ret/DD | B&H Ret/DD |
|---|---:|---:|---:|---:|---:|---:|---:|
| KO | 17 | **0.54** | 0.52 | **−20%** | −21% | 2.1 | **2.2** |
| ^GSPC | 17 | **0.85** | 0.72 | **−18%** | −25% | **4.3** | 2.9 |
| NVDA | 51 | **1.32** | 1.17 | **−54%** | −66% | **19.2** | 14.9 |
| MSTR | 91 | **0.60** | 0.52 | −87% | **−84%** | **1.5** | 1.1 |
| ASTS | 104 | **0.93** | 0.80 | **−75%** | −86% | **11.7** | 4.8 |
| SMR | 99 | **0.74** | 0.43 | **−79%** | −87% | **2.8** | −0.3 |
| OPEN | 109 | **0.87** | 0.32 | **−69%** | −98% | **10.3** | −0.7 |

**Basket aggregate (17 volatile names):** mean Sharpe **0.73 vs 0.58** (strategy higher on **16/17**), mean max drawdown **−57.1% vs −69.5%** — a **shallower drawdown on 16 of 17 names** (≈ a fifth less, on average).

> ⚠️ **In-sample vs out-of-sample — read this.** The table above is *full-window*, so it includes the **2022 bear** the strategy dodges — part of the Sharpe edge comes from there. Out of sample the gap to buy-&-hold is now **narrow, not adverse** (it used to trail): on the high-vol deployment set, a bull-only rolling walk-forward (2023–2026 — no bear falls inside any test window) has the strategy **essentially tying buy-&-hold on Sharpe (≈0.83 vs 0.85, winning 2/4 folds)** while still cutting fold drawdown (**~42% vs 48%**); and on a **broad random 500-name US-common-stock sample** it now **edges buy-&-hold on Sharpe (0.20 vs 0.19)** while staying shallower on drawdown for ~84% of names. So the honest read: since the bias reforms this is a **capital-preservation overlay that roughly matches-or-beats buy-&-hold on risk-adjusted return** rather than trailing it — but its clearest, most *durable* edge is still **drawdown reduction**, and the full-window Sharpe lead leans partly on the single in-sample 2022 bear (which can't be re-confirmed out-of-sample — Yahoo caps history at ~5y).

### The trade-off, honestly
- **It captures the high-vol runs rather than sitting them out.** The bias leans long into volatile names that trend (ASTS: Sharpe **0.93 vs 0.80**, drawdown **−75% vs −86%**, Ret/DD **11.7 vs 4.8**; OPEN **0.87 vs 0.32**) — where a fixed low bias used to lag buy-&-hold, it now beats it.
- **The drawdown cut is real but modest — and the newer, more aggressive bias trades some of it for return.** ≈ a fifth shallower vs buy-&-hold on the basket (was ~a quarter with the earlier, tamer bias; ~half with a pure defensive config). On a few extreme names it can even run *deeper* than buy-&-hold (MSTR: −87% vs −84%) when it holds a crashing name long. For maximum capital preservation instead, leave `BearRegimeMode` on cash so it sits out the out-of-region stretches, or lower the bias ceiling (`DynSlowMult` / `DynMax`).
- **On low-vol names it roughly tracks buy-&-hold** (KO: 0.54 vs 0.52 — a near-tie now, not a clear lag): no deep drawdown to dodge, so leaning long just matches it at best. Across the broad universe the calm 0–25% HV bucket still trails slightly (≈0.38 vs 0.44). Don't expect an edge there.
- **On a broad universe it matches buy-&-hold; the edge is risk control and screening, not raw Sharpe.** A random-500 out-of-sample test (last 30% of ~5y) with a **$100M market-cap floor** puts Deploy/Hold OOS Sharpe at **~0.46, ≈ buy-&-hold (0.46)** across every HV bucket — including HV > 100 once microcaps are screened out. The durable contributions are (1) **screening** — a cap floor lifts every bucket and turns HV > 100 from −0.07 to +0.43 Sharpe — and (2) **drawdown reduction** — the cash / in-region policies cut mean OOS drawdown ~6–12 pts. Sub-$100M microcaps are net-negative under *any* mode *and* under buy-&-hold, so the cap floor is the single most important deployment gate.

### When to deploy it

Three gates decide the outcome far more than any parameter: **which stocks, which regime, which out-of-region mode.**

- **Screen by market cap first — it beats HV as a filter.** A broad random-500 out-of-sample test (last 30% of each name's ~5y history) found **market cap, not volatility, separates winners from losers.** Below **~$100M** the OOS Sharpe is negative in *every* HV bucket — and buy-&-hold is just as bad there, so it's the *stocks*, not the strategy. Apply a **cap floor: ~$100M minimum, ~$500M for a cleaner book.** Do **not** exclude a name for high HV alone: a high-vol *large* cap is the single best cell (HV 75–100, ≥ $500M → OOS Sharpe ~0.8), a high-vol *micro* cap is garbage. With the floor even the **HV > 100 bucket is net-positive** (Deploy ~0.43, vs −0.07 unfloored).
- **Deploy in-region.** The edge is built for a name **in its bull-dominant region** — LT & ST persistence ratios ≥ 1 over their trailing windows (LT 50 / ST 10 bars, shown live in the Pine table). Out of region is the decision point below.
- **Calm large caps just track buy-&-hold.** Below ~HV 25 there's no drawdown to dodge, so it matches B&H — fine to hold, but no edge to harvest.
- **Long-or-cash only.** Allowing shorts (`MinExposure = −100%`) was tested and made every metric *worse* — bearish signals are best expressed as cash.

**The out-of-region mode is your call** (`BearRegimeMode`) — a risk-appetite choice, not a fixed best:
- **Cash (`1`, default)** — flatten out of region and *rotate the capital to another in-region name*. **Lowest drawdown** (~6 pts under the other modes on the floored universe), at the cost of ~0.18 Sharpe and ~8 pts of return. Best as a **rotation-portfolio** engine or when capital preservation leads.
- **Hold / mirror buy-&-hold (`2`)** — stay fully invested out of region. **Best Sharpe of the three** (ties-or-edges B&H, ~0.46–0.50 floored) and keeps you in the name. Choose it when you have **high conviction in the specific name and don't want the region rule to bounce you out of a position you mean to ride through the dip.**
- **Deploy (`0`)** — keep running the strategy out of region; behaves ≈ Hold on Sharpe.

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
