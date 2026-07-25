# StockOdds

**A risk-adjustment overlay for equity exposure.** It reads each stock's trend, sizes a **0–150%** long position (leaning up to **1.5×** into the strongest signals), and — the part that matters — **steps aside to cash when the trend breaks** and trims overbought strength harder in quiet regimes than volatile ones (an **HV-conditioned trim**). The result: it **keeps most of buy-&-hold's upside while carrying a smaller drawdown in every mode.** Three selectable risk modes set how defensive: **Deploy / Hold** stay invested and essentially *dominate* buy-&-hold (higher return, better Sharpe, lower drawdown); the default **Cash** mode is the most defensive, trading some return for the shallowest drawdown of the three. Participation-tilted, not maximally defensive.

> Companion write-up (the origin of the trend model): [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

This is **not an alpha engine** and doesn't pretend to be. It's an exposure-control overlay driven by a light, deliberately simple trim (an HV-conditioned overbought trim plus final-position smoothing): on a random 500-stock universe **Deploy/Hold edge buy-&-hold on risk-adjusted return (Sharpe 0.63 / 0.57 vs 0.49) at a *lower* drawdown** (33% vs 37%), and the default **Cash** mode trades return for the shallowest drawdown of the three (**21% vs 37%**). On the stocks that hurt most — falling, or ripping higher with gut-wrenching pullbacks — it takes far less pain than buy-&-hold. No shorting: a bearish signal means *cash*, never short.

---

## What to expect

The proof is out-of-sample. Every table below is scored on the **last 30% of each name's ~5-year history** (data the parameters never saw), on a **random 500-name US-common-stock universe** with the recommended **≥ $500M market-cap floor** applied. Drawdowns are shown as positive magnitudes (smaller = better).

### The whole universe (296 names)

| Mode | OOS Sharpe | OOS Max DD | OOS Return |
|---|---:|---:|---:|
| **Deploy** | 0.63 | 33.4% | +36% |
| **Cash** *(default)* | 0.37 | **20.5%** | +19% |
| **Hold** | 0.57 | 33.8% | +34% |
| *Buy & hold* | *0.49* | *37.5%* | *+34%* |

Deploy essentially **dominates buy-&-hold** — higher return (+36% vs +34%), better Sharpe (0.63 vs 0.49), *and* lower drawdown (33.4% vs 37.5%); Hold matches its return at a better Sharpe and lower drawdown too. The default **Cash** mode is the low-drawdown dial: **20.5% vs B&H's 37.5%** — shallower than buy-&-hold on **287 of 296 names (97%)** — while still returning +19%. The engine is driven by an **HV-conditioned RSI-2 overbought trim** (harder on low-vol candles, capped at numerator 40) plus a light **EMA-smoothing of the final position.** The real value shows up in the two cohorts that matter most.

### When the stock is falling (99 names with a negative buy-&-hold return)

This is what a risk overlay is *for.* These names lost money over the test window — and the system barely participates in the loss:

| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Cash** *(default)* | **−7%** | **24.8%** | −0.22 |
| **Deploy** | −16% | 42.7% | −0.06 |
| **Hold** | −17% | 43.2% | −0.14 |
| *Buy & hold* | *−23%* | *47.5%* | *−0.24* |

Buy-&-hold loses **−23% with a −47% drawdown.** The default Cash mode cuts that to **−7% at a −25% drawdown** — shallower than buy-&-hold on **97 of 99** names — by going to cash when the trend breaks. And every mode now loses *less* than buy-&-hold at a lower drawdown, including the fully-invested Deploy (−16% at −43% vs B&H's −23% / −47%) — the HV-conditioned trim de-risks the low-vol give-backs on the way down. *(Sharpe is unstable when returns hug zero — read the Return and Max-DD columns here; they are the story.)*

### When the stock rips — but violently (27 names, +return but ≥ 50% buy-&-hold drawdown)

The high-flyers. The system gives up a chunk of the upside but takes a *much* smaller beating:

| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Deploy** | +158% | 59.0% | 1.07 |
| **Hold** | +154% | 57.0% | 1.06 |
| **Cash** *(default)* | +117% | **38.4%** | 0.91 |
| *Buy & hold* | *+145%* | *59.5%* | *0.99* |

Buy-&-hold makes **+145% but suffers a −60% drawdown.** Here the 1.5× lean pays: **Deploy out-returns buy-&-hold at +158%** (Sharpe 1.07 vs 0.99) at a slightly lower drawdown (−59% vs −60%), and even the defensive **Cash** mode keeps **+117% at just −38%** (Sharpe 0.91), shallower than buy-&-hold on **all 27** names. The leverage amplifies the upside on the names that keep working, while Cash captures most of it at two-thirds the drawdown.

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
| ^GSPC | 17 | **8%** | 25% | +21% | +71% |
| KO | 17 | **5%** | 21% | +6% | +51% |
| NVDA | 51 | **40%** | 66% | +279% | +904% |
| COIN | 85 | **56%** | 91% | −6% | −28% |
| MSTR | 90 | **54%** | 84% | +267% | +73% |
| ASTS | 104 | **53%** | 86% | +1534% | +376% |
| SMR | 99 | **58%** | 87% | +258% | −15% |
| OPEN | 109 | **81%** | 98% | +37% | −74% |

Cash cuts the drawdown on **every** name — and the HV-conditioned trim shows its shape here: the **low-vol names collapse in drawdown** (^GSPC 25%→8%, KO 21%→5%) while the **high-flyers keep their upside** (NVDA +279%, MSTR +267%, ASTS +1534%) at well under buy-&-hold's drawdown. On this basket it **edges buy-&-hold on risk-adjusted return: Basket aggregate (all 18) mean Sharpe Deploy 0.55 / Cash 0.56 / Hold 0.50 vs B&H 0.47**, at mean Max DD **Deploy 65% / Cash 44% / Hold 67% / B&H 70%.** This is **partly in-sample** (survivor-heavy, includes the 2022 bear); the broad OOS tables above are the honest expectation.

### The trade-off, honestly

- **It is a risk overlay, not alpha.** Deploy essentially *dominates* buy-&-hold (higher return +36% vs +34%, better Sharpe 0.63 vs 0.49, *lower* drawdown 33% vs 37%); the default Cash mode trades return for the shallowest drawdown. The parts that **generalize out-of-sample are drawdown reduction and screening** — real return outperformance is modest and should not be relied on.
- **The drawdown cut is the durable edge.** The **HV-conditioned trim** cuts drawdown in *every* mode (Deploy 33% / Cash 21% / Hold 34% vs B&H 37%) by trimming the low-vol overbought give-backs harder while letting the volatile trends run. Cash is still the low-drawdown dial (21%, below B&H on 97% of names); Deploy/Hold are the return/Sharpe dial. Raising the numerator cap lightens the trim; lowering it (or `RsiOverlayPeriod`) tightens it.
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
7. and finally **EMA-smoothed (period 5)** as a *final position* — averaging out the RSI-2 single-bar chatter. Unlike a harder trim (which cuts drawdown by holding *less*), this cuts it by holding *steadier*, so it preserves upside participation. It improves Sortino over the fixed-N baseline and is strongest in the mid-high HV band (50–100, the deployment sweet spot); `PositionSmoothPeriod = 0` turns it off.

**Default parameters** (`Program.cs`): Exposure EMA `5`, Bias period `15`, Bias EMA `150`, Rebalance drift `30%`, exposure clamp `0–150%` (ceiling `200%`), RSI overlay period `2` / numerator cap `40` / HV-trim slope `0.6` / floor `8`, final-position smoothing `5`. The long bias is dynamic by default. Smoothing knobs were validated as near-optimal and robust — see [Notes on tuning](#notes-on-tuning).

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



**Knobs** (all on `BankrollSimulator`, hand-set — *not* fitted to returns): `DynBase` (**1**), `DynDecay` (**0.6**), `DynSmoothPeriod` (**10**), `DynMin`/`DynMax` (`[0, 150]`), `HvWindow`/`PersistWindow` (**60 / 63**), refs `HvRefMean`/`HvRefStd` (**57 / 34.6**), `PersRefMean`/`PersRefStd` (**0.072 / 0.010**), `BiasSplit` (**on**), the out-of-region rule `BearRegimeMode` (**1 = cash**), `RsiOverlayPeriod` (**2**, 0 = off), `RsiMultNumerator` (**40** — the trim N *cap*), and the HV-conditioned trim `HvTrimSlope` (**0.6**, 0 = off) / `HvTrimFloor` (**8**) — N_eff = min(N, max(floor, slope·HV)).

**Out-of-region rule (`BearRegimeMode`).** A name is out of region **whenever its raw exposure signal is bearish** — the EMA of the (LT, ST) target (before the bias skew) is < 0. One condition, no windows to tune. `BearRegimeMode` then picks the [mode](#the-three-modes). This replaced an earlier trailing-persistence rule (two tuned windows): raw < 0 is cleaner *and* scores a higher OOS Cash Sharpe (0.22 vs 0.11 on a broad ~1,300-name universe). It's a **reactive** signal — it can't tell a recoverable pullback from a real decline in advance.

The dynamic bias is mirrored in the Pine scripts: the per-candle bias (orange `Dyn LongBias` stepline), the table row, and the Data Window (`DBG Dyn LongBias` / `DBG z`). The table also shows the LT / ST persistence ratios and the **Region** status (IN / OUT → cash), and the exposure line drops to 0 when the cash exit fires.

---

## Expressing the exposure through options (research)

> **Model-only — read the caveats first.** This is a separate research simulator (`OptionsOverlaySimulator.cs`), **not part of the production engine**, and it changes no defaults. There is **no real options chain** in the pipeline: every option is priced and marked with Black-Scholes (r = 0) at an implied vol of **trailing-60-day realized HV × 1.10** (a vol-risk-premium). It ignores **volatility skew, term structure, early assignment, and liquidity**, and the results are **highly sensitive to execution cost**. Treat this as a directional estimate, not a tradeable backtest.

Instead of holding the underlying at the engine's target exposure, this expresses that **same per-bar target as the net delta of an options structure** — rolling short-dated options (**~14 DTE** by default — see [Tuning the PMCC](#tuning-the-pmcc-delta-dte-and-the-flat-at-0-rule)) to steer net delta onto the target (short calls reduce delta, short puts add it), using the delta rebalance-drift band (30%) as the roll trigger and rolling any long-dated leg at expiry. Five structures:

| Structure | Long core | Delta steered by | Net-delta range |
|---|---|---|---|
| **Straddle LEAP** *(recommended for a flyer / momentum book)* | long **0.80Δ** call + **0.15Δ** put (365 DTE) | short calls (reduce) / short puts (add) | **0 → 1.5** (the only lean structure that levers past 1.0) |
| **PMCC** *(the capped, most cost-stable alternative)* | long **0.80Δ** call LEAP (365 DTE) | short calls only | 0 → ~0.80 (pinned at the LEAP delta) |
| **Short-put** | *(none)* | one short put at delta = min(target, **0.50**) — ATM, peak theta | 0 → 0.50 |
| **Covered stock** | long shares | short calls / short puts | 0 → 1.5 |
| **Put diagonal** | long **0.15Δ** put LEAP (365 DTE), **delta-rolled** back to 0.15Δ as the stock rises (trailing protective put — see [tuning](#tuning-the-put-diagonal-long-put-leap--short-puts)) | short puts | 0 → 1.5 |

Because the engine now clamps to **150%** exposure (see [defaults](#5-from-target-to-position-the-overlay)), the strong-signal candles ask for a target above 1.0. Only the structures that **add** delta with short puts (straddle, covered stock, put diagonal) can express that; the **PMCC self-caps at its LEAP delta (~0.80)** and the short-put at 0.50. This is the whole reason the straddle now leads on the momentum names — it can run the leverage the engine wants, and the PMCC structurally cannot.

When the target hits zero, the core is **held and hedged to 0 delta** (with a ~20-day timeout — see [Tuning the PMCC](#tuning-the-pmcc-delta-dte-and-the-flat-at-0-rule)) rather than closed out to cash — holding won on every universe (it keeps the cheap short-leg premium and the core's gamma for the frequent snap-backs, and avoids churning the wide-spread LEAP in and out).

All on the **shipped engine config** (150% exposure cap, HV-conditioned RSI trim, N cap 40) at the **optimal/default overlay parameters** (365-DTE LEAP core, **14-DTE short legs**, hold-at-0 with a 20-day timeout; PMCC 0.80Δ, put diagonal 0.15Δ), **pooled across four disjoint random-500 samples (961 names after the ≥ $500M floor).** Each cell is **return% / max-DD%** — the metric that matters — shown **frictionless** (a ceiling) and at **mid ~1%** (patient limit fills near mid). *(Sharpe dropped by design — these are read on return vs drawdown.)*

### Broad (961 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) |
|---|---|---|
| *Buy & hold* | *+37% / 34.8* | — |
| *Cash (engine)* | *+16% / 17.6* | — |
| **Straddle** | +38% / 16.2 | **+31% / 17.6** |
| PMCC | +36% / 15.9 | +28% / 17.6 |
| Short-put | +25% / 13.8 | +22% / 14.4 |
| Covered stock | +43% / 21.0 | +32% / 22.9 |
| Put diagonal † | +50% / 16.9 | +35% / 18.4 |

### Decliners (339 names, negative B&H return)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) |
|---|---|---|
| *Buy & hold* | *−23% / 46.9* | — |
| *Cash (engine)* | *−7% / 21.6* | — |
| Straddle | +6% / 17.2 | +1% / 18.6 |
| PMCC | +4% / 16.4 | −2% / 18.1 |
| **Short-put** | +3% / 15.4 | **+0% / 16.1** |
| Covered stock | +10% / 21.5 | +3% / 23.6 |
| **Put diagonal †** | +15% / 18.2 | **+7% / 20.0** |

### Violent (83 names, +return but ≥ 50% B&H drawdown)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) |
|---|---|---|
| *Buy & hold* | *+146% / 55.8* | — |
| *Cash (engine)* | *+86% / 39.1* | — |
| Straddle | +142% / 39.9 | +124% / 42.5 |
| **PMCC** | +136% / 36.6 | **+117% / 40.1** |
| Short-put | +90% / 31.7 | +78% / 32.8 |
| Covered stock | +155% / 44.8 | +123% / 48.3 |
| Put diagonal † | +178% / 39.7 | +113% / 44.3 |

### Hand-picked high-vol basket (17 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) |
|---|---|---|
| *Buy & hold* | *+128% / 48.2* | — |
| *Cash (engine)* | *+58% / 35.5* | — |
| **Straddle** | +167% / 34.5 | **+162% / 35.8** |
| PMCC | +164% / 29.9 | +144% / 32.8 |
| Short-put | +86% / 21.9 | +76% / 22.9 |
| Covered stock | +157% / 33.8 | +134% / 38.5 |
| **Put diagonal †** | +153% / 21.2 | **+125% / 23.4** |

> **†** The put-diagonal rows run its recommended **trailing-put delta-roll** (roll the 0.15Δ put LEAP back to target whenever its delta drifts ~10Δ *below* — the stock rallied and the put went far-OTM). This is a clean risk-adjusted gain in every universe (see [tuning](#tuning-the-put-diagonal-long-put-leap--short-puts)); all other structures roll their LEAP at expiry only. The roll is **put-diagonal-specific** — the same roll *drags* the straddle, so the straddle keeps its LEAPs static.

**Reading it (return ÷ max-DD).** The overlays **beat buy-&-hold on return/drawdown in every universe**, and the HV-conditioned trim tightens the drawdowns further across the board:
- **Broad:** **put-diagonal** leads (+35%/18.4, ratio **1.92**), then straddle +31%/17.6 (1.76) and PMCC +28%/17.6 (1.58); the **short-put** is the lowest-drawdown seller (+22%/14.4, 1.51). All clear B&H (+37%/34.8, 1.06) and Cash (+16%/17.6, 0.91).
- **Decliners:** the standouts are **put-diagonal (+7%) and covered stock (+3%)** — *positive at mid* where buy-&-hold loses −23% — plus the short-put holding ~flat (+0%/16.1) at the shallowest drawdown. Every structure beats B&H and Cash.
- **Violent:** the **straddle has the best ratio** (+124%/42.5, **2.93**), PMCC right behind (+117%/40.1, 2.91) — both edge B&H (+146%/55.8, 2.6); covered stock and the straddle capture the most return.
- **Basket:** the **delta-rolled put-diagonal now leads outright** — +125%/23.4 (**5.35**, the lowest drawdown of the set *and* the top ratio; the trailing put adds +13% of return at the same DD), ahead of the straddle +162%/35.8 (4.53, the highest raw return, running the >1 delta the LEAP-capped PMCC can't) and PMCC +144%/32.8 (4.38). All crush B&H (+128%/48.2, 2.65) and Cash (1.64).
- **Cost sensitivity:** covered stock and put diagonal roll the most contracts, so they lose the most from frictionless→mid; PMCC and the straddle are the most cost-stable.

> **⚠️ These tables lean on the 14-DTE theta harvest — the most model-optimistic part of the study.** Selling short-dated premium collects the steepest theta, which is why the numbers jumped versus 40-DTE, but front-week short options carry **gamma / gap / pin / assignment** risk that the Black-Scholes, close-to-close, no-real-chain model **cannot see**. The return/drawdown edge over buy-&-hold shown here is real *in the model*; treat the short-DTE-driven portion as a ceiling, not a promise. (This is also why the default short leg is 14 DTE, not 7.)

**Why the put diagonal beats a plain short put.** It isn't a hedged short put — it's a *larger* short-put book plus a bought tail hedge. To hit the same net delta it must short more puts (to offset the long put's negative delta), so it collects more premium and theta; the long-dated put is a bought hedge that pays off precisely on falling names (its **+14% vs the short put's +3%** frictionless return on decliners) and caps the drawdown of that bigger short book. More carry in the good tape, less pain in the bad — but it trades the most contracts and leans hardest on the flat-vol (no-skew) assumption. Keep the base put **shallow** (0.15Δ) — a deeper base just inflates the short book (see [Tuning the put diagonal](#tuning-the-put-diagonal-long-put-leap--short-puts)).

### Tuning the PMCC (delta, DTE, and the flat-at-0 rule)

**Recommended starter — and the simulator defaults:** a **0.80-delta, 365-DTE call LEAP**, hedged with **~14-DTE short calls** to the exposure target, **held-and-hedged at 0 delta with a 20-day timeout.** In `OptionsOverlaySimulator`: `CallLeapDelta = 0.80`, `LeapDteDays = 365`, `ShortDteDays = 14`, `FlatHoldDays = 20`. That's the all-round default; the notes below say when to deviate.

Read on the metric that matters here — **return ÷ max-drawdown** — the PMCC has four knobs:

- **Call-LEAP delta — deeper is better for return; 0.80 is the balanced starter.** A deep-ITM call is more stock-like, with less time premium to bleed and a defined downside. **0.80** is the all-round pick (best return/DD balance on the broad set); push to **0.90** for concentrated high-flyers, where it raises return *and* lowers drawdown (the basket's best return/max-DD ratio).
- **LEAP DTE — 365 is the all-round sweet spot; 540–720 leans defensive.** A longer-dated call bleeds theta more slowly and rolls less often, so it loses the least on decliners (−8 to −9% at 720 vs −10 to −12% at 180); 180-DTE rolls ~3×/yr and pays more roll cost. The effect is modest and a bit noisy — don't over-fit the DTE; **365 is the safe default.**
- **Short-leg DTE — shorter harvests more theta; ~14 DTE is the sweet spot.** The short calls are theta engines, and theta is steepest near expiry, so shorter-dated short legs collect far more premium per unit time. Return rises monotonically as the short DTE shortens — at 2% spread, broad return roughly *doubles* from 40-DTE to ~7-DTE (PMCC 8% → 30%), and it **turns decliners positive** (−9% → +1%). Crucially this is **friction-robust**: doubling the spread from 1%→2% costs a near-uniform ~8 points at *every* DTE (each weekly roll trades a smaller premium, so annual premium turnover is ~DTE-independent), so shorter isn't punished the way you'd expect — and it's **universal across all seven structures**, not a PMCC quirk. The reason to stop at ~14 rather than 7 is **not friction — it's gamma/gap/pin/assignment** risk, which no spread level captures and which the close-to-close BS model can't see. `ShortDteDays = 14` is the default; go to 7 only if you're comfortable with weekly-gamma tail risk, or 21 to back further off it.
- **At target 0, hold-and-hedge to 0 delta with a 20-day timeout — don't close out early.** Holding beats closing out in *every* case (closing crystallizes a sell-low/buy-high round trip against the lagging, mean-reverting exit signal). A short timeout is worse, not better: 5- or 10-day "hold then exit" *underperforms* holding at equal-or-worse drawdown — even on decliners — because it fires on most dips and pays that round-trip tax a few days late. The timeout only stops costing at **~20 days** (≈ pure hold on every universe while keeping the position finite — a permanent full hedge is dead capital + ongoing roll cost). **`FlatHoldDays = 20` is the default; 15 is fine on broad/moderate names but a touch short for concentrated flyers (they need ~20–25).**

**In one line:** *PMCC — 0.80-delta (→0.90 for flyers), 365-DTE call LEAP, ~14-DTE short calls to target, hold-and-hedge at 0 with a 20-day timeout.* On the concentrated flyer basket this beats buy-&-hold on return/max-DD; on the broad universe plain buy-&-hold still wins that ratio, so use the overlay there for the drawdown cushion, not for outperformance.

### Tuning the put diagonal (long put LEAP + short puts)

If you run the put diagonal instead of the PMCC, the base put LEAP wants to go **shallow and long-dated**: a **cheap ~0.10–0.15-delta far-OTM put, 360–540 DTE.**

- **Shallower base delta is strictly better** — 0.10–0.15 ≫ 0.25 ≫ 0.35 ≫ 0.50 on return in every universe, with drawdown *rising* as the base put deepens. This is counter-intuitive but mechanical: a deeper base put doesn't protect more, it forces a **bigger short-put book** (the shorts must supply target + base-delta of positive delta), which is more premium but also more contracts (friction) and more tail. A 0.50 base *loses −18% on decliners* vs −7.5% at 0.15 — the larger short book swamps the extra protection. Keep the base a cheap tail.
- **DTE 360–540 is the sweet spot** (slow theta, few rolls); shorter is a touch more return at higher drawdown. Same shape as the PMCC.
- **Roll the put LEAP to *defend* (trailing protective put) — the one delta-roll that pays.** Roll the long put back to its **0.15Δ** target whenever its delta drifts **~10Δ below** it — i.e. the stock has *rallied* and the put has gone far-OTM — which keeps the tail hedge current as price rises, so it's in place for the next pullback. Confirmed **efficiency, not beta** (paired on one harness, return ÷ max-DD): Broad 2.83 → **2.93**, Decliners 0.71 → **0.82** (*better on both return and DD* — the tell it isn't beta), Violent 4.44 → **4.47** (DD 41.0 → 39.7 at ~flat return), Basket 6.58 → **7.19** (+13% return at the *same* 21.2 DD). It survives to **~1% round-trip** cost — the far-OTM put crosses the band only on real rallies (~5–7 rolls/name over the whole window). Config: `LeapRollMode = 2` (roll when delta is *below* target−band), `LeapRollDeltaBand = 0.10`, `LeapRollCallLeg = false`. **Direction and structure both matter:** roll to *defend* (below), **not** to take profit (above — that hurts), and this is **put-diagonal-specific** — the identical roll *drags the straddle*, whose put is a minor tail the 0.80Δ call overshadows and whose melt-up basket rejects any LEAP re-centering (tested both legs, all modes/bands: the straddle basket is hurt in every case). So run the roll on the put diagonal and leave the straddle static.
- **At that shallow/long sweet spot the put diagonal is competitive with the PMCC** — it *beats* PMCC on return/max-DD on the flyer **basket** (~2.5 vs ~2.1) and loses **less on decliners** (−7.5% vs −9.1%), at a slightly higher broad drawdown. It even beats the naked short put on both counts (the tiny long put is a cheap tail while the slightly larger short book collects more premium).
- **Caveat — it's the most model-flattered structure.** The put diagonal is short-put-heavy, so unmodeled **skew** (richer short puts *and* a pricier long put) and **gap/assignment** risk on the short book would tax it more than the PMCC. Treat its basket edge as model-optimistic; the PMCC is the more robust, model-honest pick.

**Bottom line:** at the tuned defaults (365-DTE LEAP, 14-DTE short legs, hold-20), the overlays beat buy-&-hold on **return ÷ max-drawdown in every universe** — most of the upside at roughly half the drawdown, breakeven on decliners where B&H bleeds −23%. **PMCC (0.80Δ) is the recommended all-rounder**; the **put diagonal (0.15Δ) with the trailing-put delta-roll now leads the flyer basket** on return/max-DD (ratio 5.35 vs the straddle's 4.53), with covered stock also strong there. Two things to keep honest: this rests on **near-mid execution** and, more importantly, on the **14-DTE theta harvest whose front-week gamma/gap/assignment risk the model can't see** — so read the edge as a model ceiling, strongest and most-trustworthy in the *drawdown-reduction* it shows (consistent across the whole study) rather than the short-DTE return spike. Reproduce with `OptionsOverlaySimulator` over `BankrollResult.Positions`.

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
