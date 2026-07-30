# StockOdds

**A risk-adjustment overlay for equity exposure.** It reads each stock's trend, sizes a **0–150%** long position (leaning up to **1.5×** into the strongest signals), and — the part that matters — **steps aside to cash when the trend breaks** and trims overbought strength harder in quiet regimes than volatile ones (an **HV-conditioned trim**). The result: it **keeps most of buy-&-hold's upside while carrying a smaller drawdown in every mode.** Three selectable risk modes set how defensive: **Deploy / Hold** stay invested and essentially *dominate* buy-&-hold (higher return, better Sharpe, lower drawdown); the default **Cash** mode is the most defensive, trading some return for the shallowest drawdown of the three. Participation-tilted, not maximally defensive.

> Companion write-up (the origin of the trend model): [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

This is **not an alpha engine** and doesn't pretend to be. It's an exposure-control overlay driven by a light, deliberately simple trim (an HV-conditioned overbought trim plus final-position smoothing): on a random 500-stock universe **Deploy/Hold edge buy-&-hold on risk-adjusted return (Sharpe 0.61 / 0.57 vs 0.49) at a *lower* drawdown** (32% vs 37%), and the default **Cash** mode trades return for the shallowest drawdown of the three (**20% vs 37%**). On the stocks that hurt most — falling, or ripping higher with gut-wrenching pullbacks — it takes far less pain than buy-&-hold. No shorting: a bearish signal means *cash*, never short.

---

## What to expect

The proof is out-of-sample. Every table below is scored on the **last 30% of each name's ~5-year history** (data the parameters never saw), on a **random 500-name US-common-stock universe** with the recommended **≥ $500M market-cap floor** applied. Drawdowns are shown as positive magnitudes (smaller = better).

> **Regenerated 2026-07-28** on the shipped config (extension cap + **KAMA-distance smoothing** — the final position is smoothed progressively harder the further price sits *below* its Kaufman adaptive MA, and stays light at/above it). This one continuous rule **replaced** the older HV+ER "corner" smoother: it matches it on the broad universe, beats it on the violent-rip cohort, cuts drawdown, and wins 14/18 basket names over full history — at the cost of giving back some explosive V-recovery upside on the wildest names (IREN, ASST). Cash on the 27 violent names holds **+115%** at a **−38%** drawdown (Sharpe 0.99, up from 0.92). All tables below — including the options overlay — are regenerated on this config. See [the trade-off](#the-trade-off-honestly).

### The whole universe (296 names)

| Mode | OOS Sharpe | OOS Max DD | OOS Return |
|---|---:|---:|---:|
| **Deploy** | 0.61 | 31.9% | +35% |
| **Cash** *(default)* | 0.39 | **19.8%** | +19% |
| **Hold** | 0.57 | 33.1% | +35% |
| *Buy & hold* | *0.49* | *37.5%* | *+34%* |

Deploy essentially **dominates buy-&-hold** — higher return (+35% vs +34%), better Sharpe (0.61 vs 0.49), *and* lower drawdown (31.9% vs 37.5%); Hold matches its return at a better Sharpe and lower drawdown too. The default **Cash** mode is the low-drawdown dial: **19.8% vs B&H's 37.5%** — shallower than buy-&-hold on **288 of 296 names (97%)** — while still returning +19%. The engine is driven by an **HV-conditioned RSI-2 overbought trim** (harder on low-vol candles, capped at numerator 40) plus a light **EMA-smoothing of the final position.** The real value shows up in the two cohorts that matter most.

### When the stock is falling (99 names with a negative buy-&-hold return)

This is what a risk overlay is *for.* These names lost money over the test window — and the system barely participates in the loss:

| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Cash** *(default)* | **−8%** | **24.2%** | −0.24 |
| **Deploy** | −15% | 40.8% | −0.10 |
| **Hold** | −16% | 42.2% | −0.14 |
| *Buy & hold* | *−23%* | *47.5%* | *−0.24* |

Buy-&-hold loses **−23% with a −47% drawdown.** The default Cash mode cuts that to **−8% at a −24% drawdown** — shallower than buy-&-hold on **97 of 99** names — by going to cash when the trend breaks. And every mode now loses *less* than buy-&-hold at a lower drawdown, including the fully-invested Deploy (−15% at −41% vs B&H's −23% / −47%) — the HV-conditioned trim de-risks the low-vol give-backs on the way down. *(Sharpe is unstable when returns hug zero — read the Return and Max-DD columns here; they are the story.)*

### When the stock rips — but violently (27 names, +return but ≥ 50% buy-&-hold drawdown)

The high-flyers. The system gives up a chunk of the upside but takes a *much* smaller beating:

| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Deploy** | +163% | 57.1% | 1.07 |
| **Hold** | +162% | 56.3% | 1.06 |
| **Cash** *(default)* | +115% | **37.5%** | 0.99 |
| *Buy & hold* | *+145%* | *59.5%* | *0.99* |

Buy-&-hold makes **+145% but suffers a −60% drawdown.** Here the 1.5× lean pays: **Deploy out-returns buy-&-hold at +163%** (Sharpe 1.07 vs 0.99) at a lower drawdown (−57% vs −60%), and even the defensive **Cash** mode keeps **+115% at just −38%** (Sharpe 0.99 — up from the old corner's 0.92), shallower than buy-&-hold on **all 27** names. This is where KAMA-distance smoothing makes its clearest trade: it holds *steadier* through these names' violent pullbacks, which lifts Cash's risk-adjusted return (Sharpe 0.92 → 0.99) and trims its drawdown (37.9% → 37.5%) but gives back some of the explosive V-recovery upside (Cash return 135% → 115%). The leverage amplifies the upside on the names that keep working, while Cash captures most of it at well under two-thirds the drawdown.

### The three modes

When a name's own signal turns bearish (its raw exposure drops below zero — "out of region"), `BearRegimeMode` decides what happens:

| Mode | Out-of-region action | Character | Choose it when… |
|---|---|---|---|
| **`1` Cash** *(default)* | flatten to 0% | **maximum drawdown protection** | you preserve capital and **rotate it to another in-region name** — "go to cash" means "go find another stock to trade" |
| **`2` Hold** | force full long (mirror B&H) | ride through the dip | you have **conviction in the specific name** and don't want the rule to exit a position you mean to hold |
| **`0` Deploy** | keep running the strategy | signal everywhere | you want the raw signal applied continuously; behaves ≈ Hold |

The single-name backtest **understates Cash** — it sits in cash instead of redeploying to another opportunity, which a real portfolio would. To judge one name's continuous behaviour end-to-end, score it with `BearRegimeMode = 0` (Deploy).

### On a hand-picked high-vol basket

A curated 19-name basket (IREN added this cycle), **no per-symbol tuning**, over each name's *full* history. This is **partly in-sample** (it includes the 2022 bear the strategy dodges), so treat the broad OOS tables above as the honest expectation — this just shows per-name texture. Drawdown, default **Cash** mode vs doing nothing:

| Symbol | HV | Cash Max DD | B&H Max DD | Cash Return | B&H Return |
|---|---:|---:|---:|---:|---:|
| ^GSPC | 17 | **6%** | 25% | +25% | +71% |
| KO | 17 | **5%** | 21% | +6% | +51% |
| NVDA | 51 | **38%** | 66% | +327% | +904% |
| COIN | 85 | **63%** | 91% | +10% | −28% |
| MSTR | 90 | **52%** | 84% | +289% | +73% |
| SMR | 99 | **50%** | 87% | +381% | −15% |
| ASTS | 104 | **46%** | 86% | +1034% | +376% |
| OPEN | 109 | **73%** | 98% | +85% | −74% |
| IREN | 115 | **51%** | 96% | +1107% | +52% |

Cash cuts the drawdown on **every** name — and the HV-conditioned trim shows its shape here: the **low-vol names collapse in drawdown** (^GSPC 25%→6%, KO 21%→5%) while most **high-flyers keep their upside** (NVDA +327%, ASTS +1034%, SMR +381%) at well under buy-&-hold's drawdown. **IREN is the showcase** — Cash captures **+1107% at a 51% drawdown** where buy-&-hold round-tripped to just **+52% at −96%.** KAMA-distance smoothing is a **redistribution on this in-sample basket** — smoothing *harder* the deeper a name has pulled below its KAMA **lifts the recovering names sharply** (ASTS +686%→+1034%, MSTR +152%→+289%, OPEN +15%→+85%) while **giving back some of the wildest V-recoveries** (IREN +1682%→+1107%, COIN +33%→+10%), which snap back so fast the heavier smoothing re-levers into them a touch late. Net it wins 14 of the 18 non-index names. In aggregate Cash **edges buy-&-hold on risk-adjusted return: Basket aggregate (all 19) mean Sharpe Deploy 0.56 / Cash 0.66 / Hold 0.50 vs B&H 0.48**, at mean Max DD **Deploy 64% / Cash 42% / Hold 68% / B&H 72%** (the mean Cash return is lifted by the biggest recoveries). This is **partly in-sample** (survivor-heavy, includes the 2022 bear); the broad OOS tables above are the honest expectation.

### The trade-off, honestly

- **It is a risk overlay, not alpha.** Deploy essentially *dominates* buy-&-hold (higher return +35% vs +34%, better Sharpe 0.61 vs 0.49, *lower* drawdown 32% vs 37%); the default Cash mode trades return for the shallowest drawdown. The parts that **generalize out-of-sample are drawdown reduction and screening** — real return outperformance is modest and should not be relied on.
- **The drawdown cut is the durable edge.** The **HV-conditioned trim** (with the extension cap and KAMA-distance smoothing) cuts drawdown in *every* mode (Deploy 32% / Cash 20% / Hold 33% vs B&H 37%) by trimming the low-vol overbought give-backs harder while letting the volatile trends run. Cash is still the low-drawdown dial (20%, below B&H on 97% of names); Deploy/Hold are the return/Sharpe dial. Raising the numerator cap lightens the trim; lowering it (or `RsiOverlayPeriod`) tightens it.
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
8. and finally **EMA-smoothed** as a *final position* — averaging out the RSI-2 single-bar chatter. Unlike a harder trim (which cuts drawdown by holding *less*), this cuts it by holding *steadier*, so it preserves upside participation. The base period is **P5**, but the smoothing gets **heavier the further price sits *below* its Kaufman adaptive MA (KAMA)** and stays light at/above it. A name pulling back below its KAMA chatters and heavy smoothing is *efficiency* (return up **and** drawdown down); a name at/above its KAMA is *trending*, so it stays responsive at P5 and participation is preserved (a flat P50 would crater the rip). The period is one continuous ramp — `below = max(0, (kama − close) / kama)`, then `smoothPer = clamp(5 + KamaSmoothSlope · below · 50, 5, 50)` with **slope 4** — so it sits at the P5 floor at/above the KAMA and rises smoothly toward the 50-bar ceiling the deeper the pullback (saturating around ~22% below). The KAMA itself adapts by the same rolling price efficiency-ratio the engine already computes (fast 2 / slow 30). This **replaced** the older HV+ER "corner" smoother (a gated, chop-duration-ramped taper): one continuous rule, no gate, it **matches the corner on the broad OOS universe** (4-sample median return-per-drawdown 0.31 vs 0.29), **beats it on the violent-rip cohort**, **cuts drawdown**, and **wins 14 of 18 basket names over full history** — at the cost of giving back some explosive V-recovery upside on the wildest names (IREN, ASST). A distance *cap* and an *ER gate* were both tried as guards on that give-back and **both degraded the broad OOS without fixing it** — the benefit and the cost share the same trigger (the deep-below-KAMA smoothing that rescues a recovering pullback is the same behavior that over-holds one that keeps falling), so neither shipped. `PositionSmoothPeriod = 0` turns smoothing off; `KamaSmooth = false` reverts to the flat P5 EMA; `KamaSmoothSlope` / `KamaSmoothMaxPeriod` set the ramp rate and ceiling.

**Default parameters** (`Program.cs`): Exposure EMA `5`, Bias period `15`, Bias EMA `150`, Rebalance drift `30%`, exposure clamp `0–150%` (ceiling `200%`), RSI overlay period `2` / numerator cap `40` / HV-trim slope `0.6` / floor `8`, extension cap `55%` trigger / `60%` ceiling / `50`-bar MA, final-position smoothing `5` (KAMA-distance smoothing on: period ramps toward `50` the further price is below its KAMA — `clamp(5 + 4·max(0,(kama−close)/kama)·50, 5, 50)`, KAMA fast `2` / slow `30`). The long bias is dynamic by default. Smoothing knobs were validated as near-optimal and robust — see [Notes on tuning](#notes-on-tuning).

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



**Knobs** (all on `BankrollSimulator`, hand-set — *not* fitted to returns): `DynBase` (**1**), `DynDecay` (**0.6**), `DynSmoothPeriod` (**10**), `DynMin`/`DynMax` (`[0, 150]`), `HvWindow`/`PersistWindow` (**60 / 63**), refs `HvRefMean`/`HvRefStd` (**57 / 34.6**), `PersRefMean`/`PersRefStd` (**0.072 / 0.010**), `BiasSplit` (**on**), the out-of-region rule `BearRegimeMode` (**1 = cash**), `RsiOverlayPeriod` (**2**, 0 = off), `RsiMultNumerator` (**40** — the trim N *cap*), the HV-conditioned trim `HvTrimSlope` (**0.6**, 0 = off) / `HvTrimFloor` (**8**) — N_eff = min(N, max(floor, slope·HV)), and the **extension cap** `ExtCapPct` (**55** — % above the MA that triggers it, 0 = off) / `ExtCapCeil` (**60** — exposure ceiling when extended) / `ExtMaPeriod` (**50** — the MA lookback).

**Out-of-region rule (`BearRegimeMode`).** A name is out of region **whenever its raw exposure signal is bearish** — the EMA of the (LT, ST) target (before the bias skew) is < 0. One condition, no windows to tune. `BearRegimeMode` then picks the [mode](#the-three-modes). This replaced an earlier trailing-persistence rule (two tuned windows): raw < 0 is cleaner *and* scores a higher OOS Cash Sharpe (0.22 vs 0.11 on a broad ~1,300-name universe). It's a **reactive** signal — it can't tell a recoverable pullback from a real decline in advance.

The dynamic bias is mirrored in the Pine scripts: the per-candle bias (orange `Dyn LongBias` stepline), the table row, and the Data Window (`DBG Dyn LongBias` / `DBG z`). The table also shows the LT / ST persistence ratios and the **Region** status (IN / OUT → cash), and the exposure line drops to 0 when the cash exit fires.

---

## Expressing the exposure through options (research)

> **Model-only — read the caveats first.** This is a separate research simulator (`OptionsOverlaySimulator.cs`), **not part of the production engine**, and it changes no defaults. There is **no real options chain** in the pipeline: every option is priced and marked with Black-Scholes (r = 0) at an implied vol of **trailing-60-day realized HV × 1.10** (a vol-risk-premium). It ignores **volatility skew, term structure, early assignment, and liquidity**, and the results are **highly sensitive to execution cost**. Treat this as a directional estimate, not a tradeable backtest.

Instead of holding the underlying at the engine's target exposure, this expresses that **same per-bar target as the net delta of an options structure** — rolling short-dated options (**~14 DTE** by default — see [Tuning the PMCC](#tuning-the-pmcc-delta-dte-and-the-flat-at-0-rule)) to steer net delta onto the target (short calls reduce delta, short puts add it), using the delta rebalance-drift band (30%) as the roll trigger and rolling any long-dated leg at expiry. Four structures:

| Structure | Long core | Delta steered by | Net-delta range |
|---|---|---|---|
| **PMCC** *(the capped, cleanest structure — no naked puts)* | long **0.80Δ** call LEAP (365 DTE) | short calls only | 0 → ~0.80 (pinned at the LEAP delta) |
| **PMCC + short puts** *(the >1.0 lean — capital caveat below)* | long **0.80Δ** call LEAP (365 DTE) | short calls (reduce) / short puts (add) | **0 → 1.5** |
| **Short-put** *(fully clean — no naked legs; cash-secured)* | *(none)* | one short put at delta = min(target, **0.50**), size capped so strike collateral ≤ account — ATM, peak theta | 0 → 0.50 |
| **Covered stock** | long shares | short calls / short puts | 0 → 1.5 |

Because the engine now clamps to **150%** exposure (see [defaults](#5-from-target-to-position-the-overlay)), the strong-signal candles ask for a target above 1.0. Only the structures that **add** delta with short puts (**PMCC + short puts**, covered stock) can express that; the **plain PMCC self-caps at its LEAP delta (~0.80)** and the short-put at 0.50. Adding short puts on top of the PMCC's call LEAP runs the leverage the engine wants and lifts the out-of-sample ratios — **with a capital caveat: the delta above ~1.0 comes from *naked* short puts (they can't be cash-secured with the freed capital), so this is a delta-only picture of what that extra exposure would earn, not a cash-secured structure.** The plain PMCC is the clean, no-naked-puts alternative.

**No naked short calls anywhere.** When a structure needs to *reduce* delta below its long core (PMCC, covered stock, and the reduce side of PMCC + short puts), it is capped at **one short call covered 1:1 by the long core** (a single LEAP or stock unit); any reduction beyond that one call's delta is expressed with a **long put** instead of a second, uncovered call. This closed a model artifact — the old code stacked multiple out-of-the-money short calls (2–3 contracts against one core), harvesting ~25–30% of extra theta that a naked position would collect but that the Black-Scholes model can't charge tail risk for. Removing it is why PMCC and covered-stock returns come down here versus earlier drafts (the short-put, which sells no calls, is unchanged). The one structure with fully no naked legs of any kind is the **single short-put** (one put at delta ≤ 0.50).

**The short-put is cash-secured (no put margin either).** A short put's real collateral is the **strike** — the cash you must hold to buy the shares if assigned — not `delta × spot`. The model now caps the put's size at each sale so its strike collateral never exceeds the account (`CashSecuredPut = true`). This matters because the overlay's account grows only through the (delta-capped, defensive) option P&L, while the strike tracks the *underlying*: on a name that has run up several-fold, a full ATM put's strike is far larger than the account, so a genuinely cash-secured seller can only carry a *fraction* of a contract. Measured across the 961-name broad set the un-capped version was implicitly running **~1.3× leverage on average (≈2× on the high-flyer basket, up to ~18× on extreme winners)** — that "leverage" was the bulk of the short-put's old table-topping basket return. With the cap on, the short-put's basket ratio falls from ~4.4 to ~3.4 while broad (1.46) and decliners barely move, because those cohorts never appreciated enough to trigger the cap. It is now a true cash-secured put — and the reason PMCC beats it on the flyers is precisely that PMCC's delta rides an *owned, fully-paid* LEAP, which is not margin and so is never capped.

When the engine target drops **below 0.20** the bar is treated as **"flat"** — the signal is too weak to express (`FlatEps = 0.20`). A structure with a core (PMCC, covered stock) then **holds the small position for 20 days and only closes to cash if it's still flat** (see [Tuning the PMCC](#tuning-the-pmcc-delta-dte-and-the-flat-at-0-rule)) rather than flattening on the first weak bar — holding won on every universe (it keeps the core's gamma for the frequent snap-backs and avoids churning the wide-spread LEAP in and out). The single short-put carries no core, so for it the rule is simply an **expression floor**: it won't sell a put below a 0.20 target and holds cash instead.

All on the **shipped engine config** (150% exposure cap, HV-conditioned RSI trim, N cap 40) at the **optimal/default overlay parameters** (365-DTE LEAP core, **14-DTE short legs**, **flat below a 0.20 target, hold-20-then-cash**; PMCC 0.80Δ), **pooled across four disjoint random-500 samples (961 names after the ≥ $500M floor).** Each cell is **return% / max-DD%** — the metric that matters — shown **frictionless** (a ceiling) and at **mid ~1%** (patient limit fills near mid). *(Sharpe dropped by design — these are read on return vs drawdown.)* The last column is the **opportunity-cost lens**: **In-trade %** (share of OOS bars the position actually holds market exposure, |net delta| > 0.05) and **avg exp** (mean |net delta| across all bars — capital at work per dollar). Buy-&-hold is 100% / 1.00 by definition; every overlay sits well below on both, and that gap is the price paid for the drawdown reduction.

### Broad (961 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+37% / 34.8* | — | 100% / 1.00 |
| *Cash (engine)* | *+16% / 16.7* | — | 84% / 0.37 |
| PMCC + short puts | +28% / 15.8 | +21% / 17.2 | 83% / 0.38 |
| PMCC | +25% / 15.7 | +19% / 17.2 | 83% / 0.36 |
| **Short-put** | +23% / 13.2 | **+20% / 13.5** | 63% / 0.24 |
| Covered stock | +28% / 18.0 | +21% / 19.5 | 76% / 0.36 |

### Decliners (339 names, negative B&H return)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *−23% / 46.9* | — | 100% / 1.00 |
| *Cash (engine)* | *−8% / 21.2* | — | 82% / 0.34 |
| PMCC + short puts | −2% / 17.5 | −6% / 18.9 | 82% / 0.35 |
| PMCC | −4% / 17.5 | −8% / 19.3 | 81% / 0.34 |
| **Short-put** | +4% / 14.7 | **+1% / 15.3** | 59% / 0.24 |
| Covered stock | −3% / 20.1 | −9% / 22.2 | 74% / 0.33 |

### Violent (83 names, +return but ≥ 50% B&H drawdown)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+146% / 55.8* | — | 100% / 1.00 |
| *Cash (engine)* | *+87% / 36.9* | — | 89% / 0.50 |
| PMCC + short puts | +111% / 38.6 | +95% / 40.7 | 89% / 0.52 |
| PMCC | +100% / 40.6 | +86% / 41.9 | 89% / 0.49 |
| **Short-put** | +79% / 29.3 | **+67% / 30.3** | 74% / 0.29 |
| Covered stock | +121% / 41.2 | +105% / 43.6 | 85% / 0.50 |

### Hand-picked high-vol basket (18 names, incl IREN)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+140% / 50.4* | — | 100% / 1.00 |
| *Cash (engine)* | *+85% / 37.7* | — | 89% / 0.43 |
| PMCC + short puts | +133% / 29.2 | +114% / 31.2 | 86% / 0.44 |
| **PMCC** | +136% / 25.7 | **+119% / 27.6** | 82% / 0.40 |
| Short-put | +84% / 19.1 | +72% / 21.2 | 69% / 0.26 |
| Covered stock | +126% / 29.3 | +108% / 30.5 | 82% / 0.42 |


**Reading it (return ÷ max-DD).** Three model-honesty rules shape this: short calls are covered 1:1 (no naked calls), the single short-put is **cash-secured** (its size is capped so the strike collateral never exceeds the account), and weak signals aren't expressed — **any target below 0.20 is treated as "flat"** (`FlatEps = 0.20`; see [the flat rule](#tuning-the-pmcc-delta-dte-and-the-flat-at-0-rule)). The short-put leads the broad and decliner cohorts and carries the shallowest drawdowns, but on the high-flyer basket a cash-secured put can't keep pace and **plain PMCC posts the top ratio** there:
- **Broad:** the **short-put leads** as the lowest-drawdown seller (+20%/13.5, ratio **1.46**), then PMCC + short puts (+21%/17.2, 1.24) and plain PMCC (+19%/17.2, 1.09, ≈ B&H). B&H is +37%/34.8 (1.06) and Cash +16%/16.7 (0.97); covered stock (+21%/19.5, 1.05) sits at B&H's level once its naked-call theta is gone.
- **Decliners:** only the **short-put stays positive at mid** (+1%/15.3) where buy-&-hold loses −23% — the call-covered structures (PMCC −8%, covered stock −9%) turn mildly negative once the naked-call premium is removed. Short-put is the shallowest and the only one clearly beating B&H (the 0.20 flat floor pushes it *further* positive here — not expressing weak signals keeps it in cash through more of the decline).
- **Violent:** **B&H reclaims the ratio here** (+146%/55.8, **2.61**) — no overlay beats it once naked calls and put leverage are gone. Covered stock is the best overlay (+105%/43.6, 2.41) and PMCC + short puts next (+95%/40.7, 2.34); the short-put runs the shallowest drawdown (+67%/30.3, 2.22). On this cohort the overlays' value is drawdown reduction, not return.
- **Basket (18, incl IREN):** **plain PMCC posts the top ratio** (+119%/27.6, **4.32**), then PMCC + short puts (+114%/31.2, 3.66) and covered stock (+108%/30.5, 3.55). The cash-secured **short-put is +72%/21.2 (3.38)** — held back both by the cash-secured cap (on these high-flyers the strike collateral outgrows the premium-fed account ~2×) and the 0.20 flat floor. PMCC's delta comes from an *owned, fully-paid* LEAP, so it isn't capped and wins here. All still beat B&H (2.78) and Cash (+85%/37.7, 2.25).
- **Cost sensitivity:** covered stock rolls the most contracts, so it loses the most from frictionless→mid; the single-leg **short-put is the most cost-stable** (only ~3–4 points), ahead of the PMCC structures (~6 points).
- **Opportunity cost (last column):** every overlay runs at **~0.24–0.52 mean exposure** — roughly a third to a half of capital at work vs buy-&-hold's 1.00 — which is precisely *why* they roughly halve the drawdown. The **short-put is by far the least-deployed** (in-trade only ~59–74% of bars, avg exposure ~0.24–0.29, both the lowest) — its delta is capped at 0.50, trimmed again by the cash-secured cap, and now floored out below a 0.20 target. That under-deployment is its opportunity cost: it wins broad/decliners on *risk-adjusted* terms but leaves the most upside on the table, and can't keep pace with the owned-LEAP PMCC on the flyers. PMCC and PMCC + short puts are the most-deployed overlays (~0.36–0.52), the reason they capture more of the flyer runs.

> **⚠️ These tables lean on the 14-DTE theta harvest — the most model-optimistic part of the study.** Selling short-dated premium collects the steepest theta, which is why the numbers jumped versus 40-DTE, but front-week short options carry **gamma / gap / pin / assignment** risk that the Black-Scholes, close-to-close, no-real-chain model **cannot see**. The return/drawdown edge over buy-&-hold shown here is real *in the model*; treat the short-DTE-driven portion as a ceiling, not a promise. (This is also why the default short leg is 14 DTE, not 7.)


### Tuning the PMCC (delta, DTE, and the flat-at-0 rule)

**Recommended starter — and the simulator defaults:** a **0.80-delta, 365-DTE call LEAP**, hedged with **~14-DTE short calls** to the exposure target, **held-and-hedged at 0 delta with a 20-day timeout.** In `OptionsOverlaySimulator`: `CallLeapDelta = 0.80`, `LeapDteDays = 365`, `ShortDteDays = 14`, `FlatHoldDays = 20`. That's the all-round default; the notes below say when to deviate.

Read on the metric that matters here — **return ÷ max-drawdown** — the PMCC has four knobs:

- **Call-LEAP delta — 0.80 is the pick everywhere; don't go deeper.** A deep-ITM call is more stock-like, with less time premium to bleed and a defined downside, and **0.80** gives the best return/DD balance on the broad set. *Re-swept under the no-naked rule, 0.80 also wins on the flyer basket* — pushing to **0.90 no longer helps** (it raises basket drawdown without raising the ratio: 0.90/365 ≈ 0.80/365, and 0.90 is clearly worse at every other DTE). The old "0.90 for flyers" advice was an artifact of the naked-call era; 0.80 now dominates 0.70–0.90 across broad, decliners, and basket.
- **LEAP DTE — 365 is the all-round sweet spot; 540–720 leans defensive.** A longer-dated call bleeds theta more slowly and rolls less often, so it loses the least on decliners (−8 to −9% at 720 vs −10 to −12% at 180); 180-DTE rolls ~3×/yr and pays more roll cost. The effect is modest and a bit noisy — don't over-fit the DTE; **365 is the safe default.**
- **Short-leg DTE — shorter harvests more theta; ~14 DTE is the sweet spot.** The short legs are theta engines, and theta is steepest near expiry, so shorter-dated short legs collect more premium per unit time. **Re-swept under the no-naked rule (mid ~1%), return and the return/DD ratio rise monotonically as the short DTE shortens — across every structure and cohort:** broad PMCC ratio 0.78 (40-DTE) → 0.97 (21) → 1.09 (14) → 1.15 (7); short-put 1.21 → 1.44 → 1.59 → 1.77. The effect is smaller than the naked-era draft implied (the extra short-call contracts that used to amplify it are gone), but the direction is unchanged, and at 7 DTE the **short-put turns decliners positive** (+3%/16.6). Premium turnover per year is roughly DTE-independent (each shorter roll trades a smaller premium), so friction doesn't punish shorter legs the way you'd expect. The reason to stop at ~14 rather than 7 is **not friction — it's gamma/gap/pin/assignment** risk, which no spread level captures and which the close-to-close BS model can't see. `ShortDteDays = 14` is the default; go to 7 only if you're comfortable with weekly-gamma tail risk, or 21 to back further off it.
- **The flat rule — treat a target below 0.20 as "flat", then hold 20 days before closing (don't flatten early).** Two knobs. **(1) The flat threshold (`FlatEps = 0.20`).** A sub-0.20 target is too weak a signal to express; swept `{0.05, 0.10, 0.20, 0.30}`, **0.20 is the optimum** — it lifts the short-put on every cohort (broad ratio 1.42→**1.46**, decliners 0.03→**0.09**, basket 2.96→**3.38**) and PMCC's basket (4.04→**4.32**), at the cost of ~12 points of the short-put's time-in-trade (75%→63% — it sits in cash through more weak-signal bars). 0.30 *overshoots* (over-cuts participation — short-put broad falls to 1.31). The one loser is PMCC + short puts (broad 1.29→1.24), the max-participation structure. **(2) Hold, don't flatten (`FlatHoldDays = 20`).** For a structure with a core, once flat you **hold the small position for 20 days and only then close to cash** — you must *not* flatten to 0 on the first weak bar: doing so (`FlatHoldDays = 0`) **craters PMCC's broad ratio 1.09 → 0.57**, because it churns the wide-spread LEAP in and out and misses the mean-reversion snap-backs. `hold20 ≈ never-close` (both fine); the 20-day close barely matters vs *not flattening*. **The single short-put carries no core**, so for it the flat rule is a pure **expression floor** (won't sell a put below 0.20; the timer is inert — `hold0 = hold20 = never`, all identical). **Defaults: `FlatEps = 0.20`, `FlatHoldDays = 20`.**

- **Short-put roll trigger — roll on *time* (hold to expiry), not on 50% profit.** The short-put caps net delta at `ShortPutCap = 0.50`, so above a 0.50 target it never rebalances on the drift band — the roll triggers are time (`ShortRollDte`) and an optional profit target (`ShortProfitTarget`). Swept both: at the 14-DTE harvest **rolling at expiry wins** (broad ratio 1.42) and a 50%-profit rule is *counterproductive* (broad 1.39, decliners negative, ~50% more rolls) — a profit exit fires at ~0.30 delta with plenty of theta left and just churns, whereas at 14 DTE holding captures the full theta ramp; the profit exit only ever fires on rallies (a losing put runs to expiry regardless), where it pays a round-trip to re-sell a slower-decaying put. (A `ShortDeltaFloor` roll — re-arming a put once its delta decays below 0.10 — was tried and dropped: once the position is cash-secured its incremental effect is marginal.) **Defaults: `ShortRollDte = 1`, `ShortProfitTarget = 0` (off).**

**In one line:** *PMCC — 0.80-delta, 365-DTE call LEAP, ~14-DTE short calls to target, flat below a 0.20 target, hold-20-then-cash.* On the concentrated flyer basket this beats buy-&-hold on return/max-DD (4.32 vs 2.78); on the broad universe plain buy-&-hold still wins that ratio, so use the overlay there for the drawdown cushion, not for outperformance.

**Bottom line:** at the tuned defaults (365-DTE LEAP, 14-DTE short legs, flat below 0.20 with hold-20), and with two model-honesty rules enforced — **all short calls covered 1:1 (no naked calls)** and the **short-put genuinely cash-secured (no put margin)** — the overlays still beat buy-&-hold on **return ÷ max-drawdown in three of four universes**, most of the upside at roughly half the drawdown, breakeven on decliners where B&H bleeds −23%. The **single cash-secured short-put** (one put at delta ≤ 0.50, no naked legs of any kind) leads the broad set (1.46) and decliners, carries the shallowest drawdowns, and is the most cost-stable — the honest, most-capital-efficient pick for a diversified book (it's also the least-deployed, ~0.24 avg exposure — most of the drawdown cushion comes from simply holding less). On the **high-flyer basket, plain PMCC (0.80Δ) wins** (4.32): its delta rides an owned, fully-paid LEAP, whereas a cash-secured put's strike collateral outgrows the account on names that multiply, capping its exposure (basket 3.38). **PMCC + short puts** adds a >1.0 lean but at the cost of naked-short-*put* margin (a delta-only picture — see the capital caveat above). Once the naked-call theta is removed, **buy-&-hold reclaims the ratio on the violent cohort** — there the overlays are a drawdown tool, not an outperformer. Two things to keep honest: this rests on **near-mid execution** and, more importantly, on the **14-DTE theta harvest whose front-week gamma/gap/assignment risk the model can't see** — so read the edge as a model ceiling, strongest and most-trustworthy in the *drawdown-reduction* it shows (consistent across the whole study) rather than the short-DTE return spike. Reproduce with `OptionsOverlaySimulator` over `BankrollResult.Positions`.

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

---

## Disclaimer

This is a research backtest, not investment advice. Past performance does not guarantee future results. Backtests use adjusted daily data from Yahoo Finance and idealized fills; live results will differ. Use at your own risk.
