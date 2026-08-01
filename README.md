# StockOdds

**A risk-adjustment overlay for equity exposure.** It reads each stock's trend, sizes a **0–150%** long position (leaning up to **1.5×** into the strongest signals), and — the part that matters — **steps aside to cash when the trend breaks** and trims overbought strength harder in quiet regimes than volatile ones (an **HV-conditioned trim**). The result: it **keeps most of buy-&-hold's upside while carrying a smaller drawdown in every mode.** Three selectable risk modes set how defensive: **Deploy / Hold** stay invested and essentially *dominate* buy-&-hold (higher return, better Sharpe, lower drawdown); the default **Cash** mode is the most defensive, trading some return for the shallowest drawdown of the three. Participation-tilted, not maximally defensive.

> Companion write-up (the origin of the trend model): [Three-Level Trend Following](https://josephmacri2.substack.com/p/three-level-trend-following-options)

This is **not an alpha engine** and doesn't pretend to be. It's an exposure-control overlay driven by a light, deliberately simple trim (an HV-conditioned overbought trim plus final-position smoothing): across 2,289 US names **Deploy/Hold match buy-&-hold's return (+36% vs +37%) at a better Sharpe (0.57 / 0.53 vs 0.46) and a lower drawdown** (33% / 35% vs 39%), and the default **Cash** mode trades return for the shallowest drawdown of the three (**21% vs 39%**). On the stocks that hurt most — falling, or ripping higher with gut-wrenching pullbacks — it takes far less pain than buy-&-hold. No shorting: a bearish signal means *cash*, never short.

---

## What to expect

The proof is out-of-sample. Every table below is scored on the **last 30% of each name's ~5-year history** (data the parameters never saw), on the **full US-common-stock universe above the recommended ≥ $500M market-cap floor** — 2,429 eligible tickers, 2,289 with enough history, split into four disjoint samples so every finding below is checked for replication across all four. **Every cell is a MEAN across names, not a median** — the mean is what an equal-weight book actually earns, since a portfolio's return is the average of its holdings'. Two consequences to keep in mind: means are pulled up hard by the fat right tail (on the broad universe buy-&-hold's mean return is +37% against a +14% median, because a handful of names ran several hundred percent), and **a mean of per-name max-drawdowns is not a portfolio drawdown** — a real diversified book would draw down far less than the average name does. Read the drawdown columns as "what the average holding put you through", not "what the book did". Drawdowns are shown as positive magnitudes (smaller = better). The **basket table further down covers each name's full ~5-year history** instead, and a full-history version of the three cohort tables is folded in below them.
>
> **Span convention:** the strategy can't trade until its state machine has warmed up (2-12 bars), so buy-&-hold is measured over the **identical bar span** as the strategy rather than from the very first bar — an apples-to-apples comparison. The console app's own `BuyHoldReturnPct` starts one bar in, so for names with a longer warmup its buy-&-hold figure differs slightly from these tables (materially only on the wildest names: GRPN −23% vs −1%, BE +871% vs +797%).

