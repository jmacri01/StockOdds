# StockOdds

**A risk-adjustment overlay for equity exposure.** It reads each stock's trend, sizes a **0–150%** long position (leaning up to **1.5×** into the strongest signals), and — the part that matters — **steps aside to cash when the trend breaks** and trims overbought strength harder in quiet regimes than volatile ones (an **HV-conditioned trim**). The result: it **keeps most of buy-&-hold's upside while carrying a smaller drawdown in every mode.** Three selectable risk modes set how defensive: **Deploy / Hold** stay invested and essentially *dominate* buy-&-hold (higher return, better Sharpe, lower drawdown); the default **Cash** mode is the most defensive, trading some return for the shallowest drawdown of the three. Participation-tilted, not maximally defensive.

> Companion write-up (the origin of the trend model): [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

This is **not an alpha engine** and doesn't pretend to be. It's an exposure-control overlay driven by a light, deliberately simple trim (an HV-conditioned overbought trim plus final-position smoothing): across 2,289 US names **Deploy/Hold match buy-&-hold's return (+36% vs +37%) at a better Sharpe (0.57 / 0.53 vs 0.46) and a lower drawdown** (33% / 35% vs 39%), and the default **Cash** mode trades return for the shallowest drawdown of the three (**21% vs 39%**). On the stocks that hurt most — falling, or ripping higher with gut-wrenching pullbacks — it takes far less pain than buy-&-hold. No shorting: a bearish signal means *cash*, never short.

---

## What to expect

The proof is out-of-sample. Every table below is scored on the **last 30% of each name's ~5-year history** (data the parameters never saw), on the **full US-common-stock universe above the recommended ≥ $500M market-cap floor** — 2,429 eligible tickers, 2,289 with enough history, split into four disjoint samples so every finding below is checked for replication across all four. **Every cell is a MEAN across names, not a median** — the mean is what an equal-weight book actually earns, since a portfolio's return is the average of its holdings'. Two consequences to keep in mind: means are pulled up hard by the fat right tail (on the broad universe buy-&-hold's mean return is +37% against a +14% median, because a handful of names ran several hundred percent), and **a mean of per-name max-drawdowns is not a portfolio drawdown** — a real diversified book would draw down far less than the average name does. Read the drawdown columns as "what the average holding put you through", not "what the book did". Drawdowns are shown as positive magnitudes (smaller = better). The **basket table further down covers each name's full ~5-year history** instead, and a full-history version of the three cohort tables is folded in below them.
>
> **Span convention:** the strategy can't trade until its state machine has warmed up (2-12 bars), so buy-&-hold is measured over the **identical bar span** as the strategy rather than from the very first bar — an apples-to-apples comparison. The console app's own `BuyHoldReturnPct` starts one bar in, so for names with a longer warmup its buy-&-hold figure differs slightly from these tables (materially only on the wildest names: GRPN −23% vs −1%, BE +871% vs +797%).

