# Exposure Overlay — The Split Switch

A trend model outputs one number per stock each day: a **target exposure**. This is the single rule
that expresses it with options. One decision, taken **only when a rebalance is forced**.

Mechanism, parameters and out-of-sample tables: <https://github.com/jmacri01/StockOdds>

---

## The decision

| target | hold | how |
|---|---|---|
| **> 0.50** | **Covered stock** | Buy stock with the **whole account**, then sell a call of delta **1 − target** against it. At a 0.72 target that is a ~0.28Δ, ~14 DTE short call. Nothing is uncovered. |
| **0.20 – 0.50** | **Cash-secured short put** | Sell one put at **delta = target**, ~14 DTE. Collateral is the **strike**, not delta×spot — so the position is fully funded and the size is whatever the account can actually secure. |
| **< 0.20** | **Nothing** | Hold cash. A sub-0.20 sale is nearly all friction and almost no premium; skipping it cuts time in trade from **88% to 71%** and raises return per unit of deployed capital, at unchanged risk-adjusted return. |

**The whole rule is when you look, not just what you pick.** The decision is re-read **only on a forced
rebalance** — when net delta leaves the **±0.30** band around the target, or a short leg reaches expiry.
A target that drifts across 0.50 while the existing position is still inside the band changes nothing:
leave it alone. The band is the only source of hysteresis, and re-deciding every day instead scored
**0.557 → 0.466** on broad out-of-sample Sharpe. The churn, not the split, was what lost money.

---

## The rules

1. **Read the target, then do nothing.** The model gives a target exposure each day with a rebalance
   band of **±0.30** around it. While net delta sits inside that band there is **no action** — no roll,
   no resize, no switch.

2. **Act only when forced.** Two things force a rebalance: net delta **leaving the band**, or a **short
   leg reaching expiry**. Nothing else. At that moment — and only then — read the target and apply the
   decision above.

3. **Aim at the actual target.** Size to the **target itself**, not the middle of the band. Aiming at the
   midpoint quietly holds ~0.09 more delta than asked for whenever the target is under 0.30, and it makes
   a minimum-exposure rule **impossible** — the midpoint can never fall below 0.15, so any lower floor
   never fires.

4. **Stock, never a LEAP.** Above 0.50 hold **shares**, not a long call standing in for them. Stock is
   linear, never expires, pays no premium and costs a few basis points to trade. A 0.80Δ LEAP core in the
   same slot scored **0.46 Sharpe against stock's 0.61** — even with no calls sold, i.e. even with its long
   gamma working for it. And a long call can expire worthless: every LEAP-core variant reached **−100% on
   at least one name**. Shares cannot do that.

5. **Keep the short legs fresh.** Short legs run **~14 DTE** and roll at expiry — on **time, not profit**.
   Taking 50% profit early churns the position at ~0.30 delta and measured worse. Longer tenors are worse
   still: gamma falls as **1/√T** but so does theta, so reward-to-risk is unchanged while friction rises.

6. **Long or cash — never net short.** A bearish signal means less exposure, or cash, never a net short
   position. Shorts were tested four ways and cash beat every one of them.

---

## What it measured

Last 30% of each name's history — out of sample, never seen by any parameter choice. Mid ~1% fills.
Return % / max drawdown %, mean across names.

| Universe | Split switch | Best alternative | Cash engine | Buy & hold |
|---|---:|---:|---:|---:|
| Broad (2,289) | **+25 / 22.7** | +21 / 17.1 `put` | +19 / 21.8 | +37 / 38.9 |
| Decliners (834) | **−2 / 26.8** | +2 / 19.9 `put` | −8 / 26.1 | −26 / 48.4 |
| Violent (208) | **+106 / 38.9** | +88 / 41.5 `stk` | +88 / 40.1 | +143 / 60.1 |
| Basket (19) | **+349 / 43.9** | +212 / 47.9 `stk` | +309 / 43.0 | +138 / 71.3 |

**Highest return in all four cohorts**, on both the full-history and out-of-sample windows — and the only
overlay that beats the plain cash engine on the violent cohort and on the high-vol basket. On broad
out-of-sample Sharpe across four disjoint samples covering the whole 2,289-name universe it scores
**0.557 against 0.547** for the next-best configuration, winning **4 of 4 samples**, at lower drawdown and
**17 points less time in trade**.

---

## Why each piece is there

| piece | evidence |
|---|---|
| **Stock above 0.50** | A LEAP core scored **0.46** Sharpe against stock's **0.61** even with no calls sold. It loses on theta and deep-ITM roll spreads, and it can go to zero. |
| **Aim at the target** | A band midpoint is a **clamp artifact** — the band's floor can't go below zero, so the midpoint sits above the target whenever the target is low. It also blocks any minimum-exposure rule. |
| **Skip below 0.20** | Time in trade **88% → 71%**, return per unit of deployed time **27.3 → 33.4**, risk-adjusted return unchanged. You stop paying to be tied up in 0.1-delta positions. |
| **Decide only on a rebalance** | Re-reading the split every bar scored **0.466** against **0.557**. Switching on every 0.50 crossing is the single most expensive mistake available here. |

---

## Run it on TradingView

Two **Pine** scripts reproduce the engine bar-for-bar. The **Indicator** plots the target-exposure line and
the **±0.30 rebalance band**, and its table carries an **`Overlay (next rebal)`** row that applies this rule
to the current exposure — reading `stock + SHORT CALL 0.28d`, `SHORT PUT 0.35d`, or `cash / no position`,
with the delta of the leg to sell.

The row is labelled **next rebal** deliberately. It tells you the structure to hold **when a rebalance is
next forced** — it is not a signal to switch today.

Scripts: <https://github.com/jmacri01/StockOdds/tree/main/pine>

---

## What it does not do

**It is not the lowest-risk choice.** A plain 0.50-capped cash-secured put program still owns the
risk-adjusted corner: better return ÷ drawdown on the broad universe (**1.23 vs 1.10**) and on decliners,
and a drawdown **5–6 points shallower in every cohort**. The split switch wins return ÷ drawdown only on
the **violent and basket** cohorts. Choose it for participation; choose the put alone for capital
preservation.

**Turnover is untested at realistic spreads.** It rebalances ~**146 times per name** against ~91 for the
simpler variant, and every figure here assumes fills near the **mid (~1%)**. Live quotes on a high-vol name
imply spreads closer to **5%**, where this is the most exposed variant of the family — and that case has
not been measured.

**The basket number is concentrated.** Most of the +349% advantage comes from a single name. Per-name it is
much closer to a coin flip, and its basket risk-adjusted return is slightly *worse* than the simpler
variant even as its return is better.

**The whole ranking moves with the volatility risk premium.** These tests price options at implied =
1.1 × realized volatility. That assumption is doing real work: the ordering of structures inverts once
options stop being systematically rich.

**Research framework, not investment advice.** Prices are Black-Scholes off realized volatility with
idealized fills on a survivor-heavy universe. The model captures the drawdown-and-participation logic but
**overstates the short-dated premium harvest** — skew, gaps, early assignment and bid/ask are not in it.
Size small, respect the tail, and lean on the drawdown reduction, which is the part that generalizes.