> **Regenerated 2026-07-31.** Shipped config: **KAMA-distance smoothing**, the flat long-bias bear, the **peak-age scaler confined to below-KAMA bars**, and — new today — the **KAMA trim adapter** (the RSI trim's numerator cap is lifted below `kama × 1.12` and tightened to 35 at/above it), with the **blow-off extension cap REMOVED**. Both are deliberate trades documented in [step 5 and the removal note](#5-from-target-to-position-the-overlay): the adapter is worth **+0.004 broad Sharpe on 4 of 4 disjoint samples** at unchanged capital but its basket gain is concentrated in four high-vol names and it costs the options overlay; the extension cap was **still a small positive broadly** (removing it lost 24 of 24 sample-comparisons) and was deleted anyway because it cost the high-vol basket 14–19 points of return and damaged the options expression. Every table below is on the shipped config and reports **means across names** — see the note above on why, and on why a mean of per-name drawdowns is not a portfolio drawdown.

### The whole universe (2289 names)
| Mode | OOS Sharpe | OOS Max DD | OOS Return |
|---|---:|---:|---:|
| **Deploy** | 0.58 | 33.1% | +36% |
| **Cash** *(default)* | 0.38 | **21.8%** | +19% |
| **Hold** | 0.55 | 35.4% | +37% |
| *Buy & hold* | *0.46* | *38.9%* | *+37%* |

### When the stock is falling (834 names)
| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Deploy** | −16% | 41.0% | -0.10 |
| **Cash** *(default)* | −8% | **26.1%** | -0.23 |
| **Hold** | −18% | 44.2% | -0.13 |
| *Buy & hold* | *−26%* | *48.4%* | *-0.27* |

### When the stock rips — but violently (208 names)
| Mode | OOS Return | OOS Max DD | OOS Sharpe |
|---|---:|---:|---:|
| **Deploy** | +130% | 58.5% | 0.92 |
| **Cash** *(default)* | +88% | **40.1%** | 0.86 |
| **Hold** | +145% | 58.5% | 0.94 |
| *Buy & hold* | *+143%* | *60.1%* | *0.93* |

<details>
<summary><b>The same three cohorts over each name's full ~5-year history</b> (partly in-sample — includes the 2022 bear the strategy dodges)</summary>

The tables above are the honest out-of-sample proof. These cover the **whole window** for reference — every name's full history, cohorts re-derived on full-history buy-&-hold (so the counts differ). The 2022 bear is in here, which is why buy-&-hold's drawdowns are far deeper and the engine's edge looks larger.

| Mode | Sharpe | Max DD | Return |
|---|---:|---:|---:|
| **Cash** *(default)* | 0.23 | **35.2%** | +34% |
| **Hold** | 0.38 | 52.8% | +73% |
| *Buy & hold* | *0.33* | *58.6%* | *+71%* |

*Whole universe, 2,289 names.*

| Falling (906 names) | Return | Max DD | Sharpe |
|---|---:|---:|---:|
| **Cash** *(default)* | −0% | **43.9%** | -0.03 |
| **Hold** | −26% | 65.4% | 0.05 |
| *Buy & hold* | *−43%* | *71.6%* | *-0.04* |

| Violent (595 names) | Return | Max DD | Sharpe |
|---|---:|---:|---:|
| **Cash** *(default)* | +96% | **42.0%** | 0.44 |
| **Hold** | +181% | 61.5% | 0.57 |
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
| KO | 17 | **5%** | 21% | +7% | +58% |
| ^GSPC | 17 | **8%** | 25% | +28% | +68% |
| AAPL | 28 | **12%** | 33% | +45% | +124% |
| MSFT | 28 | **17%** | 38% | +43% | +56% |
| NOK | 38 | **33%** | 53% | +59% | +49% |
| NVDA | 51 | **38%** | 66% | +309% | +862% |
| AMD | 56 | **41%** | 65% | +160% | +331% |
| TSLA | 60 | **30%** | 74% | +83% | +39% |
| ATAI | 85 | **59%** | 94% | +72% | −51% |
| COIN | 85 | **63%** | 91% | +8% | −36% |
| BE | 86 | **49%** | 76% | +1225% | +797% |
| FIG | 89 | **38%** | 81% | −18% | −70% |
| MSTR | 90 | **52%** | 84% | +473% | +46% |
| GRPN | 90 | **58%** | 90% | +55% | −1% |
| SMR | 99 | **58%** | 87% | +350% | −15% |
| ASTS | 104 | **53%** | 86% | +1275% | +457% |
| OPEN | 109 | **71%** | 98% | +72% | −74% |
| IREN | 116 | **47%** | 95% | +1358% | +82% |
| ASST | 199 | **86%** | 97% | +264% | −95% |

Cash cuts the drawdown on **all 19 names** — often by more than half — at a **mean drawdown of 41% against buy-&-hold's 71%**, while **out-returning it on mean return (+241% vs +138%)**. The showcases are the names buy-&-hold ruins (ASST, OPEN, SMR, MSTR all far ahead); the bill comes due on clean, relentless trends, where every layer of trimming costs participation. This is the one place the engine beats buy-&-hold on *return* as well as drawdown, and it is also the **most in-sample** part of the study (survivor-heavy, hand-picked, and it includes the 2022 bear the strategy dodges) — the broad tables above are the honest expectation.

### The trade-off, honestly

- **It is a risk overlay, not alpha.** Deploy matches buy-&-hold's return (+36% vs +37%) at a lower drawdown (33% vs 39%) and a clearly better Sharpe (0.57 vs 0.46); the default Cash mode trades much more return for the shallowest drawdown of all (21%). **On means there is no meaningful return outperformance** — what survives is the drawdown reduction and the screening. The parts that **generalize out-of-sample are drawdown reduction and screening** — real return outperformance is modest and should not be relied on.
- **The drawdown cut is the durable edge.** The **HV-conditioned trim** (with the KAMA trim adapter and KAMA-distance smoothing) cuts drawdown in *every* mode (Deploy 33% / Cash 21% / Hold 35% vs B&H 39%). Cash is the low-drawdown dial — 21%, and shallower than buy-&-hold on **2,245 of 2,289 names (98%)**; Deploy/Hold are the return/Sharpe dial. Raising the numerator cap lightens the trim; lowering it (or `RsiOverlayPeriod`) tightens it.
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

   **KAMA trim adapter (shipped 2026-07-31).** The numerator is a **cap**, and that cap now depends on how far the close sits above its KAMA: **effectively no cap below `kama × 1.12`, and a tight 35 at/above it.** Formally `N_eff = min(cap_zone, max(8, 0.6 × HV))` with `cap = ∞` below the threshold and `35` above. Two consequences follow from it being a *cap*: it is **completely inert for any name whose `0.6 × HV` never reaches it** — nothing under ~HV 58 changes at all, which is most of the universe — and it acts progressively on the high-vol tail (at HV 116 the below-zone numerator goes 40 → 68; at HV 199, 40 → 88; the extended zone goes 40 → 35).

   **Why.** A map of 2.75M bars by signed KAMA distance shows median forward-20 return falling **monotonically** with distance — **+1.08%** at 20–12% *below* the KAMA down to **−0.27%** at 12–20% above, with 4/4 sample replication in every populated bucket — while the engine's own exposure ran **inverted** to it (0.25 in the best buckets, 0.57–0.66 in the worst). This lifts the cap where forward returns are best and tightens it where they are worst.

   **Measured** (2,289 names, last 30% OOS, means, against the extension-cap-off baseline): Sharpe **0.379 → 0.383 on 4 of 4** disjoint samples, excess return ÷ drawdown over a matched-exposure flat curve **+0.005**, walk-forward in-sample **0.157 → 0.159**, decliners **−0.238 → −0.230**, violent **0.858 → 0.863**, steady 0.711 → 0.713, curated basket **281 → 309** with basket Sharpe **0.674 → 0.685** — at mean exposure 0.387 → 0.389, i.e. *unchanged capital*, so this is reshaping rather than sizing. (Sharpe is mathematically invariant to a pure exposure change — verified by scaling the shipped position by k = 0.85…1.05 and getting 0.380 every time — so it needs no haircut correction, unlike return ÷ drawdown.)

   **Controls.** A *proportional* harder slope when extended (`ExtTrimSlope = 0.45`) wins a further +0.007 broad Sharpe but degrades the violent cohort 0.867 → 0.849 and basket Sharpe 0.685 → 0.667, so it was rejected. Removing the cap entirely instead gives similar return at +3.6 drawdown, more capital and *lower* Sharpe — the extended-zone cap is what pays for lifting the one below it. And **dropping the HV slope in favour of a fixed numerator per zone fails outright**: all 12 tuned combinations score 0/4 with excess return ÷ drawdown of −0.11 to −0.40. The slope is load-bearing.

   **Honest weak points.** On the 19-name basket the gain is **four names** — of a +803 total per-name return change, ASTS is +392, IREN +160, MSTR +152, ASST +148 (the four highest-HV names, i.e. exactly where cap removal bites), and the other fifteen net *negative*. Per-name it is a coin flip (10 of 19 better on return, ratio and Sharpe alike) and the **median name is worse on all three** (76 → 72, 44.2 → 46.6, 1.71 → 1.55). It also **costs the options expression**: PMCC + short puts falls 137 → 123 mean return with Sharpe 0.431 → 0.414, better on only 4 of 19. The broad 4/4 result across 2,289 names is the real evidence, not the basket mean. `ExtTrimThreshold = 1e9` disables the adapter.

6. overridden, if the **raw exposure signal turns bearish** (out of region), per the chosen **[mode](#the-three-modes)** — cash by default,
7. scaled by a **peak-age scaler — but only on bars below the KAMA**: two trailing drawdowns of the close, `dd60` from the 60-bar high and `dd30` from the 30-bar high, with exposure multiplied by `clamp(0.75 × dd60/dd30, 0.5, 2.0)` when the close sits **below** its KAMA, and left alone otherwise.

   **What the ratio measures — precisely.** `dd30 == dd60` exactly when `hi30 == hi60`, i.e. **whenever the 60-bar peak falls inside the last 30 bars**. So the ratio reads **peak age, not depth**: it is 1 on a fresh pullback from a recent high (multiplier pins to `K`, the hardest de-lever, *at any depth*) and rises above 1 when the 60-bar peak is older than 30 bars and price is grinding back toward a nearer high (multiplier toward the 2.0 cap). Being scale-free it discards depth entirely.

   **Why the KAMA confinement is the whole feature.** Peak age means opposite things in the two regimes. Above the KAMA, a recent peak is an ordinary uptrend pullback and cutting it is pure cost; below the KAMA, a recent peak is a fresh break down and cutting it is protection. Scoring each bucket's bars as their own return series: above-KAMA return ÷ drawdown goes **0.558 → 0.475** with the scaler on (it *destroys* that bucket), while below-KAMA goes **0.514 → 0.540** with Sharpe **0.70 → 0.76**. Bars split ~51% above / 49% below.

   **Measured, confined** (2,289 names, last 30% OOS, means): return **18.0 → 19.0**, drawdown 21.2 → 21.6, return ÷ drawdown **0.849 → 0.878**, Sharpe **0.36 → 0.38** — at mean exposure **0.38 → 0.39**, i.e. *unchanged capital deployed*, so this reshapes **when** the engine holds rather than holding less. Violent cohort 2.20 → 2.30, decliners −0.32 → −0.30, curated basket 5.84 → 6.28 (13 of 19 names higher return, 13 of 19 better return ÷ drawdown). Since a signal-free flat haircut lifts return ÷ drawdown from 0.757 to 0.849 purely by holding more, every candidate was scored as *excess over a matched-exposure flat-haircut curve*: this is **+0.029**, the same scaler run **everywhere** is **+0.001** (and −0.018 at the original `K = 0.5`), **above-KAMA-only is −0.045** — the worst configuration tested — and the **inverted** tilt is −0.022/−0.032 on 0 of 4 samples. A walk-forward that re-chose the parameters on the first 70% of history still wins on the untouched tail (Sharpe 0.41 vs 0.38).

   **The two-day history, because it is the point.** The unconfined form shipped 2026-07-30 and was disabled on 2026-07-31 after a live chart: **IREN 2025-09-08** had run 16.58 → 26.19 (**+58%**) across the window and sat 10% below a peak six sessions old, so `dd30 == dd60 == 10.03%`, the ratio was exactly 1, and it took the **maximum de-lever** (0.6982 traded against 1.2850 with the scaler off) — identically to a name 40% down and still falling. Across 208,000 scored bars, **54% of all bars** had the peak inside the last 30 bars, **~41% of all bars** took the maximum cut, and **26.5% of all bars were uptrend bars** (close above the 50-bar SMA) halved at a median depth of just **6.3%**. The original rationale — "de-levers while still making new short-window lows" — was never what the expression computed. Confining it below the KAMA removes exactly that population: **the same IREN bar now prices at 1.2850, untouched**, because it is above its KAMA.

   `DdRatioKamaMode = 0` runs it everywhere (don't — that is the disabled form), `2` runs it above only, and `DdRatioMode = 0` turns the layer off entirely. `DdRatioMinDd = 1` (require a 1% drawdown on both windows) is retained but **redundant** while confined, since a bar at a fresh high is above its KAMA by construction. `DdRatioGate` stays at 0; gating on a minimum `dd60` measurably hurts.
8. and finally **EMA-smoothed** as a *final position* — averaging out the RSI-2 single-bar chatter. Unlike a harder trim (which cuts drawdown by holding *less*), this cuts it by holding *steadier*, so it preserves upside participation. The base period is **P5**, but the smoothing gets **heavier the further price sits *below* its Kaufman adaptive MA (KAMA)** and stays light at/above it. A name pulling back below its KAMA chatters and heavy smoothing is *efficiency* (return up **and** drawdown down); a name at/above its KAMA is *trending*, so it stays responsive at P5 and participation is preserved (a flat P50 would crater the rip). The period is one continuous ramp — `below = max(0, (kama − close) / kama)`, then `smoothPer = clamp(5 + KamaSmoothSlope · below · 50, 5, 50)` with **slope 4** — so it sits at the P5 floor at/above the KAMA and rises smoothly toward the 50-bar ceiling the deeper the pullback (saturating around ~22% below). The KAMA itself adapts by the same rolling price efficiency-ratio the engine already computes (fast 2 / slow 30). This **replaced** the older HV+ER "corner" smoother (a gated, chop-duration-ramped taper): one continuous rule, no gate, it **matches the corner on the broad OOS universe** (4-sample median return-per-drawdown 0.31 vs 0.29), **beats it on the violent-rip cohort**, **cuts drawdown**, and **wins 14 of 18 basket names over full history** — at the cost of giving back some explosive V-recovery upside on the wildest names (IREN, ASST). A distance *cap* and an *ER gate* were both tried as guards on that give-back and **both degraded the broad OOS without fixing it** — the benefit and the cost share the same trigger (the deep-below-KAMA smoothing that rescues a recovering pullback is the same behavior that over-holds one that keeps falling), so neither shipped. `PositionSmoothPeriod = 0` turns smoothing off; `KamaSmooth = false` reverts to the flat P5 EMA; `KamaSmoothSlope` / `KamaSmoothMaxPeriod` set the ramp rate and ceiling.

**Removed 2026-07-31 — the blow-off extension cap.** A step used to sit between the out-of-region rule and the peak-age scaler: if the close sat more than 55% above its 50-bar SMA and the candle was not ST-Bull, exposure was pinned to a 60% ceiling via a `min()` (lowering the top only, never raising exposure and never forcing a sell). It was switched to default-off and then deleted the same day, and it is worth being clear that **it was not removed for being wrong.** On the broad universe it was a small but consistent positive — turning it off lost **24 of 24 sample-comparisons** across all three modes, and the violent, decliners and steady cohorts each preferred it 6 of 6. It was removed because the margin was only **0.001–0.002 Sharpe** while it cost the concentrated high-vol basket **14–19 points of return** (267 → 281 alone, 281 → 294 alongside the KAMA trim adapter) and materially damaged the options expression — in a layer-by-layer build on IREN, switching the layer on took PMCC + short puts from 71 to **44**. That is a bad trade for a basket/overlay-focused deployment, and not worth carrying a dead code path for. Its original rationale still stands on its own terms and is preserved in git history: the acutely extended tail carries near-zero forward return but ~2× the forward drawdown on the reverting high-vol cohort, and the **ST-Bull exclusion** was what made it safe (the give-back lives entirely in the non-ST-Bull states; capping still-pushing ST-Bull bars only forfeits winner upside). Not to be confused with the **KAMA trim adapter** in step 5 — a different mechanism on the same "extension" theme, and still on.

> **The 150% ceiling needs margin, and "Cash mode" does not mean a cash account.** Two independent things share the word *cash* here. **Cash mode** describes what the engine does when the signal leaves its region — it goes to cash rather than holding or staying deployed. It says nothing about the account type. Separately, the exposure clamp runs to **150%**, and shares cannot exceed delta 1.0 without borrowing, so the strong-signal bars need a **Reg-T margin account**; in a strict cash account they are not reachable. **DECIDED 2026-07-31: keep 150%.** The alternative was measured rather than assumed. Expressing everything above 1.0 as 1.0 — the same signal, just clipped — is **Sharpe-neutral to slightly positive** (broad OOS 0.384 → 0.391, 4/4 samples) and **drawdown-positive in all five universes**, because only 3.7–4.0% of broad bars and 6.3% of basket bars ever exceed 1.0. The cost is return: the clipped portion is **5.8% of engine gross pooled, 5.6% median, and *negative* on 4 of 19 basket names** (COIN −20.2%, FIG −18.1%), i.e. clipping *helps* those. Reconfiguring properly to `MaxExposurePercent = 100` — letting the trim, scaler and smoother adapt rather than post-clipping — costs about **half the basket return (309% → 181%, or 159% with the trim slope re-tuned to 0.45) at unchanged Sharpe (0.69 → 0.67–0.68) and drawdown lower on 19 of 19 names**. Re-tuning at the lower ceiling does move three of four knob optima (trim cap 35 → 20, HV slope 0.60 → 0.35, scaler K 0.75 → 1.00) and lifts *broad* Sharpe to 0.408, but that gain **does not appear on the basket** and `ret ÷ dd` moves the wrong way (0.869 → 0.856), so by this project's own flat-haircut discipline it is a de-risking dial rather than an efficiency gain. Set `MaxExposurePercent = 100` if you are deploying in a cash account or want the drawdown reduction; expect to pay for it in return, not to gain Sharpe.

**Default parameters** (`Program.cs`): Exposure EMA `5`, Bias period `15`, Bias EMA `150`, Rebalance drift `30%`, exposure clamp `0–150%` (ceiling `200%`), RSI overlay period `2` / HV-trim slope `0.6` / floor `8` / numerator cap **uncapped below `kama × 1.12`, 35 at/above** (the KAMA trim adapter), peak-age scaler on **below-KAMA bars only** (`K = 0.75`, clamp `[0.5, 2.0]`, windows `30`/`60`, min drawdown `1%` on both), final-position smoothing `5` (KAMA-distance smoothing on: period ramps toward `50` the further price is below its KAMA — `clamp(5 + 4·max(0,(kama−close)/kama)·50, 5, 50)`, KAMA fast `2` / slow `30`). The long bias is dynamic by default. Smoothing knobs were validated as near-optimal and robust — see [Notes on tuning](#notes-on-tuning).

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

**Knobs** (all on `BankrollSimulator`, hand-set — *not* fitted to returns): `DynBase` (**1**), `DynDecay` (**0.6**), `DynSmoothPeriod` (**10**), `DynMin`/`DynMax` (`[0, 150]`), `HvWindow`/`PersistWindow` (**60 / 63**), refs `HvRefMean`/`HvRefStd` (**57 / 34.6**), `PersRefMean`/`PersRefStd` (**0.072 / 0.010**), the out-of-region rule `BearRegimeMode` (**1 = cash**), `RsiOverlayPeriod` (**2**, 0 = off), `RsiMultNumerator` (**1000** — the trim N *cap* below the KAMA threshold, i.e. effectively uncapped), the HV-conditioned trim `HvTrimSlope` (**0.6**, 0 = off) / `HvTrimFloor` (**8**) — N_eff = min(N, max(floor, slope·HV)), and the **KAMA trim adapter** `ExtTrimThreshold` (**12** — % above the KAMA at which the cap tightens) / `ExtTrimCap` (**35** — the cap at/above that distance) / `ExtTrimSlope` (**0** = leave the HV slope alone).

**Out-of-region rule (`BearRegimeMode`).** A name is out of region **whenever its raw exposure signal is bearish** — the EMA of the (LT, ST) target (before the bias skew) is < 0. One condition, no windows to tune. `BearRegimeMode` then picks the [mode](#the-three-modes). This replaced an earlier trailing-persistence rule (two tuned windows): raw < 0 is cleaner *and* scores a higher OOS Cash Sharpe (0.22 vs 0.11 on a broad ~1,300-name universe). It's a **reactive** signal — it can't tell a recoverable pullback from a real decline in advance.

The dynamic bias is mirrored in the Pine scripts: the per-candle bias (orange `Dyn LongBias` stepline), the table row, and the Data Window (`DBG Dyn LongBias` / `DBG z`). The table also shows the LT / ST persistence ratios and the **Region** status (IN / OUT → cash), and the exposure line drops to 0 when the cash exit fires.

---

## Expressing the exposure through options (research)

### The preferred structure: the split switch

**The rule, in full.** One decision, taken **only when a rebalance is forced** — that is, when held delta leaves the `± 0.30` band around the engine target, or a short leg reaches expiry. At that moment, look at the engine's **actual target exposure**:

| target at the moment of forced rebalance | what to hold |
|---|---|
| **> 0.50** | **covered stock** — buy stock with the whole account, sell a call of delta `1 − target` against it |
| **0.20 – 0.50** | **short put** at delta = target (cash-secured, so the strike is collateralised) |
| **< 0.20** | **nothing** — hold cash |

Nothing else triggers a change. A target that drifts across 0.50 while held delta stays inside the band does **not** cause a switch — the band is the sole source of hysteresis, which is what keeps turnover down. Sizing aims at the **actual target**, never at a range midpoint.

**Measured, last 30% out-of-sample, mid ~1%:**

| cohort | Split switch | best alternative | cash engine |
|---|---|---|---|
| Broad (2,289) | **+25% / 22.7** | short-put +21% / 17.1 | +19% / 21.8 |
| Decliners (834) | **−2% / 26.8** | short-put +2% / 19.9 | −8% / 26.1 |
| Violent (208) | **+106% / 38.9** | covered stock +88% / 41.5 | +88% / 40.1 |
| Basket (19) | **+349% / 43.9** | covered stock +212% / 47.9 | +309% / 43.0 |

It has the **highest return of any structure in every one of the four cohorts**, on both windows, and it is the only overlay that beats the cash engine on the basket (+349% vs +309%) and on the violent cohort. On a 4-disjoint-sample broad OOS Sharpe it scores **0.557 against 0.547** for the previous best range configuration, winning **4 of 4 samples**, at lower drawdown (22.7 vs 23.3) and **17 points less time in trade** (71% vs 88%).

**What it is not.** The **short-put alone still owns the risk-adjusted corner** — broad 21%/17.1 and decliners +2%/19.9 are both better on return ÷ drawdown, and its drawdown is 5–6 points shallower in every cohort. The split switch wins **return** and wins **return ÷ drawdown on the violent and basket cohorts only** (2.72 vs 2.51, and 7.95 vs 3.95); on broad and decliners the short-put is still ahead. Choose the split switch for participation, the short-put for capital preservation.

**Why each piece is there.** *Covered stock rather than a LEAP above 0.50* — stock is linear, never expires, pays no premium and costs 5bp against 1% on option legs; a 0.80Δ LEAP core scored 0.46 Sharpe against stock's 0.61 even with no calls sold, and every PMCC variant could reach **−100% on a single name** because a long call expires worthless, which stock cannot do. *Aim at the actual target* — a range midpoint is a clamp artifact (`lo` cannot go below zero), and it makes a minimum-exposure rule **structurally impossible**: the midpoint bottoms out at 0.15, so any skip threshold below that can never fire. *Skip below 0.20* — cuts time in trade from 88% to 71% and lifts return per unit of deployed time from 27.3 to 33.4, with Sharpe unchanged. *Switch only on a forced rebalance* — re-evaluating the mode every bar scored **0.466** broad against 0.557 here; the churn, not the split, was the problem.

**Caveats.** Turnover is **146 rolls per name against 91** for the midpoint variant, and this is all at the 1% mid — **untested at the ~5% spread real quotes imply**, which is the first thing to check before deploying. On the basket the return gain is concentrated (IREN alone accounts for most of the mean advantage) and the basket OOS *Sharpe* is slightly worse than the midpoint variant (1.00 vs 1.05) even as return is better. And everything in this section sits under the volatility-risk-premium caveat below: the whole ranking moves with `VolRiskPremium`.

Research-only, default-inert: `OverlayStrategy.RangePutCall` with `RangeTargetMode = true`, `RangeModeSplit = 0.50`, `RangeAimAtTarget = true`, `RangePutMin = 0.20`, `RangePutMinSkip = true`, `RangePutCap = RangeShortCallCap = 0.50`, `RangeCoreStock = true`.


> **⚠️ THE STRUCTURE RANKING IS DRIVEN BY THE VOL-RISK-PREMIUM ASSUMPTION, AND IT INVERTS.** Every option here is
> priced at `IV = trailing HV × VolRiskPremium` with **VolRiskPremium = 1.10, i.e. options are 10% rich by
> construction** — so net-short-premium structures earn a guaranteed edge and net-long ones pay a guaranteed tax.
> Swept across 0.85 → 1.20 on 2,289 names (last 30% OOS), the broad Sharpe of the single short put runs
> **−0.103 / +0.066 / 0.233 / 0.400 / 0.564 / 0.723 / 1.035**, and **the ordering of the five structures inverts
> between VRP 0.90 and 0.95**: below ~0.93 every premium seller is *negative* and a pure long-option structure
> ranks first on risk-adjusted terms. So **"the single short put is the standout structure" is largely a restatement of the pricing
> assumption, not a market finding.** At fair value (1.00) premium-selling still leads, so the conclusion is
> *conditional* rather than wrong — but the condition is unvalidated, and it is weakest exactly where this study
> aims: `VolRiskPremium` is a single constant applied to a 17-HV index and a 199-HV microcap alike, while real
> variance risk premium is persistent on indices and thin-to-negative on the highest-vol single names — the curated
> basket. Read the *drawdown* reductions, which are far less VRP-sensitive, ahead of any cross-structure ranking.
> The one structure whose result barely moves across the whole range is a quantity-scaled LEAP with no offsetting
> legs (basket Sharpe range **0.08** versus **1.06** for the short put), because its edge does not come from
> premium capture at all.

> **Model-only — read the caveats first.** This is a separate research simulator (`OptionsOverlaySimulator.cs`), **not part of the production engine**, and it changes no defaults. There is **no real options chain** in the pipeline: every option is priced and marked with Black-Scholes (r = 0) at an implied vol of **trailing-60-day realized HV × 1.10** (a vol-risk-premium). It ignores **volatility skew, term structure, early assignment, and liquidity**, and the results are **highly sensitive to execution cost**. Treat this as a directional estimate, not a tradeable backtest.

Instead of holding the underlying at the engine's target exposure, this expresses that **same per-bar target as the net delta of an options structure** — rolling short-dated options (**~14 DTE** by default — see [Tuning the PMCC](#tuning-the-pmcc-delta-dte-and-the-flat-rule)) to steer net delta onto the target (short calls reduce delta, short puts add it), using the delta rebalance-drift band (30%) as the roll trigger and rolling any long-dated leg at expiry. Four structures:

| Structure | Long core | Delta steered by | Net-delta range |
|---|---|---|---|
| **PMCC** *(the capped, cleanest structure — no naked puts)* | long **0.80Δ** call LEAP (365 DTE) | short calls only | 0 → ~0.80 (pinned at the LEAP delta) |
| **PMCC + short puts** | long **0.80Δ** call LEAP (365 DTE) | short calls (reduce) / **cash-secured** short puts (add) | 0 → **~1.0–1.1** (whatever the cash left after the LEAP can secure) |
| **Short-put** | *(none)* | one short put at delta = min(target, **0.50**), size capped so strike collateral ≤ account — ATM, peak theta | 0 → 0.50 |
| **Covered stock** | long shares | short calls (the shares consume the account, so **no puts are fundable**) | 0 → 1.0 |

Because the engine clamps to **150%** exposure (see [defaults](#5-from-target-to-position-the-overlay)), the strong-signal candles ask for a target above 1.0. The **net-delta ceiling is tied to that engine ceiling** (`TieNetDeltaToEngine = true`, so `MaxNetDelta = MaxExposurePercent / 100`) rather than being a second independent literal — leaving them uncoupled hid a real bug, where the engine was swept to 300% while the overlay silently discarded every target above 1.0 and the extra exposure showed up as pure drawdown.

**But the tie is not licence to lever, because collateral is now enforced everywhere (see the two invariants below).** Only structures that **add** delta with short puts can exceed their core's delta, and every such put must be cash-secured out of the capital *not already committed to the long core*. In practice that means **PMCC + short puts tops out near 1.0–1.1**, not 1.5: the 0.80Δ LEAP consumes part of the account and the remaining cash secures only a fraction of a contract. **Covered stock cannot sell puts at all** — the shares consume the whole account — so it is capped at delta 1.0. An earlier draft of this README documented PMCC + short puts as reaching **1.5**; that was true only because its puts were *naked and unfunded*, and it is the single biggest correction in this section. Under the honest rule, every measured configuration says the same thing: **more leverage buys return at proportionally more drawdown and a lower Sharpe.**

**Two hard invariants, enforced for every structure.** These are clamps in the simulator, not properties of how each branch happens to be written, so a future edit cannot quietly reintroduce phantom leverage:

1. **Every short put is cash-secured.** Collateral is the **strike** (netted to the spread width where a long put at a lower strike offsets it), funded only from **bankroll minus capital already committed to long legs** — money spent on a call LEAP or on shares cannot also secure a put. If the requirement exceeds available cash, every put leg in that expiry is scaled down pro rata, short and long together, so a spread keeps its shape. Previously this was applied *only* to the standalone short-put structure, which is how PMCC + short puts and covered stock reached delta > 1 on unfunded naked puts. Cost of fixing it, measured on 2,289 names (last 30% OOS, mid ~1%): **PMCC + short puts loses ~0.009 Sharpe and 1.5 points of return** (0.460 → 0.451); PMCC and the standalone short-put are unchanged, and covered stock is unchanged below delta 1.0 because it never needed puts there. At the tied 1.5 ceiling the structure was asking for **1.32×** the account in collateral — a third of that delta was never fundable. *(Accounting note: the overlay is a P&L/delta model, not a settlement ledger, so "capital committed" is long legs at market value. That is the right first-order test of whether a put could be secured; it is not a margin engine.)*
2. **No naked short calls.** Short calls may never exceed the long core covering them 1:1. Every branch already satisfied this, and the regression check confirms the clamp changes nothing (PMCC is byte-identical with it on or off) — it exists so that stays true.

**How the no-naked-call rule works.** When a structure needs to *reduce* delta below its long core (PMCC, covered stock, and the reduce side of PMCC + short puts), it is capped at **one short call covered 1:1 by the long core** (a single LEAP or stock unit); any reduction beyond that one call's delta is expressed with a **long put** instead of a second, uncovered call. This closed a model artifact — the old code stacked multiple out-of-the-money short calls (2–3 contracts against one core), harvesting ~25–30% of extra theta that a naked position would collect but that the Black-Scholes model can't charge tail risk for. Removing it is why PMCC and covered-stock returns come down here versus earlier drafts (the short-put, which sells no calls, is unchanged). The one structure with fully no naked legs of any kind is the **single short-put** (one put at delta ≤ 0.50).

**The short-put is cash-secured (no put margin either).** A short put's real collateral is the **strike** — the cash you must hold to buy the shares if assigned — not `delta × spot`. The model now caps the put's size at each sale so its strike collateral never exceeds the account (`CashSecuredPut = true`). This matters because the overlay's account grows only through the (delta-capped, defensive) option P&L, while the strike tracks the *underlying*: on a name that has run up several-fold, a full ATM put's strike is far larger than the account, so a genuinely cash-secured seller can only carry a *fraction* of a contract. Measured across the 961-name broad set the un-capped version was implicitly running **~1.3× leverage on average (≈2× on the high-flyer basket, up to ~18× on extreme winners)** — that "leverage" was the bulk of the short-put's old table-topping basket return. **That drift has since been traced to a sizing defect affecting every structure, not just the put — see the note below the tables.** With the cap on, the short-put's basket ratio falls from ~4.4 to ~3.4 while broad (1.46) and decliners barely move, because those cohorts never appreciated enough to trigger the cap. *(Those ratio figures predate the sizing correction and are kept only to show the direction of the cap's effect; read the regenerated tables for levels.)* It is now a true cash-secured put — and the reason PMCC beats it on the flyers is precisely that PMCC's delta rides an *owned, fully-paid* LEAP, which is not margin and so is never capped.

When the engine target drops **below 0.20** the bar is treated as **"flat"** — the signal is too weak to express (`FlatEps = 0.20`). A structure with a core (PMCC, covered stock) then **holds the small position for 20 days and only closes to cash if it's still flat** (see [Tuning the PMCC](#tuning-the-pmcc-delta-dte-and-the-flat-rule)) rather than flattening on the first weak bar — holding won on every universe (it keeps the core's gamma for the frequent snap-backs and avoids churning the wide-spread LEAP in and out). The single short-put carries no core, so for it the rule is simply an **expression floor**: it won't sell a put below a 0.20 target and holds cash instead.

All on the **shipped engine config** (150% exposure cap, HV-conditioned RSI trim with N cap 40, and the below-KAMA peak-age scaler) at the **optimal/default overlay parameters** (365-DTE LEAP core, **14-DTE short legs**, **flat below a 0.20 target, hold-20-then-cash**; PMCC 0.80Δ), **pooled across four disjoint samples covering the full ≥ $500M universe (2,289 names), over each name's FULL ~5-year history** (the out-of-sample versions of all four tables are folded in below them). Each cell is the **mean across names** of **return% / max-DD%** — shown **frictionless** (a ceiling) and at **mid ~1%** (patient limit fills near mid). Same caveat as the tables above: the mean is the equal-weight-book figure, but a mean of per-name drawdowns is not a portfolio drawdown. *(Sharpe dropped by design — these are read on return vs drawdown.)* The last column is the **opportunity-cost lens**: **In-trade %** (share of OOS bars the position actually holds market exposure, |net delta| > 0.05) and **avg exp** (mean |net delta| across all bars — capital at work per dollar). Buy-&-hold is 100% / 1.00 by definition; every overlay sits well below on both, and that gap is the price paid for the drawdown reduction.

**Sizing correction (2026-07-31) — these eight tables were regenerated.** The overlay used to size legs in *per-share* units: `bankroll` started at the share price and leg quantities were quantities on **one share**, but the account then compounded independently and positions were never re-scaled to it. So a position reported as "delta 1.0" was really `S/bankroll` **of the account**, and that ratio drifts without bound. On IREN it fell to **0.193 in 2023** — the overlay's biggest year — meaning full exposure was actually running ~0.19. A per-bar decomposition put **24,329 bp** of a 23,417 bp shortfall against the cash engine on that one artifact, dwarfing the delta cap (2,128 bp) and swamping a convexity term that was actually *positive* (−3,040 bp). Positions are now re-scaled to the account at every establish and resize (`AccountScaledSizing`, default on), so held delta is a true fraction of capital — matching the engine's own semantics. **This was not a uniform rescaling.** The drift was name-specific and ran in both directions, so it moved the *ranking*: on the basket, covered stock goes **+135% → +212%** while PMCC + short puts' drawdown blows out **37.6 → 51.6**. Broad-cohort numbers barely move, because there `bankroll ≈ S` for most names; the distortion concentrated in exactly the high-flyers the basket is made of. Set `AccountScaledSizing = false` to reproduce anything published before this date.

### Broad (2289 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+71% / 58.6* | — | 100% / 1.00 |
| *Cash (engine)* | *+34% / 35.2* | — | 81% / 0.35 |
| **Split switch** | +81% / 32.9 | **+64% / 34.0** | 66% / 0.33 |
| PMCC + short puts | +69% / 33.4 | **+28% / 37.4** | 83% / 0.38 |
| PMCC | +57% / 33.4 | **+22% / 37.5** | 83% / 0.37 |
| Short-put | +52% / 22.0 | **+41% / 23.4** | 61% / 0.20 |
| Covered stock | +60% / 34.7 | **+34% / 37.4** | 60% / 0.34 |

### Decliners (906 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *−43% / 71.6* | — | 100% / 1.00 |
| *Cash (engine)* | *−0% / 43.9* | — | 81% / 0.35 |
| **Split switch** | +50% / 40.0 | **+34% / 41.4** | 64% / 0.33 |
| PMCC + short puts | +18% / 41.3 | **−15% / 46.6** | 82% / 0.38 |
| PMCC | +6% / 41.8 | **−21% / 47.1** | 81% / 0.36 |
| Short-put | +17% / 24.0 | **+8% / 26.2** | 64% / 0.18 |
| Covered stock | +17% / 42.3 | **−5% / 45.7** | 59% / 0.33 |

### Violent (595 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+182% / 66.6* | — | 100% / 1.00 |
| *Cash (engine)* | *+96% / 42.0* | — | 83% / 0.40 |
| **Split switch** | +161% / 39.5 | **+134% / 40.7** | 70% / 0.38 |
| PMCC + short puts | +151% / 40.8 | **+74% / 45.5** | 86% / 0.45 |
| PMCC | +128% / 40.6 | **+63% / 45.3** | 85% / 0.43 |
| Short-put | +110% / 28.1 | **+92% / 29.3** | 64% / 0.24 |
| Covered stock | +133% / 41.7 | **+89% / 44.3** | 65% / 0.40 |

### Hand-picked high-vol basket (19 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+138% / 71.3* | — | 100% / 1.00 |
| *Cash (engine)* | *+309% / 43.0* | — | 83% / 0.39 |
| **Split switch** | +414% / 42.8 | **+349% / 43.9** | 67% / 0.37 |
| PMCC + short puts | +250% / 44.3 | **+126% / 51.6** | 84% / 0.44 |
| PMCC | +209% / 44.8 | **+107% / 51.1** | 83% / 0.41 |
| Short-put | +137% / 25.9 | **+113% / 28.6** | 64% / 0.20 |
| Covered stock | +333% / 45.8 | **+212% / 47.9** | 62% / 0.38 |

<details>
<summary><b>The same four tables scored out-of-sample (last 30% of each name's history)</b> — the honest expectation, on data the parameters never saw</summary>

### Broad (2289 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+37% / 38.9* | — | 100% / 1.00 |
| *Cash (engine)* | *+19% / 21.8* | — | 85% / 0.39 |
| **Split switch** | +29% / 22.2 | **+25% / 22.7** | 71% / 0.37 |
| PMCC + short puts | +29% / 21.2 | **+20% / 22.6** | 85% / 0.40 |
| PMCC | +26% / 20.9 | **+17% / 22.3** | 85% / 0.39 |
| Short-put | +24% / 16.5 | **+21% / 17.1** | 66% / 0.25 |
| Covered stock | +26% / 22.4 | **+20% / 23.5** | 65% / 0.37 |

### Decliners (834 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *−26% / 48.4* | — | 100% / 1.00 |
| *Cash (engine)* | *−8% / 26.1* | — | 84% / 0.37 |
| **Split switch** | +0% / 26.0 | **−2% / 26.8** | 68% / 0.35 |
| PMCC + short puts | −0% / 24.4 | **−6% / 26.0** | 84% / 0.37 |
| PMCC | −4% / 24.1 | **−8% / 25.7** | 83% / 0.35 |
| Short-put | +4% / 19.1 | **+2% / 19.9** | 65% / 0.24 |
| Covered stock | −2% / 26.0 | **−7% / 27.5** | 63% / 0.34 |

### Violent (208 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+143% / 60.1* | — | 100% / 1.00 |
| *Cash (engine)* | *+88% / 40.1* | — | 88% / 0.51 |
| **Split switch** | +115% / 38.5 | **+106% / 38.9** | 79% / 0.50 |
| PMCC + short puts | +100% / 40.7 | **+68% / 44.3** | 90% / 0.53 |
| PMCC | +88% / 40.6 | **+61% / 43.6** | 90% / 0.51 |
| Short-put | +79% / 27.2 | **+70% / 27.9** | 73% / 0.30 |
| Covered stock | +102% / 40.4 | **+88% / 41.5** | 74% / 0.51 |

### Hand-picked high-vol basket (19 names)
| Strategy | frictionless (Ret / DD) | mid ~1% (Ret / DD) | In-trade % / avg exp |
|---|---|---|---|
| *Buy & hold* | *+138% / 71.3* | — | 100% / 1.00 |
| *Cash (engine)* | *+309% / 43.0* | — | 83% / 0.39 |
| **Split switch** | +414% / 42.8 | **+349% / 43.9** | 67% / 0.37 |
| PMCC + short puts | +250% / 44.3 | **+126% / 51.6** | 84% / 0.44 |
| PMCC | +209% / 44.8 | **+107% / 51.1** | 83% / 0.41 |
| Short-put | +137% / 25.9 | **+113% / 28.6** | 64% / 0.20 |
| Covered stock | +333% / 45.8 | **+212% / 47.9** | 62% / 0.38 |

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

> **A note on the flat rule and the peak-age scaler — and how the KAMA confinement fixed it downstream.** When the scaler ran **unconfined**, it lowered targets in ordinary uptrends, which lengthened runs of weak-signal bars: bars below 0.20 rose 34.8% → 45.6% and **forced core closes rose 1.86 → 2.71 per name**. That, not the scaler itself, is why the overlays gave back more than the underlying did — a fixed threshold downstream of a changed target distribution. **Confined to below-KAMA bars the pressure is simply absent:** re-measured on the shipped config, flat bars are **34.8% → 34.2%** and forced core closes **1.86 → 1.86** — unchanged to two decimals, and flat bars even fall slightly, because the bars it no longer touches were the uptrend bars driving targets under the threshold. Re-tuning the threshold under the shipped config confirms the defaults: PMCC and PMCC + short-puts still score better with the core *never* closed (0.38 → 0.41 and 0.45 → 0.53 broad) but that is the same regime bet as before, closing on the *first* flat bar is still catastrophic (PMCC 0.38 → **0.06**), and `FlatEps` is correctly inert for a never-closed core (0.05/0.10/0.20/0.30 give byte-identical rows — the consistency check on the semantics).

- **Short-put roll trigger — roll on *time* (hold to expiry), not on 50% profit.** The short-put caps net delta at `ShortPutCap = 0.50`, so above a 0.50 target it never rebalances on the drift band — the roll triggers are time (`ShortRollDte`) and an optional profit target (`ShortProfitTarget`). Swept both: at the 14-DTE harvest **rolling at expiry wins** (broad ratio 1.42) and a 50%-profit rule is *counterproductive* (broad 1.39, decliners negative, ~50% more rolls) — a profit exit fires at ~0.30 delta with plenty of theta left and just churns, whereas at 14 DTE holding captures the full theta ramp; the profit exit only ever fires on rallies (a losing put runs to expiry regardless), where it pays a round-trip to re-sell a slower-decaying put. (A `ShortDeltaFloor` roll — re-arming a put once its delta decays below 0.10 — was tried and dropped: once the position is cash-secured its incremental effect is marginal.) **Defaults: `ShortRollDte = 1`, `ShortProfitTarget = 0` (off).**

**In one line:** *a single cash-secured short put — one put at delta ≤ 0.50, ~14 DTE, rolled at expiry, never sold below a 0.20 target.* It leads the broad set (1.77 vs buy-&-hold's 1.21), the decliners, and — at under half the drawdown — edges even the violent cohort (2.75 vs 2.73), while carrying the shallowest drawdown of anything on the page in all four universes. On the concentrated high-flyer basket, though, no overlay beats simply running the engine on the underlying (Cash 5.85 vs the short-put's 4.58).

### The range switch: short puts first, stock + calls when the put cannot reach

**The rule.** The engine's target defines a **tolerance range** `[max(0, tgt − 0.30), tgt + 0.30]` (the rebalance deadband), which doubles as the no-trade zone — the position is only resized when held delta leaves it, and every resize aims at the range **midpoint**.

1. **Short puts are preferred**, capped at **0.50 delta**: sell a cash-secured put at `min(0.50, midpoint)`.
2. **Once the put can no longer express the target** — i.e. the range floor exceeds the put cap, `lo > 0.50`, which means `tgt > 0.80` — **buy the stock** and sell calls against it, capped at **0.50 delta**, to track the target back down.
3. **Exit the stock** when even a full 0.50-delta call cannot bring the position into range: `hi < 1.00 − 0.50`, i.e. `tgt < 0.20`. Then revert to the put program.

Both thresholds are **derived from the two caps, not fitted** — 0.80 in / 0.20 out falls out of `putCap = callCap = 0.50`. That asymmetry is deliberate hysteresis: the position is held through the middle of the range rather than being torn down on every crossing. An earlier version that tore the core down as soon as the put could reach again churned **136 rolls against 57** for a plain short put and made the exit rule unreachable dead code.

**Why the stock leg is necessary.** A 0.50-capped put simply cannot express a target above 0.50, and above 0.80 the range is entirely out of its reach. Something has to carry that delta, and stock is the right instrument: it is **linear (zero gamma), pays no premium decay, never expires, and costs 5bp** against the 1% charged on option legs. A **call LEAP in the same slot is strictly worse** — 0.61 → 0.46 Sharpe — even with no short calls, i.e. even when the LEAP's *long* gamma is working for it; it loses on theta and on deep-ITM roll spreads. (Every earlier LEAP test was miscast as a *replacement* for stock at 0.80 delta rather than as a way to exceed 1.0. Exceeding 1.0 turns out to be worth only **5.8% of engine return pooled, 5.6% median, and is negative on 4 of 19 names** — so stacking LEAPs for leverage is not worth building.)

**Measured** (19-name basket, 1% mid, account-scaled sizing, `putCap = callCap = 0.50`, calls on):

| | basket full history | basket last 30% (OOS) | broad OOS (2,289) |
|---|---|---|---|
| *Cash engine* | *309% / 43.0 / 0.68* | *88% / 33.7 / 0.87* | — |
| 0.50-capped put program alone | 122% / **38.8** / 0.64 | 62% / **26.8** / **1.08** | — |
| **Range switch** | **312% / 44.1 / 0.72** | **100% / 31.7 / 1.05** | **0.552** *(4/4 samples)* |

*(The put-alone column is the same range machinery with `RangePutsOnly` — matched midpoint targeting and 0.50 cap — so the only difference is the stock phase. The standalone `ShortPut` structure, sized differently, reads 113% / 28.6 / 0.66 full history and 62% / 22.6 / 1.21 OOS and remains the broad-universe leader at 0.773.)*

**It beats the cash engine out of sample on all three measures at once — return 13 of 19 names, drawdown 11 of 19, Sharpe 13 of 19** — and it is the only structure here that does. Notably the per-name evidence gets *stronger* out of sample than on full history, the opposite of the usual pattern. IREN **+2095%** full history and **+463%** OOS; ASTS +1340% / +99%; BE +562% / +545%. Against the matched put-alone program it wins return **16 of 19** and Sharpe **13 of 19** on full history, but out of sample only **12 of 19** and **8 of 19** — the edge is concentrated in the older window.

**The honest weak points, and they matter.**

- **Drawdown loses to the short-put on 1 of 19 names full-history and 2 of 19 OOS** (mean 44.3 vs 28.6). This is invariant across every version of the idea tested; taking real stock exposure buys return with drawdown and there is no configuration that avoids it.
- **Sharpe against the short-put is a coin flip** — 9/19 full, 7/19 OOS, means 0.66 vs 0.66 and 1.03 vs 1.21. The short-put's edge is *concentrated*, not broad: it wins decisively on ^GSPC (2.22 vs 1.39), BE (2.75 vs 1.93), GRPN (2.11 vs 1.61) and TSLA (1.18 vs 0.23), and roughly ties elsewhere.
- **Broadly it is well behind the short-put** — 0.552 vs 0.773 across 2,289 names. The case for this structure is the concentrated high-vol basket, not the universe.
- **Turnover is ~341 rolls per name against ~60 for the short-put.** At the ~5% spread real quotes imply, that is roughly 5× the friction, and it is the constant call-rolling rather than the cheap stock leg that pays it. **Untested at 5%** — this is the first thing to check before deploying.
- **Turnover is high and concentrated in the call rolls.** OPEN, the most trend-persistent name, sits **42% of bars in stock with 287 rolls and 16 forced exits** and returns +84% against the put-alone program's +133% — the stock phase still costs it, but it is no longer negative. FIG is the low-turnover mirror case.

> **Corrected 2026-08-01 — a core-sizing defect inflated the earlier figures for this structure.** The core was sized once at establishment and **never re-scaled to the account afterwards**, so as the underlying moved its delta *as a fraction of capital* drifted away from 1.0. Short legs were never affected because they are re-sold at the current scale on every resize. On OPEN — which fell from roughly \$74 to \$0.73 over the run — a core established years earlier had decayed to **0.07 account delta**, which then disabled two rules at once: no call could be sold (`scD = min(cap, coreΔ − mid)` is zero when `coreΔ < mid`) and the unreachable-exit test could never fire (`coreΔ − cap > hi` is never true for a tiny `coreΔ`), so the position sat vestigial and never exited — carrying 0.06–0.28 delta through a **+245% month** (July 2025). The core is now re-scaled to `bankroll / S` at every resize, charged at the traded increment. Effect on the basket: full history **266% → 312%** return and **0.66 → 0.72** Sharpe, time-in-stock **46% → 33%** as exits began firing, OPEN **−21% → +84%**, ASST 8% → 314%, ASTS 884% → 1340%. **Out of sample it barely moved (1.03 → 1.05)** — drift accumulates with elapsed time, so it distorted the deep history far more than the recent tail, which is why the walk-forward conclusions below are unchanged. Same class of defect as the account-scaling correction above: quantities were scaled at *creation* but the core is created once and was never revisited.

**Cap-tuning is exhausted — 0.50/0.50 is the balanced point, not an optimum.** Five configurations spanning caps 0.25–0.50 and hysteresis bands 10 to 60 points wide all land on the same spot out of sample: **basket OOS return 100–108%, drawdown 31.4–32.3, Sharpe 0.99–1.05.** The full-history spread between them (266% / 288% / 356%) that made them look different **collapsed entirely under walk-forward** — and the in-sample ranking *inverts* out of sample (the 0.25 config was 1st in-sample at 0.50 Sharpe and 3rd OOS at 0.99; the short-put was last in-sample at 0.40 and 1st OOS at 1.21). Never read a full-history basket figure from this family without the split.

Two sub-findings worth keeping. **The call cap changes which sub-rule is correct**, so 0.25 is not merely a smaller 0.50: at a 0.50 cap, selling no calls beats selling them, while at 0.25 it reverses decisively (0.488 with calls vs **0.324** without). And **the core phase tracks the stock to within 0.2pp per episode** — there is no options cost in it at all, so when core mode loses it is the *signal* failing, not the structure. Core mode is a **~5-bar momentum bet fired ~50 times per name** at a 34–51% hit rate and ~2:1 payoff; it compounds positively only where the mean episode's stock move clears roughly **+1.5–2%** (IREN +5.5% → +641%, MSTR +2.3% → +62%, OPEN +0.9% → **−41%**, TSLA −0.1% → −20%), because ±15% episode dispersion destroys a small positive mean.

Research-only: `OverlayStrategy.RangePutCall`, default-inert. Knobs `RangePutCap` / `RangeShortCallCap` / `RangeShortCalls` / `RangeCoreEnter` (0 = derive entry from the put cap) / `RangeCoreStock` / `RangeSitOut`, plus the `RangePutsOnly` / `RangePinHalf` / `RangeNoHysteresis` ablations. Every figure here depends on the **account-scaled sizing** correction documented above the tables; the pre-correction numbers for this structure are void.

**Bottom line:** at the tuned defaults (365-DTE LEAP, 14-DTE short legs, flat below a 0.20 target — hold-20 for option cores, close-immediately for stock) and with three model-honesty rules enforced — **all short calls covered 1:1 (no naked calls)**, the **short-put genuinely cash-secured (no put margin)**, and **positions sized as a fraction of the account** (the 2026-07-31 correction above) — the overlays beat buy-&-hold on return-per-drawdown in **three of the four universes**, losing only the violent cohort, where buy-&-hold's mean is carried by a handful of enormous winners no delta-capped structure can follow. **The SPLIT SWITCH (documented at the top of this section) has the highest return in all four cohorts; the short-put is the standout on RISK** *(and subject to the vol-risk-premium caveat at the top of this section — the ranking inverts if options are not systematically rich)*: it carries the shallowest drawdown in every cohort by a wide margin (basket **28.6** vs 47.9–51.6 for every core structure) and is the most cost-stable because it trades one leg. On **return** it no longer leads the basket — corrected sizing puts **covered stock first at +212%** against the short-put's +113% — and that reordering is the single clearest consequence of the sizing fix. Read the short-put as the risk-adjusted pick and covered stock as the participation pick. It is also the **least-deployed** (~0.17–0.19 average exposure, in trade under half the bars) — most of the cushion comes from simply holding less, which is the honest way to read it. The **PMCC structures earn their keep on the broad set** (1.24 / 1.16, both ahead of buy-&-hold) but trail the plain engine on the basket — their delta caps bite hardest exactly where the underlying compounds fastest. Two things to keep honest: this rests on **near-mid execution**, and more importantly on the **14-DTE theta harvest whose front-week gamma / gap / assignment risk the close-to-close Black-Scholes model cannot see** — read the edge as a model ceiling, most trustworthy in the *drawdown reduction* it shows (consistent across every cohort and both windows) rather than in the return figures. Reproduce with `OptionsOverlaySimulator` over `BankrollResult.Positions`.

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

By default the indicator's state table shows only the two **actionable** rows — **`Exposure`** and **`Overlay (next rebal)`**; the eleven diagnostic rows (states, EMAs, counts, anchors, candle type, bias, region) appear only when **Show debug information** is ticked. The **exposure EMA lines are off by default** too, so the chart opens showing the traded exposure and nothing competing with it. The `Overlay (next rebal)` row applies the [split switch](#the-preferred-structure-the-split-switch) to the current traded exposure: **`stock + SHORT CALL 0.28d`** above 0.50, **`SHORT PUT 0.35d`** between 0.20 and 0.50, **`cash / no position`** below — naming the leg to sell explicitly so there is no ambiguity about direction. The table anchors **Bottom Right** with a **10% bottom margin** — Pine has no percentage anchor (only top/middle/bottom), so the margin is a transparent spacer row placed *below* the content using `table.cell`'s `height`, which is a percent of the pane. `Table position` and `Table bottom margin (% of pane)` inputs expose both. The frame and cell borders are off so the spacer draws nothing; the dark per-cell backgrounds carry the visual. The strategy script's reconciliation panel is likewise gated behind its own **Show debug information** box, off by default. It reports the structure to hold **at the next forced rebalance** — when held delta leaves the band or a short leg expires — and is deliberately *not* a signal to switch today. An exposure that drifts across 0.50 while the existing position is still inside the band should be left alone: the band is the only source of hysteresis, and switching on every crossing is what made the rule fail (broad OOS Sharpe **0.466** switching every bar against **0.557** switching only on a forced rebalance). The row shows the actionable delta alongside the structure — the short put's own delta, or the delta of the call to sell against the stock.

---

## Notes on tuning

Stress-tested for overfitting, and the findings shaped the defaults:

- **Parameter tuning does not survive out-of-sample.** Per-symbol grid search *lost* to a fixed global default on held-out data (overfit decay ~1.3 Sharpe). Rolling walk-forward showed no durable *alpha*.
- **The smoothing knobs are second-order.** Sharpe barely moves across a wide range; the current values sit in the ~92nd percentile and are treated as fixed.
- **The real, robust value is drawdown reduction** — consistent out-of-sample and across a full market cycle.
- **Don't tune to a single symbol.** Individual names vary widely around the average; that dispersion is expected noise, not a defect to fit away.
- **Return ÷ drawdown is not comparable across configs that deploy different amounts of capital — score the excess over a flat-haircut *curve* instead.** A flat multiplier that ignores every signal raises the ratio monotonically as it *lowers* exposure: ×0.5 → 0.757, ×0.7 → 0.798, ×0.9 → 0.833, ×1.0 → 0.849 (means, last 30% OOS, 2,289 names), purely because return falls sub-linearly against drawdown. **Sharpe barely moves across that whole range** (0.321 → 0.355), because it is scale-invariant. So the procedure is: run the signal-free multiplier at ~10 levels to build an exposure → ratio curve, then score each candidate as **`ratio − flatRatio(its own exposure)`**, interpolated at the exposure it actually deploys. That single change flips conclusions — the peak-age scaler run everywhere scores **−0.018** (worse than simply holding less) while confined below the KAMA it is **+0.029**, a distinction the raw ratio cannot make. Pair it with an **inverted-signal** control (does the mirror image win too? then it's not signal) and a **walk-forward** on the parameters themselves. This discipline is what rejected HV-conditioning, ER cutoffs, dynamic K, rebalance-drift scaling, and longer drawdown windows, and it is the reason the scaler shipped twice — once wrongly, once with the confinement.

---

## Disclaimer

This is a research backtest, not investment advice. Past performance does not guarantee future results. Backtests use adjusted daily data from Yahoo Finance and idealized fills; live results will differ. Use at your own risk.