> **Regenerated 2026-07-31.** Shipped config: extension cap, **KAMA-distance smoothing**, and the flat long-bias bear. The **drawdown-recovery scaler is now DEFAULT OFF** (`DdRatioMode = 0`) — it was shipped on 2026-07-30 and disabled a day later when a live chart showed it halving exposure in clean uptrends. Its `dd60/dd30` ratio is **depth-blind**: dd30 equals dd60 whenever the 60-bar peak falls inside the last 30 bars, which is the normal state in *any* uptrend, so a 10% pullback was cut exactly as hard as a 40% collapse (worked example and measurements in [step 8](#5-from-target-to-position-the-overlay)). Every table below is on the shipped, scaler-off config, and reports **means across names** — see the note above on why, and on why a mean of per-name drawdowns is not a portfolio drawdown.

### The whole universe (2289 names)
| Mode | OOS Sharpe | OOS Max DD | OOS Return |
|---|---:|---:|---:|
| **Deploy** | 0.57 | 33.2% | +36% |
| **Cash** *(default)* | 0.36 | **21.2%** | +18% |
| **Hold** | 0.53 | 34.7% | +36% |
| *Buy & hold* | *0.46* | *38.9%* | *+37%* |

### When the stock is falling (834 names)
| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Deploy** | −16% | 40.9% | -0.12 |
| **Cash** *(default)* | −8% | **25.2%** | -0.27 |
| **Hold** | −19% | 43.0% | -0.17 |
| *Buy & hold* | *−26%* | *48.4%* | *-0.27* |

### When the stock rips — but violently (208 names)
| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Deploy** | +128% | 57.7% | 0.90 |
| **Cash** *(default)* | +86% | **38.8%** | 0.85 |
| **Hold** | +140% | 57.5% | 0.92 |
| *Buy & hold* | *+143%* | *60.1%* | *0.93* |

<details>
<summary><b>The same three cohorts over each name's full ~5-year history</b> (partly in-sample — includes the 2022 bear the strategy dodges)</summary>

The tables above are the honest out-of-sample proof. These cover the **whole window** for reference — every name's full history, cohorts re-derived on full-history buy-&-hold (so the counts differ). The 2022 bear is in here, which is why buy-&-hold's drawdowns are far deeper and the engine's edge looks larger.

| Mode | Sharpe | Max DD | Return |
|---|---:|---:|---:|
| **Cash** *(default)* | 0.21 | **34.2%** | +32% |
| **Hold** | 0.37 | 52.1% | +73% |
| *Buy & hold* | *0.33* | *58.6%* | *+71%* |

*Whole universe, 2,289 names.*

| Falling (906 names) | Return | Max DD | Sharpe |
|---|---:|---:|---:|
| **Cash** *(default)* | −1% | **42.7%** | -0.05 |
| **Hold** | −26% | 64.7% | 0.03 |
| *Buy & hold* | *−43%* | *71.6%* | *-0.04* |

| Violent (595 names) | Return | Max DD | Sharpe |
|---|---:|---:|---:|
| **Cash** *(default)* | +89% | **40.8%** | 0.43 |
| **Hold** | +180% | 60.5% | 0.56 |
| *Buy & hold* | *+182%* | *66.6%* | *0.55* |

</details>

### The three modes

When a name's own signal turns bearish (its raw exposure drops below zero — "out of region"), `BearRegimeMode` decides what happens:

| Mode | Out-of-region action | Character | Choose it when… |
|---|---|---|---|
| **`1` Cash** *(default)* | flatten to 0% | **maximum drawdown protection** | you preserve capital and **rotate it to another in-region name** — "go to cash" means "go find another stock to trade" |
| **`2` Hold** | force full long (mirror B&H) | ride through the dip | you have **conviction in the specific name** and don't want the rule to exit a position you mean to hold |
| **`0` Deploy** | keep running the strategy | signal everywhere | you want the raw signal applied continuously; behaves ≈ Hold |

The single-name backtest **understates Cash** — it sits in cash instead of redeploying to another opportunity, which a real portfolio would. To judge one name's continuous behaviour end-to-end, score it with `BearRegimeMode = 0` (Deploy).

### On a hand-picked high-vol basket
| Symbol | HV | Cash Max DD | B&H Max DD | Cash Return | B&H Return |
|---|---:|---:|---:|---:|---:|
| KO | 17 | **5%** | 21% | +6% | +58% |
| ^GSPC | 17 | **6%** | 25% | +26% | +68% |
| AAPL | 28 | **12%** | 33% | +38% | +124% |
| MSFT | 28 | **17%** | 38% | +39% | +56% |
| NOK | 38 | **29%** | 53% | +60% | +49% |
| NVDA | 51 | **38%** | 66% | +296% | +862% |
| AMD | 56 | **39%** | 65% | +202% | +331% |
| TSLA | 60 | **28%** | 74% | +69% | +39% |
| ATAI | 85 | **58%** | 94% | +47% | −51% |
| COIN | 85 | **66%** | 91% | −6% | −36% |
| BE | 86 | **48%** | 76% | +1110% | +797% |
| FIG | 89 | **33%** | 81% | −18% | −70% |
| MSTR | 90 | **52%** | 84% | +273% | +46% |
| GRPN | 90 | **49%** | 90% | +73% | −1% |
| SMR | 99 | **49%** | 87% | +390% | −15% |
| ASTS | 104 | **46%** | 86% | +694% | +457% |
| OPEN | 109 | **70%** | 98% | +103% | −74% |
| IREN | 116 | **51%** | 95% | +1050% | +82% |
| ASST | 199 | **87%** | 97% | +126% | −95% |

Cash cuts the drawdown on **all 19 names** — often by more than half — at a **mean drawdown of 41% against buy-&-hold's 71%**, while **out-returning it on mean return (+241% vs +138%)**. The showcases are the names buy-&-hold ruins (ASST, OPEN, SMR, MSTR all far ahead); the bill comes due on clean, relentless trends, where every layer of trimming costs participation. This is the one place the engine beats buy-&-hold on *return* as well as drawdown, and it is also the **most in-sample** part of the study (survivor-heavy, hand-picked, and it includes the 2022 bear the strategy dodges) — the broad tables above are the honest expectation.

### The trade-off, honestly

- **It is a risk overlay, not alpha.** Deploy matches buy-&-hold's return (+36% vs +37%) at a lower drawdown (33% vs 39%) and a clearly better Sharpe (0.57 vs 0.46); the default Cash mode trades much more return for the shallowest drawdown of all (21%). **On means there is no meaningful return outperformance** — what survives is the drawdown reduction and the screening. The parts that **generalize out-of-sample are drawdown reduction and screening** — real return outperformance is modest and should not be relied on.
- **The drawdown cut is the durable edge.** The **HV-conditioned trim** (with the extension cap and KAMA-distance smoothing) cuts drawdown in *every* mode (Deploy 33% / Cash 21% / Hold 35% vs B&H 39%). Cash is the low-drawdown dial — 21%, and shallower than buy-&-hold on **2,245 of 2,289 names (98%)**; Deploy/Hold are the return/Sharpe dial. Raising the numerator cap lightens the trim; lowering it (or `RsiOverlayPeriod`) tightens it.
- **A regime caveat.** The overbought trim is a short-horizon mean-reversion tool tuned on the 2023–26 (mean-reverting) window. The HV-conditioning concentrates it on the low-vol candles (where the mean-reversion is real) and caps it on the volatile ones, so it leans on that regime less than a hard global trim would — the main drivers of returns are still the core trend signal, the 1.5× lean, and cash-out-of-region.

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
3. **rebalanced only when it drifts past a deadband** (cuts churn) — except that when the target saturates the exposure ceiling the position snaps to that ceiling (150% by default) rather than lagging low, so the sized exposure stays accurate at the top,
4. **clamped to `[0%, 150%]`** (default; the strong-signal candles lever to 1.5×, ceiling 200%) — negative targets simply become **cash** (no short),
5. scaled by an **HV-conditioned RSI overbought-trim overlay** (position × min(N_eff / RSI(2), 1) — trims exposure when overbought, never levers. A short **RSI-2** (Connors-style) is best. The effective numerator is scaled *down* by the candle's live rolling HV: **N_eff = min(N, max(floor, slope × HV))** with N=40 (cap), slope=0.6, floor=8 — so the trim is **harder on low-vol candles** (their overbought spikes reliably mean-revert → cut them) and **relaxes up to the N cap as HV rises** (letting volatile trends run). This was the one conditioning that survived: it cuts drawdown in every mode with return ~flat, replicated 4/4 across disjoint random-500 OOS samples. A "let high-vol run" upside also showed up but was separated out as **beta** (return *and* drawdown rose together on the survivor names, not a real signal, and not rescued by trend-persistence) — so N_eff is deliberately *capped* at N, never lightened past it. `HvTrimSlope = 0` reverts to a fixed N; `RsiOverlayPeriod = 0` turns the trim off. Raising N raises the ceiling everywhere),
6. overridden, if the **raw exposure signal turns bearish** (out of region), per the chosen **[mode](#the-three-modes)** — cash by default,
7. **capped when acutely extended *and* short-term momentum has cracked** — if the close sits more than **55%** above its **50-bar SMA** (a parabolic blow-off) **and the candle is not ST-Bull**, exposure is pinned to a **60%** ceiling. This is a `min()`: it only lowers the *top*, never raises exposure and never forces a sell — it stops the engine *chasing the vertical tail* while staying in the trend. The extended tail carries near-zero forward return but ~2× the forward drawdown on the *reverting* high-vol cohort, so capping it is **efficiency** (return up *and* drawdown down — validated 4/4 in Deploy across disjoint random-500 OOS samples and strongest on the violent-rip cohort), not a de-risk. The **ST-Bull exclusion** is what makes it safe: a per-state decomposition showed the entire give-back lives in the *non*-ST-Bull states — a bull run's first crack while extended (the state machine's `BullNeutral`) — while the still-pushing ST-Bull bars are genuine continuation, so capping them only forfeits winner upside (an all-bars cap measurably hurts the leveraged winners; the gate erases that cost). A 50-bar MA isolates *acute* spikes; a slower MA flags sustained trends and caps genuine winners. `ExtCapPct = 0` turns it off.
8. *(available but **DEFAULT OFF** — `DdRatioMode = 0`)* **a drawdown-recovery scaler**: two trailing drawdowns of the close, `dd60` from the 60-bar high and `dd30` from the 30-bar high, with exposure multiplied by `clamp(K × dd60/dd30, 0.5, 1.5)`. It shipped on 2026-07-30 and was **disabled the next day**, because the ratio is **depth-blind**. The algebra: `dd30 == dd60` exactly when `hi30 == hi60`, i.e. **whenever the 60-bar peak falls inside the last 30 bars** — the normal state in *any* uptrend — and then the ratio is 1 and the multiplier pins to `K`, the hardest de-lever, *regardless of how shallow the pullback is*. Worked example: **IREN 2025-09-08** had run 16.58 → 26.19 (**+58%**) across the window and sat 10% below its peak; it was cut to **0.50×**, identically to a name 40% down and still falling. Measured across 208,000 scored bars: **54% of all bars** have the peak inside the last 30 bars, **~41% of all bars** take the maximum cut, and **26.5% of all bars are uptrend bars** (close above the 50-bar SMA) being halved at a median depth of just **6.3%**. The original rationale — "de-levers while still making new short-window lows" — was never what the expression computed: `dd60/dd30` encodes **peak age**, and being scale-free it discards depth, which is the one thing that should govern how hard to de-lever. The joint (dd30 × dd60) map that motivated it still stands as a *measurement* (forward return tracks recovery off an older low, not depth), but the ratio was the wrong encoder of it. **Open work item:** a depth-aware replacement that de-levers on how far price is down *now* and levers on the recovered gap in points, re-derived from a corrected trailing-drawdown map. `DdRatioMode = 1` restores the old behaviour.
9. and finally **EMA-smoothed** as a *final position* — averaging out the RSI-2 single-bar chatter. Unlike a harder trim (which cuts drawdown by holding *less*), this cuts it by holding *steadier*, so it preserves upside participation. The base period is **P5**, but the smoothing gets **heavier the further price sits *below* its Kaufman adaptive MA (KAMA)** and stays light at/above it. A name pulling back below its KAMA chatters and heavy smoothing is *efficiency* (return up **and** drawdown down); a name at/above its KAMA is *trending*, so it stays responsive at P5 and participation is preserved (a flat P50 would crater the rip). The period is one continuous ramp — `below = max(0, (kama − close) / kama)`, then `smoothPer = clamp(5 + KamaSmoothSlope · below · 50, 5, 50)` with **slope 4** — so it sits at the P5 floor at/above the KAMA and rises smoothly toward the 50-bar ceiling the deeper the pullback (saturating around ~22% below). The KAMA itself adapts by the same rolling price efficiency-ratio the engine already computes (fast 2 / slow 30). This **replaced** the older HV+ER "corner" smoother (a gated, chop-duration-ramped taper): one continuous rule, no gate, it **matches the corner on the broad OOS universe** (4-sample median return-per-drawdown 0.31 vs 0.29), **beats it on the violent-rip cohort**, **cuts drawdown**, and **wins 14 of 18 basket names over full history** — at the cost of giving back some explosive V-recovery upside on the wildest names (IREN, ASST). A distance *cap* and an *ER gate* were both tried as guards on that give-back and **both degraded the broad OOS without fixing it** — the benefit and the cost share the same trigger (the deep-below-KAMA smoothing that rescues a recovering pullback is the same behavior that over-holds one that keeps falling), so neither shipped. `PositionSmoothPeriod = 0` turns smoothing off; `KamaSmooth = false` reverts to the flat P5 EMA; `KamaSmoothSlope` / `KamaSmoothMaxPeriod` set the ramp rate and ceiling.

**Default parameters** (`Program.cs`): Exposure EMA `5`, Bias period `15`, Bias EMA `150`, Rebalance drift `30%`, exposure clamp `0–150%` (ceiling `200%`), RSI overlay period `2` / numerator cap `40` / HV-trim slope `0.6` / floor `8`, extension cap `55%` trigger / `60%` ceiling / `50`-bar MA, final-position smoothing `5` (KAMA-distance smoothing on: period ramps toward `50` the further price is below its KAMA — `clamp(5 + 4·max(0,(kama−close)/kama)·50, 5, 50)`, KAMA fast `2` / slow `30`). The long bias is dynamic by default. Smoothing knobs were validated as near-optimal and robust — see [Notes on tuning](#notes-on-tuning).

---

## The dynamic long-bias

The **long bias** controls how hard a bullish LT regime is leaned into. In the running trend sum, a Bull candle contributes `1 + bias` and a Bear candle a flat `−1`. A larger bias pushes exposure up harder in uptrends.

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

**Knobs** (all on `BankrollSimulator`, hand-set — *not* fitted to returns): `DynBase` (**1**), `DynDecay` (**0.6**), `DynSmoothPeriod` (**10**), `DynMin`/`DynMax` (`[0, 150]`), `HvWindow`/`PersistWindow` (**60 / 63**), refs `HvRefMean`/`HvRefStd` (**57 / 34.6**), `PersRefMean`/`PersRefStd` (**0.072 / 0.010**), the out-of-region rule `BearRegimeMode` (**1 = cash**), `RsiOverlayPeriod` (**2**, 0 = off), `RsiMultNumerator` (**40** — the trim N *cap*), the HV-conditioned trim `HvTrimSlope` (**0.6**, 0 = off) / `HvTrimFloor` (**8**) — N_eff = min(N, max(floor, slope·HV)), and the **extension cap** `ExtCapPct` (**55** — % above the MA that triggers it, 0 = off) / `ExtCapCeil` (**60** — exposure ceiling when extended) / `ExtMaPeriod` (**50** — the MA lookback).

**Out-of-region rule (`BearRegimeMode`).** A name is out of region **whenever its raw exposure signal is bearish** — the EMA of the (LT, ST) target (before the bias skew) is < 0. One condition, no windows to tune. `BearRegimeMode` then picks the [mode](#the-three-modes). This replaced an earlier trailing-persistence rule (two tuned windows): raw < 0 is cleaner *and* scores a higher OOS Cash Sharpe (0.22 vs 0.11 on a broad ~1,300-name universe). It's a **reactive** signal — it can't tell a recoverable pullback from a real decline in advance.

The dynamic bias is mirrored in the Pine scripts: the per-candle bias (orange `Dyn LongBias` stepline), the table row, and the Data Window (`DBG Dyn LongBias` / `DBG z`). The table also shows the LT / ST persistence ratios and the **Region** status (IN / OUT → cash), and the exposure line drops to 0 when the cash exit fires.

---

## Expressing the exposure through options (research)

> **Model-only — read the caveats first.** This is a separate research simulator (`OptionsOverlaySimulator.cs`), **not part of the production engine**, and it changes no defaults. There is **no real options chain** in the pipeline: every option is priced and marked with Black-Scholes (r = 0) at an implied vol of **trailing-60-day realized HV × 1.10** (a vol-risk-premium). It ignores **volatility skew, term structure, early assignment, and liquidity**, and the results are **highly sensitive to execution cost**. Treat this as a directional estimate, not a tradeable backtest.

Instead of holding the underlying at the engine's target exposure, this expresses that **same per-bar target as the net delta of an options structure** — rolling short-dated options (**~14 DTE** by default — see [Tuning the PMCC](#tuning-the-pmcc-delta-dte-and-the-flat-rule)) to steer net delta onto the target (short calls reduce delta, short puts add it), using the delta rebalance-drift band (30%) as the roll trigger and rolling any long-dated leg at expiry. Four structures:

| Structure | Long core | Delta steered by | Net-delta range |
|---|---|---|---|
| **PMCC** *(the capped, cleanest structure — no naked puts)* | long **0.80Δ** call LEAP (365 DTE) | short calls only | 0 → ~0.80 (pinned at the LEAP delta) |
| **PMCC + short puts** *(the >1.0 lean — capital caveat below)* | long **0.80Δ** call LEAP (365 DTE) | short calls (reduce) / short puts (add) | **0 → 1.5** |
| **Short-put** *(fully clean — no naked legs; cash-secured)* | *(none)* | one short put at delta = min(target, **0.50**), size capped so strike collateral ≤ account — ATM, peak theta | 0 → 0.50 |
| **Covered stock** | long shares | short calls / short puts | 0 → 1.5 |

Because the engine now clamps to **150%** exposure (see [defaults](#5-from-target-to-position-the-overlay)), the strong-signal candles ask for a target above 1.0. Only the structures that **add** delta with short puts (**PMCC + short puts**, covered stock) can express that; the **plain PMCC self-caps at its LEAP delta (~0.80)** and the short-put at 0.50. Adding short puts on top of the PMCC's call LEAP runs the leverage the engine wants and lifts the out-of-sample ratios — **with a capital caveat: the delta above ~1.0 comes from *naked* short puts (they can't be cash-secured with the freed capital), so this is a delta-only picture of what that extra exposure would earn, not a cash-secured structure.** The plain PMCC is the clean, no-naked-puts alternative.

**No naked short calls anywhere.** When a structure needs to *reduce* delta below its long core (PMCC, covered stock, and the reduce side of PMCC + short puts), it is capped at **one short call covered 1:1 by the long core** (a single LEAP or stock unit); any reduction beyond that one call's delta is expressed with a **long put** instead of a second, uncovered call. This closed a model artifact — the old code stacked multiple out-of-the-money short calls (2–3 contracts against one core), harvesting ~25–30% of extra theta that a naked position would collect but that the Black-Scholes model can't charge tail risk for. Removing it is why PMCC and covered-stock returns come down here versus earlier drafts (the short-put, which sells no calls, is unchanged). The one structure with fully no naked legs of any kind is the **single short-put** (one put at delta ≤ 0.50).

**The short-put is cash-secured (no put margin either).** A short put's real collateral is the **strike** — the cash you must hold to buy the shares if assigned — not `delta × spot`. The model now caps the put's size at each sale so its strike collateral never exceeds the account (`CashSecuredPut = true`). This matters because the overlay's account grows only through the (delta-capped, defensive) option P&L, while the strike tracks the *underlying*: on a name that has run up several-fold, a full ATM put's strike is far larger than the account, so a genuinely cash-secured seller can only carry a *fraction* of a contract. Measured across the 961-name broad set the un-capped version was implicitly running **~1.3× leverage on average (≈2× on the high-flyer basket, up to ~18× on extreme winners)** — that "leverage" was the bulk of the short-put's old table-topping basket return. With the cap on, the short-put's basket ratio falls from ~4.4 to ~3.4 while broad (1.46) and decliners barely move, because those cohorts never appreciated enough to trigger the cap. It is now a true cash-secured put — and the reason PMCC beats it on the flyers is precisely that PMCC's delta rides an *owned, fully-paid* LEAP, which is not margin and so is never capped.

When the engine target drops **below 0.20** the bar is treated as **"flat"** — the signal is too weak to express (`FlatEps = 0.20`). A structure with a core (PMCC, covered stock) then **holds the small position for 20 days and only closes to cash if it's still flat** (see [Tuning the PMCC](#tuning-the-pmcc-delta-dte-and-the-flat-rule)) rather than flattening on the first weak bar — holding won on every universe (it keeps the core's gamma for the frequent snap-backs and avoids churning the wide-spread LEAP in and out). The single short-put carries no core, so for it the rule is simply an **expression floor**: it won't sell a put below a 0.20 target and holds cash instead.

All on the **shipped engine config** (150% exposure cap, HV-conditioned RSI trim, N cap 40) at the **optimal/default overlay parameters** (365-DTE LEAP core, **14-DTE short legs**, **flat below a 0.20 target, hold-20-then-cash**; PMCC 0.80Δ), **pooled across four disjoint samples covering the full ≥ $500M universe (2,289 names), over each name's FULL ~5-year history** (the out-of-sample versions of all four tables are folded in below them). Each cell is the **mean across names** of **return% / max-DD%** — shown **frictionless** (a ceiling) and at **mid ~1%** (patient limit fills near mid). Same caveat as the tables above: the mean is the equal-weight-book figure, but a mean of per-name drawdowns is not a portfolio drawdown. *(Sharpe dropped by design — these are read on return vs drawdown.)* The last column is the **opportunity-cost lens**: **In-trade %** (share of OOS bars the position actually holds market exposure, |net delta| > 0.05) and **avg exp** (mean |net delta| across all bars — capital at work per dollar). Buy-&-hold is 100% / 1.00 by definition; every overlay sits well below on both, and that gap is the price paid for the drawdown reduction.

### Broad (2289 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+71% / 58.6* | — | 100% / 1.00 |
| *Cash (engine)* | *+32% / 34.2* | — | 81% / 0.34 |
| PMCC + short puts | +60% / 28.3 | **+39% / 32.6** | 79% / 0.34 |
| PMCC | +55% / 28.5 | **+34% / 33.0** | 78% / 0.33 |
| **Short-put** | +51% / 21.4 | **+40% / 22.6** | 58% / 0.22 |
| Covered stock | +57% / 30.6 | **+37% / 34.9** | 58% / 0.30 |

### Decliners (906 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *−43% / 71.6* | — | 100% / 1.00 |
| *Cash (engine)* | *−1% / 42.7* | — | 80% / 0.34 |
| PMCC + short puts | +5% / 27.1 | **−6% / 31.1** | 78% / 0.34 |
| PMCC | +3% / 27.3 | **−9% / 31.5** | 77% / 0.33 |
| **Short-put** | +14% / 20.8 | **+9% / 22.1** | 57% / 0.23 |
| Covered stock | +3% / 28.4 | **−7% / 32.2** | 57% / 0.30 |

### Violent (595 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+182% / 66.6* | — | 100% / 1.00 |
| *Cash (engine)* | *+89% / 40.8* | — | 83% / 0.39 |
| PMCC + short puts | +128% / 40.7 | **+96% / 46.1** | 82% / 0.39 |
| PMCC | +117% / 41.4 | **+84% / 47.0** | 81% / 0.38 |
| **Short-put** | +108% / 29.1 | **+84% / 30.5** | 63% / 0.24 |
| Covered stock | +134% / 44.1 | **+101% / 49.2** | 63% / 0.36 |

### Hand-picked high-vol basket (19 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+138% / 71.3* | — | 100% / 1.00 |
| *Cash (engine)* | *+241% / 41.2* | — | 83% / 0.37 |
| PMCC + short puts | +157% / 32.9 | **+123% / 36.9** | 79% / 0.37 |
| PMCC | +161% / 32.7 | **+128% / 36.9** | 78% / 0.35 |
| **Short-put** | +135% / 23.9 | **+115% / 25.1** | 59% / 0.23 |
| Covered stock | +157% / 32.4 | **+131% / 35.4** | 59% / 0.33 |

<details>
<summary><b>The same four tables scored out-of-sample (last 30% of each name's history)</b> — the honest expectation, on data the parameters never saw</summary>

### Broad (2289 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+37% / 38.9* | — | 100% / 1.00 |
| *Cash (engine)* | *+18% / 21.2* | — | 85% / 0.38 |
| PMCC + short puts | +30% / 20.7 | **+23% / 22.3** | 83% / 0.38 |
| PMCC | +27% / 20.6 | **+20% / 22.2** | 83% / 0.37 |
| **Short-put** | +23% / 16.1 | **+20% / 16.7** | 64% / 0.25 |
| Covered stock | +28% / 22.1 | **+21% / 23.6** | 64% / 0.34 |

### Decliners (834 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *−26% / 48.4* | — | 100% / 1.00 |
| *Cash (engine)* | *−8% / 25.2* | — | 83% / 0.35 |
| PMCC + short puts | −2% / 20.8 | **−6% / 22.5** | 82% / 0.36 |
| PMCC | −3% / 20.7 | **−7% / 22.4** | 82% / 0.34 |
| **Short-put** | +3% / 17.1 | **+1% / 17.9** | 60% / 0.24 |
| Covered stock | −2% / 22.2 | **−6% / 23.9** | 60% / 0.31 |

### Violent (208 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+143% / 60.1* | — | 100% / 1.00 |
| *Cash (engine)* | *+86% / 38.8* | — | 88% / 0.49 |
| PMCC + short puts | +110% / 44.7 | **+94% / 47.4** | 88% / 0.50 |
| PMCC | +100% / 44.4 | **+84% / 47.1** | 87% / 0.48 |
| **Short-put** | +76% / 28.6 | **+62% / 29.5** | 72% / 0.28 |
| Covered stock | +116% / 44.8 | **+98% / 47.0** | 72% / 0.47 |

### Hand-picked high-vol basket (19 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+138% / 71.3* | — | 100% / 1.00 |
| *Cash (engine)* | *+241% / 41.2* | — | 83% / 0.37 |
| PMCC + short puts | +157% / 32.9 | **+123% / 36.9** | 79% / 0.37 |
| PMCC | +161% / 32.7 | **+128% / 36.9** | 78% / 0.35 |
| **Short-put** | +135% / 23.9 | **+115% / 25.1** | 59% / 0.23 |
| Covered stock | +157% / 32.4 | **+131% / 35.4** | 59% / 0.33 |

</details>

**Reading it (return ÷ max-DD).** Three model-honesty rules shape this: short calls are covered 1:1 (no naked calls), the single short-put is **cash-secured** (its size is capped so the strike collateral never exceeds the account), and weak signals aren't expressed — **any target below 0.20 is treated as "flat"** (`FlatEps = 0.20`; see [the flat rule](#tuning-the-pmcc-delta-dte-and-the-flat-rule)). Over full history the **single cash-secured short-put leads every broad cohort** and carries the shallowest drawdowns everywhere; on the concentrated high-flyer basket the un-overlaid engine in Cash mode posts the top ratio, because the option structures' delta caps bite hardest exactly where the underlying compounds fastest:
- **Broad:** the **short-put leads** (+40%/22.6, ratio **1.77**) and is the only overlay clearly ahead of buy-&-hold (+71%/58.6, 1.21) — well over half the return at barely a third of the drawdown. Then PMCC + short puts (+39%/32.6, 1.20), covered stock (+37%/34.9, 1.06), PMCC (+34%/33.0, 1.03) and Cash (+32%/34.2, 0.94).
- **Decliners:** every overlay beats buy-&-hold's −43%, and the **short-put is decisively best** — solidly positive where the underlying loses two-fifths.
- **Violent:** **buy-&-hold wins this cohort on means** (+182%/66.6, **2.73**) — its mean is carried by a few enormous winners the delta-capped structures cannot follow. The **short-put is closest** (+84%/30.5, **2.75** — in fact a shade ahead) at under half the drawdown, then covered stock (+101%/49.2, 2.05), PMCC + short puts (+96%/46.1, 2.08), Cash (2.18) and PMCC (+84%/47.0, 1.79).
- **Basket (19, incl IREN):** the **un-overlaid engine wins outright** — Cash posts +241%/41.2 (**5.85**), ahead of the short-put (+115%/25.1, **4.58**, and the shallowest drawdown on the page), covered stock (+131%/35.4, 3.70), PMCC (+128%/36.9, 3.47) and PMCC + short puts (+123%/36.9, 3.33). All five comfortably beat buy-&-hold (+138%/71.3, 1.94).
- **Cost sensitivity:** covered stock rolls the most contracts, so it loses the most from frictionless→mid; the single-leg **short-put is the most cost-stable** (only ~3–4 points), ahead of the PMCC structures (~6 points).
- **Opportunity cost (last column):** every overlay runs at **~0.23–0.51 mean exposure** — roughly a third to a half of capital at work vs buy-&-hold's 1.00 — which is precisely *why* they roughly halve the drawdown. The **short-put is by far the least-deployed** (in-trade only ~59–74% of bars, avg exposure ~0.23–0.29, both the lowest) — its delta is capped at 0.50, trimmed again by the cash-secured cap, and floored out below a 0.20 target. Yet it earns the top-or-near-top risk-adjusted ratio on broad, decliners *and* the basket — it does the most with the least capital. PMCC and PMCC + short puts are the most-deployed overlays (~0.36–0.51) and lead on raw return.

> **⚠️ These tables lean on the 14-DTE theta harvest — the most model-optimistic part of the study.** Selling short-dated premium collects the steepest theta, which is why the numbers jumped versus 40-DTE, but front-week short options carry **gamma / gap / pin / assignment** risk that the Black-Scholes, close-to-close, no-real-chain model **cannot see**. The return/drawdown edge over buy-&-hold shown here is real *in the model*; treat the short-DTE-driven portion as a ceiling, not a promise. (This is also why the default short leg is 14 DTE, not 7.)


### Tuning the PMCC (delta, DTE, and the flat rule)

**Recommended starter — and the simulator defaults:** a **0.80-delta, 365-DTE call LEAP**, hedged with **~14-DTE short calls** to the exposure target; below a **0.20** target the signal is treated as flat, and a core structure then **holds the small position for 20 days before closing to cash** (never flattened on the first weak bar). In `OptionsOverlaySimulator`: `CallLeapDelta = 0.80`, `LeapDteDays = 365`, `ShortDteDays = 14`, `FlatEps = 0.20`, `FlatHoldDays = 20`. That's the all-round default; the notes below say when to deviate.

Read on the metric that matters here — **return ÷ max-drawdown** — the PMCC has four knobs:

- **Call-LEAP delta — 0.80 is the pick everywhere; don't go deeper.** A deep-ITM call is more stock-like, with less time premium to bleed and a defined downside, and **0.80** gives the best return/DD balance on the broad set. *Re-swept under the no-naked rule, 0.80 also wins on the flyer basket* — pushing to **0.90 no longer helps** (it raises basket drawdown without raising the ratio: 0.90/365 ≈ 0.80/365, and 0.90 is clearly worse at every other DTE). The old "0.90 for flyers" advice was an artifact of the naked-call era; 0.80 now dominates 0.70–0.90 across broad, decliners, and basket.
- **LEAP DTE — 365 is the all-round sweet spot; 540–720 leans defensive.** A longer-dated call bleeds theta more slowly and rolls less often, so it loses the least on decliners (−8 to −9% at 720 vs −10 to −12% at 180); 180-DTE rolls ~3×/yr and pays more roll cost. The effect is modest and a bit noisy — don't over-fit the DTE; **365 is the safe default.**
- **Short-leg DTE — shorter harvests more theta; ~14 DTE is the sweet spot.** The short legs are theta engines, and theta is steepest near expiry, so shorter-dated short legs collect more premium per unit time. **Re-swept under the no-naked rule (mid ~1%), return and the return/DD ratio rise monotonically as the short DTE shortens — across every structure and cohort:** broad PMCC ratio 0.78 (40-DTE) → 0.97 (21) → 1.09 (14) → 1.15 (7); short-put 1.21 → 1.44 → 1.59 → 1.77. The effect is smaller than the naked-era draft implied (the extra short-call contracts that used to amplify it are gone), but the direction is unchanged, and at 7 DTE the **short-put turns decliners positive** (+3%/16.6). Premium turnover per year is roughly DTE-independent (each shorter roll trades a smaller premium), so friction doesn't punish shorter legs the way you'd expect. The reason to stop at ~14 rather than 7 is **not friction — it's gamma/gap/pin/assignment** risk, which no spread level captures and which the close-to-close BS model can't see. `ShortDteDays = 14` is the default; go to 7 only if you're comfortable with weekly-gamma tail risk, or 21 to back further off it.
- **The flat rule — treat a target below 0.20 as "flat", then hold 20 days before closing (don't flatten early).** Two knobs. **(1) The flat threshold (`FlatEps = 0.20`).** A sub-0.20 target is too weak a signal to express; swept `{0.05, 0.10, 0.20, 0.30}`, **0.20 is the optimum** — it lifts the short-put's risk-adjusted ratio on every cohort and PMCC's basket, at the cost of ~12 points of the short-put's time-in-trade (75%→63% — it sits in cash through more weak-signal bars). 0.30 *overshoots* (over-cuts participation). The one loser is PMCC + short puts, the max-participation structure. **(2) Hold, don't flatten — except on stock (`FlatHoldDays = 20`, `CoveredFlatHoldDays = 0`).** For an *option*-core structure, once flat you **hold the small position for 20 days and only then close to cash** — you must *not* flatten to 0 on the first weak bar: doing so **craters PMCC's broad ratio 1.09 → 0.57**, because it churns the wide-spread LEAP in and out and misses the mean-reversion snap-backs. `hold20 ≈ never-close` (both fine); the 20-day close barely matters vs *not flattening*. **Covered stock is the exception and closes immediately.** That rule was derived on the PMCC and over-generalised: a LEAP core is a wide-spread option with convexity worth holding, while a *stock* core costs ~5bp to trade and has no gamma, so sitting in it through a weak stretch just absorbs drawdown for nothing. Measured across 2,289 names on four disjoint samples, closing immediately beats holding on **both** the in-sample head (ret/DD 0.04 → 0.20) and the held-out tail (0.24 → 0.32), and lifts the high-vol basket 0.72 → **1.14**. **The single short-put carries no core**, so for it the flat rule is a pure **expression floor** (won't sell a put below 0.20; the timer is inert — measured `hold0 = hold20 = never`, byte-identical). **Defaults: `FlatEps = 0.20`, `FlatHoldDays = 20`, `CoveredFlatHoldDays = 0`.**

> **A note on the flat rule after the drawdown-recovery scaler.** The scaler lowers the target, which lengthens runs of weak-signal bars: bars below 0.20 rose 34.8% → 45.6% and **forced core closes rose 1.86 → 2.71 per name**. That is why the overlays gave back more than the underlying did when the scaler shipped — a threshold downstream of the engine, not the scaler itself. Re-tuning found only covered stock worth changing: PMCC and PMCC + short-puts score better out-of-sample with the core *never* closed (0.44 → 0.49, 0.49 → 0.62) but clearly worse on the in-sample head (0.28 → 0.17), so that is a regime bet and the 20-day default stands; the short-put's ratio does not improve at any threshold (0.10 buys participation — in-trade 53% → 64% — at a slightly lower ratio, a preference rather than an edge).

- **Short-put roll trigger — roll on *time* (hold to expiry), not on 50% profit.** The short-put caps net delta at `ShortPutCap = 0.50`, so above a 0.50 target it never rebalances on the drift band — the roll triggers are time (`ShortRollDte`) and an optional profit target (`ShortProfitTarget`). Swept both: at the 14-DTE harvest **rolling at expiry wins** (broad ratio 1.42) and a 50%-profit rule is *counterproductive* (broad 1.39, decliners negative, ~50% more rolls) — a profit exit fires at ~0.30 delta with plenty of theta left and just churns, whereas at 14 DTE holding captures the full theta ramp; the profit exit only ever fires on rallies (a losing put runs to expiry regardless), where it pays a round-trip to re-sell a slower-decaying put. (A `ShortDeltaFloor` roll — re-arming a put once its delta decays below 0.10 — was tried and dropped: once the position is cash-secured its incremental effect is marginal.) **Defaults: `ShortRollDte = 1`, `ShortProfitTarget = 0` (off).**

**In one line:** *a single cash-secured short put — one put at delta ≤ 0.50, ~14 DTE, rolled at expiry, never sold below a 0.20 target.* It leads the broad set (1.77 vs buy-&-hold's 1.21), the decliners, and — at under half the drawdown — edges even the violent cohort (2.75 vs 2.73), while carrying the shallowest drawdown of anything on the page in all four universes. On the concentrated high-flyer basket, though, no overlay beats simply running the engine on the underlying (Cash 5.85 vs the short-put's 4.58).

**Bottom line:** at the tuned defaults (365-DTE LEAP, 14-DTE short legs, flat below a 0.20 target — hold-20 for option cores, close-immediately for stock) and with two model-honesty rules enforced — **all short calls covered 1:1 (no naked calls)** and the **short-put genuinely cash-secured (no put margin)** — the overlays beat buy-&-hold on return-per-drawdown in **three of the four universes**, losing only the violent cohort, where buy-&-hold's mean is carried by a handful of enormous winners no delta-capped structure can follow. The **single cash-secured short-put is the standout**: broad 1.63, decliners 0.46 (positive where the underlying loses 43%), violent 2.44, basket 4.42, always at the shallowest drawdown, and the most cost-stable because it trades one leg. It is also the **least-deployed** (~0.17–0.19 average exposure, in trade under half the bars) — most of the cushion comes from simply holding less, which is the honest way to read it. The **PMCC structures earn their keep on the broad set** (1.24 / 1.16, both ahead of buy-&-hold) but trail the plain engine on the basket — their delta caps bite hardest exactly where the underlying compounds fastest. Two things to keep honest: this rests on **near-mid execution**, and more importantly on the **14-DTE theta harvest whose front-week gamma / gap / assignment risk the close-to-close Black-Scholes model cannot see** — read the edge as a model ceiling, most trustworthy in the *drawdown reduction* it shows (consistent across every cohort and both windows) rather than in the return figures. Reproduce with `OptionsOverlaySimulator` over `BankrollResult.Positions`.

---

## Repository layout

```
StockOdds/                  C# console backtester (.NET)
├─ Program.cs               Config (symbol, dates, parameters) + entry point
├─ LongTermStateEngine.cs   Anchor-based LT regime machine
├─ CandleStateEngine.cs     4-state ST machine + candle classification
├─ BankrollSimulator.cs     Exposure model, bankroll sim, Sharpe / drawdown metrics
├─ OptionsOverlaySimulator.cs  Research: express exposure via options (model-only, BS + HV)
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
- **Return ÷ drawdown is not comparable across configs that deploy different amounts of capital.** A flat multiplier that ignores every signal raises it monotonically — 0.335 at full size up to **0.398 at ×0.4** — purely because return falls sub-linearly against drawdown. **Sharpe stays pinned at exactly 0.38 through every flat level**, because it is scale-invariant. So any candidate that changes mean exposure must be judged on Sharpe *at matched exposure* against a flat-haircut control, and ideally against an inverted version of itself. Both controls are what qualified the drawdown-recovery scaler; the ratio alone would have flattered it.

---

## Disclaimer

This is a research backtest, not investment advice. Past performance does not guarantee future results. Backtests use adjusted daily data from Yahoo Finance and idealized fills; live results will differ. Use at your own risk.
