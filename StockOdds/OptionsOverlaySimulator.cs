using System;
using System.Collections.Generic;
using System.Linq;

namespace StockOdds
{
	// Options-overlay simulator: express the engine's per-bar target exposure through an options
	// structure instead of the underlying, then measure the resulting return stream.
	//
	// MODEL, NOT A VALIDATED TRADING SYSTEM. Prices/marks are Black-Scholes (r = 0, no dividends)
	// with implied vol = trailing realized HV * VolRiskPremium (there is no real options chain in the
	// pipeline). It ignores volatility skew, term structure, early assignment, and liquidity. Results
	// are highly sensitive to SpreadFraction (per-transaction cost as a fraction of premium) — a mid
	// fill is ~0, crossing the full spread is ~0.03. Treat outputs as a directional research estimate.
	//
	// Each bar the structure's net delta is steered toward the engine's target exposure (0..1): short
	// calls reduce delta, short puts add it. A target below FlatEps (0.20) is treated as "flat" -- too weak
	// a signal to express: a coreless structure (short-put) simply holds cash, while a core structure (PMCC,
	// covered stock) HOLDS its small position and closes to cash only after FlatHoldDays consecutive flat bars
	// -- it is never flattened to 0 on the first weak bar (that churns the wide LEAP and misses the snap-backs).
	public enum OverlayStrategy
	{
		Pmcc,         // long call LEAP + short calls only (can reduce delta, never add above the LEAP)
		PmccShortPut, // long call LEAP + short calls (reduce) AND short puts (add). The puts are CASH-SECURED like
		              // every other structure's, funded only from bankroll minus the LEAP's cost, so net delta
		              // reaches only what the account can actually post collateral for (~1.0-1.1), not an
		              // unfunded 1.5. Before that rule this structure's apparent >1 leverage was naked puts.
		ShortPut,     // no core; a single short put at delta = min(target, ShortPutCap); flat when target ~ 0
		CoveredStock, // long shares + short calls
		PmccStrangle, // long call LEAP + an always-on short strangle (1 call + 1 put); the nearer leg is
		              // pinned at StrangleMinDelta, the other floats so net delta hits the target
		SplitStockPut,// regime switch at 0.5: target >= 0.5 -> long stock + covered calls to target;
		              // target < 0.5 -> no stock, a single short put sized to the target
		CallSpread,   // short-dated (ShortDteDays) bull call spread as the core: long call at CallLeapDelta,
		              // short call struck so net delta = target (1x1 vertical, same expiry, rolled monthly)
		PutSpread,    // short-dated (ShortDteDays) bull PUT spread, both legs ~40 DTE: long put at
		              // PutLeapDelta (protection) + short higher-strike put struck so net delta = target
		PmccPutFloor, // PMCC whose short calls are capped at ShortCallCap of delta reduction; any remaining
		              // reduction (at low target) comes from BUYING a put instead of piling on more short calls
		RangePutCall, // RESEARCH: RANGE-DRIVEN put / covered-stock switch. The engine target defines a tolerance
		              // RANGE [max(0, tgt - DeadbandDelta), tgt + DeadbandDelta] which doubles as the no-trade zone:
		              // the position is only resized when held delta leaves it, and every resize aims at the range
		              // MIDPOINT.
		              //   PUT MODE (no core): sell a cash-secured put at delta = min(RangePutCap, midpoint) -- the
		              //     midpoint is the target and 0.5 is only a CAP. Sit out entirely when hi <= RangeSitOut.
		              //   COVERED-STOCK MODE: entered when the range sits entirely above 0.5 (lo > 0.5). Buy the
		              //     stock (delta 1, NO gamma, no premium decay, no expiry roll) and sell calls against it to
		              //     bring net delta to the midpoint, with the short call capped at RangeShortCallCap.
		              // HYSTERESIS is the point: once in covered-stock mode the position is HELD as long as the range
		              // is still REACHABLE, i.e. while 1.0 - RangeShortCallCap <= hi. It is liquidated (reverting to
		              // put mode) only when the range drops out of reach -- the "0-0.3 target while long stock"
		              // case, where a 0.5-delta short call can only cut to 0.5 and cannot reach 0.3. An earlier
		              // version tore the core down as soon as 0.5 re-entered the range; that churned (136 rolls vs
		              // 57 for a plain short put) and made the unreachable-exit rule dead code.
		LeapScaled    // delta comes from SIZING the core, not from offsetting it: hold target/CallLeapDelta contracts
		              // of the 0.80-delta LEAP and change the QUANTITY as the target moves. No short calls, no short
		              // puts, no offsetting legs of any kind. Premium paid is capped at the account (no margin).
		              // Motive: every other core structure holds a FULL unit (one LEAP or one share) and neutralises
		              // most of it with short-dated options, so it carries option greeks at full notional while the
		              // engine's equivalent fractional stock position carries none. On a 116-HV name that cost was
		              // ~20 bp/bar regardless of whether the offsetting leg was a short call or a long put.
	}

	public sealed class OverlayResult
	{
		public List<DateTime> Dates { get; } = new();
		public List<double> Returns { get; } = new();   // per-bar fractional returns of the overlay bankroll
		public double SharpeRatio { get; set; }
		public double MaxDrawdownPct { get; set; }
		public double TotalReturnPct { get; set; }
		public int Rolls { get; set; }                   // count of short-leg resizes / liquidations
		public List<double> CoreDeltas { get; } = new();      // account-delta of the core leg carried into the next bar
		public List<double> ShortDeltas { get; } = new();     // account-delta contributed by SHORT legs (signed, <= 0)
		public List<double> Los { get; } = new();             // range low used on the bar
		public List<double> His { get; } = new();             // range high used on the bar
		public List<double> Resized { get; } = new();         // 1 if the position was resized on the bar
		public int RangePutBars { get; set; }    // RangePutCall: bars held in put mode
		public int RangeLeapBars { get; set; }   // RangePutCall: bars held in call-LEAP mode
		public int RangeFlatBars { get; set; }   // RangePutCall: bars sitting out
		public int RangeLeapExits { get; set; }  // RangePutCall: forced liquidations, range unreachable
		public double MaxShortPutCollateralRatio { get; set; } // max over the run of REQUIRED short-put collateral / bankroll, measured BEFORE the cash-secured cap is applied. >1 means the structure wanted leverage a cash account could not fund. Tracked for every structure, not just the standalone short put.
		public double TimeInTradePct { get; set; }       // % of measured bars the structure holds market exposure (|net delta| > 0.05); the rest is idle capital (opportunity cost)
		public double MeanExposure { get; set; }         // average |net delta| across measured bars (0..>1); capital-at-work per dollar
		// ---- per-bar diagnostics, parallel to Returns/Dates. Kept so the gap to the underlying engine can be
		// DECOMPOSED rather than guessed at: exposure tracking (NetDeltas vs the engine's target) / option
		// non-linearity (gross return vs delta x underlying return) / friction. See README "why the overlay
		// gives back on compounders".
		public List<double> NetDeltas { get; } = new();  // net delta held INTO the next bar (signed)
		public List<double> Targets { get; } = new();    // the engine target the overlay was tracking that bar
		public List<double> FrictionFrac { get; } = new(); // that bar's friction as a fraction of bankroll
		// ---- CORE lifecycle diagnostics. A LEAP core is re-bought at the CURRENT spot every time it is
		// established: at the start, after a flat-close to cash, on a NeedsRebuild, and at each expiry roll. On a
		// name that has run, every re-establishment resets the cost basis to a fresh ~0.80-delta strike, so the
		// structure never holds one cheap deep-ITM call through the whole move. These count that.
		public int CoreEstablished { get; set; }         // total times the core was (re)bought
		public int CoreExpiryRolls { get; set; }         // of which: rolled because the LEAP reached expiry
		public int CoreFlatCloses { get; set; }          // of which: re-bought after a flat-close liquidation
		public double CorePremiumPaid { get; set; }      // cumulative core premium paid, in units of the initial bankroll
	}

	public static class OptionsOverlaySimulator
	{
		// ---- configuration (statics, mirroring BankrollSimulator) ----
		public static OverlayStrategy Strategy = OverlayStrategy.Pmcc;
		public static double VolRiskPremium   = 1.10;  // IV = HV * this
		public static double SpreadFraction   = 0.00;  // per-transaction cost as fraction of option premium (mid ≈ 0, full spread ≈ 0.03)
		public static double StockSpreadFrac  = 0.0005;// per-transaction cost for the stock leg
		public static double DeadbandDelta     = 0.30; // resize shorts when |netDelta - target| exceeds this (= engine RebalanceDrift)
		// RESEARCH: a TIGHTER deadband on ST-Bull bars only. Motive -- the delta deadband is what let a stale short
		// call sit through IREN's +38.9% gap on 2023-12-21 (delta gap 0.153 < 0.30, so no resize fired), and the
		// engine's own signal was already at 1.03/1.19 those days. ST-Bull is where those gaps live, so tightening
		// only there buys responsiveness without paying churn in every regime. 0 = use DeadbandDelta everywhere.
		public static double DeadbandStBull    = 0.0;
		// On a resize, aim net delta at target + this*DeadbandDelta: 0 = centre (the engine's exposure), -1 = the
		// lower edge of the tolerance band, +1 = the upper edge.
		// SWEPT AND REJECTED 2026-07-31 -- keep at 0. Aiming high (+0.25 to +0.5) attacks a real defect: the overlay
		// sits BELOW target on 37% of bars, costing ~4 bp/bar on high-move names, and the deadband cannot fix it
		// (tightening the band leaves the tracking term unchanged even frictionless -- the residual is the FlatEps
		// floor and the delta cap, not quantisation). It measured well: broad OOS Sharpe 4/4 on PMCC 0.395 -> 0.419,
		// PMCC+SP 0.460 -> 0.483, covered 0.401 -> 0.433, and it BEAT the matched-exposure flat-boost control, which
		// was strictly worse than doing nothing (0.342 at k1.20 vs 0.419). Walk-forward picked the same region on the
		// first 70% of history. Rejected anyway, for reasons that outrank the Sharpe:
		//   * the gain is a PARTICIPATION TILT concentrated in the steady cohort (0.718 -> 0.786), while the
		//     DECLINERS cohort gets worse (-0.178 -> -0.223, return -7.0 -> -8.5, drawdown 22.7 -> 24.7). Surviving
		//     falling names is this strategy's whole protective case.
		//   * per-name basket drawdown degrades at +0.5 (only 4-5 of 19 shallower) and mean exposure rises 16%.
		//   * its entire mechanism is ADDING delta, which the cash-secured-put invariant now constrains, so the
		//     measurement above predates that rule and would have to be redone before it meant anything.
		// The tracking defect it targeted is real and still open; a fix that does not lever into downtrends would be
		// the thing to look for. Do not re-sweep this knob expecting a different answer.
		public static double RebalanceEdge     = 0.0;
		// CEILING ON NET DELTA. By default this is TIED to the engine's exposure ceiling
		// (MaxExposurePercent / 100) so the overlay can always express what the engine asks for and neither knob
		// silently clips the other. Leaving them as two independent literals is what hid a real bug: the engine was
		// swept up to 300% while this stayed at 1.0, so the overlay discarded every target above 1.0 and the extra
		// exposure showed up as pure drawdown. Note the tie is not licence to lever -- short puts still have to be
		// cash-secured, so the delta actually reached is whatever the account can fund (~1.0-1.1 for a PMCC core).
		// Set TieNetDeltaToEngine = false to use MaxNetDelta as an explicit, independent override.
		public static bool   TieNetDeltaToEngine = true;
		public static double MaxNetDelta       = 1.0;  // only consulted when TieNetDeltaToEngine is false
		private static double EffMaxNetDelta =>
			TieNetDeltaToEngine ? BankrollSimulator.MaxExposurePercent / 100.0 : MaxNetDelta;
		public static double ShortRollDte      = 1;    // roll a trim leg when its remaining DTE <= this (1 = hold to expiry; ShortDteDays/2 = roll at half-life to dodge the final-week gamma/pin ramp)
		public static double ShortProfitTarget = 0.0;  // roll a SHORT leg once it decays to this fraction of its opening premium (0 = off; 0.5 = take 50% profit)
		// EVERY structure's short puts are cash-secured (collateral = strike, netted to the spread width where a
		// long put offsets it, funded only from bankroll MINUS capital already in long legs). See
		// EnforceCashSecured. Setting this false is a RESEARCH ESCAPE HATCH only -- it reproduces the old
		// delta-only fiction in which PMCC + short puts and covered stock reached net delta > 1 on unfunded naked
		// puts. Leave it true for anything you intend to trade.
		public static bool   CashSecuredPut     = true;
		// RangePutCall knobs (see the enum note). RangeSitOut is the range-high below which nothing is held.
		// RangeShortCallCap is the hard ceiling on the short CALL delta in LEAP mode, and it is what makes the
		// range unreachable (forcing an exit) once the LEAP delta runs too far above the target.
		public static double RangeSitOut        = 0.20;   // hold nothing while the range high is at/below this
		public static double RangeShortCallCap  = 0.50;   // ceiling on the short CALL delta in covered-stock mode
		public static double RangePutCap        = 0.50;   // ceiling on the short PUT delta in put mode
		// ABLATIONS, for attribution. PutsOnly never enters covered-stock mode. PinHalf reproduces the older
		// "sell a 0.5-delta put whenever 0.5 sits anywhere in the range" rule instead of aiming at the midpoint.
		// NoHysteresis reproduces the older tear-down-as-soon-as-0.5-re-enters behaviour.
		public static bool   RangePutsOnly      = false;
		public static bool   RangePinHalf       = false;
		public static bool   RangeNoHysteresis  = false;
		// Core INSTRUMENT for the above-0.5 mode. true = long stock (delta 1, no gamma, no premium, no expiry roll).
		// false = a CallLeapDelta call LEAP, rolled at expiry. Kept as a switch so the two can be compared on equal
		// footing: the LEAP core was first measured under a tear-down-on-every-crossing rule that churned, so
		// "the LEAP is bad" and "that rule was bad" were confounded. Note the reachable floors differ -- a 0.80d
		// LEAP can be cut to 0.30 by a 0.50d short call, a 1.00d stock only to 0.50 -- so the LEAP stays reachable
		// (and therefore held) over a WIDER range of targets, which is a real difference, not a nuisance.
		public static bool   RangeCoreStock     = true;
		// NO SHORT CALLS in core mode (default). The core is then pure long delta -- stock is linear (zero gamma),
		// a long LEAP is POSITIVE gamma -- so core mode pays no short-gamma cost at all, and there is nothing to
		// rebalance: establish once, hold, exit. Entry is still "the put program cannot reach the range" (lo > 0.5,
		// the put cap), at which point the core delta is inside the range by construction. EXIT is when the core
		// delta leaves the range from ABOVE (coreDelta > hi) -- the "range dips below 1 while long stock" case.
		// Exit is deliberately one-sided: if coreDelta < lo the target wants MORE delta than the core supplies, and
		// reverting to a 0.5-capped put would be strictly worse and would re-enter on the next bar (churn).
		public static bool   RangeShortCalls    = false;
		// ENTER core mode as soon as delta 1.0 is INSIDE the range, i.e. hi >= RangeCoreEnter (1.0), which with a
		// 0.30 band means target >= 0.70. Core mode is PREFERRED over the put program. Because the exit test is
		// "core delta leaves the range from above" (1.0 > hi), entry and exit share the same boundary and there is
		// no hysteresis gap.
		public static double RangeCoreEnter     = 1.00;
		// ACCOUNT-SCALED SIZING. The overlay otherwise sizes legs in PER-SHARE units (bankroll starts at S, legs are
		// quantities on one share) and never re-scales them as bankroll compounds -- so a position of "delta 1.0" is
		// really S/bankroll of the ACCOUNT, and that ratio drifts without limit. Measured on IREN it fell to 0.193
		// in 2023, meaning core mode "at delta 1.0" was running ~0.19 real exposure; that single artifact accounted
		// for 24,329 bp of a 23,417 bp shortfall vs the cash engine, dwarfing the delta cap (2,128) and swamping a
		// convexity term that was actually POSITIVE (-3,040). With this on, quantities are multiplied by
		// bankroll/S at every establish/resize so held delta is a true fraction of the account, matching the
		// engine's own semantics (engine return = position * bar return).
		// DEFAULT TRUE as of 2026-07-31. Set false to reproduce every overlay number published before that date --
		// but those numbers are distorted, not merely differently scaled: the drift is name-specific and runs in
		// BOTH directions, so it changed the RANKING of structures, not just their level.
		public static bool   AccountScaledSizing = true;
		public static double ShortDteDays      = 14;   // calendar DTE for the rolled short legs. Shorter harvests
		                                               // more theta (universal across strategies, robust to 2% spread);
		                                               // ~14 is the sweet spot — below it you mostly add gamma/gap risk.
		public static double LeapDteDays        = 365;  // calendar DTE for the long LEAP core (rolled at expiry)
		public static double ShortLegDelta     = 0.30; // delta magnitude at which short calls/puts are sold
		public static double ShortCallCap      = 0.50; // PmccPutFloor: cap on the delta reduction from short calls; remainder via a long put
		// CoveredStock COLLAR: reduce the stock's delta via a collar (1 long put + 1 short call) instead of piling on
		// short calls. Long put delta = min(reduction, CoveredPutDelta); short call delta = remainder. At target 0 the
		// two sum to 1.0 (full hedge). CoveredCollar=false keeps the legacy multi-short-call reduction.
		public static bool   CoveredCollar   = false;
		public static double CoveredPutDelta = 0.50;
		public static double CallLeapDelta      = 0.80;  // recommended PMCC starter: 0.80-delta, 365-DTE call
		public static double PutLeapDelta       = 0.15;  // shallow far-OTM base put (straddle put leg / put-diagonal base)
		public static double StrangleMinDelta   = 0.25;  // PmccStrangle: the always-on nearer leg's delta floor
		public static double ShortPutCap        = 0.50; // ShortPut: cap the short put at ~ATM (0.50Δ = peak theta, least directional risk). Deeper puts harvest less theta and carry more downside — 0.50 dominates 0.75/0.95 on every universe.
		public static double ShortPutTargetFrac = 1.0;  // ShortPut: fraction of the engine target to express (put delta = min(frac*target, cap)); 0.5 = run at half exposure
		public static double ShortPutProtDelta  = 0.0;  // ShortPut: if >0, buy a long put at this delta (same expiry) -> bull put spread; the short put deepens by this so net delta still = the cap
		public static double FlatEps            = 0.20; // target <= this is treated as "flat" -- don't express weak signals.
		// 0.20 (swept optimum) beats 0.05: a sub-0.20 target isn't worth expressing. For the SHORT-PUT this is a pure
		// EXPRESSION FLOOR (it simply won't sell a put below a 0.20 target -- holds cash; the timer below is moot since
		// it carries no core). For the LEAP/stock structures it means HOLD the small position while flat, then close
		// after FlatHoldDays -- do NOT flatten a core to cash on the first weak bar (that churns the wide LEAP and
		// misses the post-dip bounces: PMCC broad ratio collapses 1.09 -> 0.57 at hold0). 0.30 overshoots (over-cuts
		// participation). Net across structures: short-put +all cohorts, PMCC basket up, covered-stock mild +,
		// PMCC+short-puts slightly worse on broad/decliners (the participation structure dislikes the trim).
		// Behaviour at "flat". FlatHoldDays: -1 = hold indefinitely (hold/hedge, never close); 0 = close to cash on the
		// first flat bar (harmful for core structures -- see above); N = hold for N consecutive flat bars, then close.
		// 20 is the sweet spot: ≈ pure hold on every universe while keeping the position finite.
		public static int    FlatHoldDays       = 20;
		// STRUCTURE-SPECIFIC OVERRIDE: covered stock closes on the FIRST flat bar (0), not after 20.
		// The "hold, don't flatten" rule was derived on the PMCC and over-generalised to stock. A PMCC core is
		// a wide-spread call LEAP with convexity: churning it is expensive and holding it through a weak stretch
		// captures the snap-backs (FlatHoldDays = 0 craters PMCC's broad ratio 1.09 -> 0.57). Covered stock's
		// core is SHARES -- ~5bp to trade, no gamma -- so holding it while the signal is weak just absorbs
		// drawdown for nothing. Measured on 2,289 names, 4 disjoint samples, with the drawdown-recovery scaler:
		// closing immediately beats holding on BOTH the in-sample head (ret/DD 0.04 -> 0.20) and the held-out
		// tail (0.24 -> 0.32), and lifts the high-vol basket 0.72 -> 1.15. Set to 20 to restore the old shared
		// behaviour. (This was found while diagnosing why the scaler cost the overlays more than the underlying:
		// the scaler lengthens runs of weak-signal bars, so forced core closes rose 1.86 -> 2.71 per name.)
		public static int    CoveredFlatHoldDays = 0;

		// the flat-close timer actually in force for the configured structure
		private static int EffFlatHoldDays =>
			Strategy == OverlayStrategy.CoveredStock ? CoveredFlatHoldDays : FlatHoldDays;
		public static int    HvWindow           = 60;   // trailing bars for realized-vol estimate
		public static double HvFloor            = 0.08; // floor on annualized HV

		// Core = the persistent LEAP/stock (from EstablishCore); everything else is a trim leg (from ResizeShorts),
		// including any long "remainder" put. Core legs roll at their own expiry; trim legs are rebuilt on resize.
		private sealed class Leg { public bool Call; public bool Stock; public bool Core; public double Qty; public double K; public DateTime Exp; public double VPrev; public double VOpen; }

		// Run the overlay against a completed engine result over the [startDate, end] window.
		// engine.Positions[k] is the target exposure on the bar dated engine.ReturnDates[k].
		public static OverlayResult Run(IReadOnlyList<OhlcBar> bars, BankrollResult engine, DateTime startDate)
		{
			var res = new OverlayResult();
			if (bars == null || bars.Count < HvWindow + 2 || engine.Positions.Count == 0) return res;

			// close + trailing HV indexed by date
			var closeByDate = new Dictionary<DateTime, double>();
			foreach (var b in bars) closeByDate[b.Date] = b.Close;
			var hvByDate = TrailingHv(bars);

			double bankroll = 0; bool started = false; var legs = new List<Leg>(); int flatCount = 0;
			bool closedToCash = false;   // the last liquidation was a flat-close, so the next establish is a re-buy
			int nBars = 0, nInTrade = 0; double sumExp = 0;   // time-in-trade / mean-exposure accounting (opportunity cost)
			for (int k = 0; k < engine.Positions.Count && k < engine.ReturnDates.Count; k++)
			{
				DateTime date = engine.ReturnDates[k];
				if (date < startDate) continue;
				if (!closeByDate.TryGetValue(date, out double S) || S <= 0) continue;
				if (!hvByDate.TryGetValue(date, out double hv) || double.IsNaN(hv)) continue;
				double iv = hv * VolRiskPremium;
				double target = engine.Positions[k];
				if (double.IsNaN(target)) continue;

				double friction = 0, pnl = 0;
				if (!started) { bankroll = S; started = true; }
				// ACCOUNT SCALE. Legs are quantities on ONE share, so a per-share delta D earns D*(S/bankroll)*u on the
				// account. Multiplying every quantity by acct = bankroll/S (and converting account-space targets into
				// per-share space the same way) makes held delta a true fraction of the account. acct = 1 reproduces the
				// old per-share sizing exactly, which is why AccountScaledSizing defaults to false.
				double rngLo = double.NaN, rngHi = double.NaN; bool rngResized = false;
				double acct = AccountScaledSizing && S > 1e-9 ? bankroll / S : 1.0;
				if (acct <= 1e-9 || double.IsNaN(acct) || double.IsInfinity(acct)) acct = 1.0;
				else foreach (var l in legs) { double v = LegValue(l, S, iv, date); if (double.IsNaN(v) || double.IsInfinity(v)) v = l.VPrev; pnl += l.Qty * (v - l.VPrev); l.VPrev = v; }

				bool flat = target <= FlatEps;
				if (flat) flatCount++; else flatCount = 0;
				bool doClose = flat && EffFlatHoldDays >= 0 && flatCount > EffFlatHoldDays;
				if (Strategy == OverlayStrategy.RangePutCall)
				{
					double bandR = (DeadbandStBull > 0 && k < engine.StState.Count && engine.StState[k] == ShortTermState.Bull)
						? DeadbandStBull : DeadbandDelta;
					double lo = Math.Max(0.0, target - bandR);
					double hi = Math.Min(EffMaxNetDelta, target + bandR);
					double mid = 0.5 * (lo + hi);
					// per-share delta -> ACCOUNT delta. scale = bankroll/S, so a leg quantity of `scale` shares is
					// exactly 100% of the account. Dividing per-share delta by scale gives the account fraction.
					double scale = AccountScaledSizing && S > 1e-9 ? bankroll / S : 1.0;
					if (scale <= 1e-9 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
					var core = legs.FirstOrDefault(l => l.Core);
					double coreD = core != null ? core.Qty * LegDelta(core, S, iv, date) / scale : 0.0;
					double held = legs.Sum(l => l.Qty * LegDelta(l, S, iv, date)) / scale;

					// With short calls, the lowest net delta a held core can be cut to is coreD - cap; without them
					// the core delta itself must sit in the range. Either way the position is abandoned when the
					// range moves out of reach ABOVE the position (see RangeShortCalls).
					double reachFloor = coreD - (RangeShortCalls ? RangeShortCallCap : 0.0);
					bool unreachable = core != null && reachFloor > hi + 1e-9;
					bool sitOut = core == null && hi <= RangeSitOut;

					if (sitOut || unreachable)
					{
						if (legs.Count > 0)
						{
							foreach (var l in legs) friction += Cost(l, l.VPrev);
							legs.Clear(); res.Rolls++;
							if (unreachable) res.RangeLeapExits++;
							core = null; coreD = 0; held = 0;
						}
					}
					else
					{
						// ENTER covered-stock mode when the range sits entirely above 0.5. HOLD it thereafter while
						// it stays reachable (checked above) -- that hysteresis is what keeps turnover down.
						// ENTRY. RangeCoreEnter > 0: enter as soon as that delta sits inside the range (hi >= it).
						// RangeCoreEnter == 0: enter when the PUT PROGRAM CAN NO LONGER REACH the range, i.e. lo exceeds the
						// put cap. That derives the threshold from the cap instead of making it a free parameter, and it pairs
						// with the exit test (reachFloor > hi) to give real hysteresis: with putCap 0.25 and callCap 0.25 entry
						// fires at tgt > 0.55 and exit at tgt < 0.45, so the exit rule is live rather than preempted.
						bool wantCore = !RangePutsOnly && (RangeCoreEnter > 0
							? hi >= RangeCoreEnter - 1e-9
							: lo > RangePutCap + 1e-9);
						if (wantCore && core == null)
						{
							foreach (var l in legs) friction += Cost(l, l.VPrev);
							legs.Clear();
							var cc = RangeCoreStock
								? new Leg { Core = true, Stock = true, Qty = scale, Exp = DateTime.MaxValue }
								: new Leg { Core = true, Call = true, Qty = scale,
									K = StrikeForDelta(true, S, iv, LeapDteDays / 365.0, CallLeapDelta), Exp = date.AddDays(LeapDteDays) };
							cc.VPrev = LegValue(cc, S, iv, date); friction += Cost(cc, cc.VPrev);
							legs.Add(cc); core = cc; res.CoreEstablished++; res.Rolls++;
							if (!cc.Stock) res.CorePremiumPaid += cc.Qty * cc.VPrev / Math.Max(1e-9, bankroll);
							coreD = cc.Qty * LegDelta(cc, S, iv, date) / scale; held = coreD;
						}
						else if (core != null && !core.Stock && (core.Exp - date).TotalDays <= 1)
						{
							// option core only: roll one day before expiry
							foreach (var l in legs.Where(l => l.Core)) friction += Cost(l, l.VPrev);
							legs.RemoveAll(l => l.Core);
							var cc = new Leg { Core = true, Call = true, Qty = scale,
								K = StrikeForDelta(true, S, iv, LeapDteDays / 365.0, CallLeapDelta), Exp = date.AddDays(LeapDteDays) };
							cc.VPrev = LegValue(cc, S, iv, date); friction += Cost(cc, cc.VPrev);
							legs.Add(cc); core = cc; res.CoreEstablished++; res.CoreExpiryRolls++;
							res.CorePremiumPaid += cc.Qty * cc.VPrev / Math.Max(1e-9, bankroll);
							coreD = cc.Qty * LegDelta(cc, S, iv, date) / scale;
							held = legs.Sum(l => l.Qty * LegDelta(l, S, iv, date)) / scale;
						}
						else if (RangeNoHysteresis && !wantCore && core != null)
						{
							foreach (var l in legs) friction += Cost(l, l.VPrev);
							legs.Clear(); core = null; coreD = 0; held = 0; res.Rolls++;
						}

						bool shortExp = legs.Any(l => !l.Core && l.Exp != DateTime.MaxValue && (l.Exp - date).TotalDays <= ShortRollDte);
						// only meaningful when something can actually be traded to fix it
						bool outside = (core == null || RangeShortCalls) && (held < lo - 1e-9 || held > hi + 1e-9);
						// under the no-short-calls rule a core-only position is COMPLETE, so "nothing sold" must not
						// count as a reason to resize -- otherwise it would re-enter the resize block every bar
						bool nothingSold = !legs.Any(l => !l.Core) && (core == null || RangeShortCalls);
						if (outside || shortExp || nothingSold)
						{
							foreach (var l in legs.Where(l => !l.Core)) friction += Cost(l, l.VPrev);
							legs.RemoveAll(l => !l.Core);
							// RE-SCALE THE CORE TO THE ACCOUNT. The core is established once; its quantity is fixed at that
							// bar's bankroll/S, so as the price moves its delta AS A FRACTION OF THE ACCOUNT drifts. On OPEN
							// (which fell ~74 -> 0.73 and never exited) it decayed to 0.07, so no call was ever sold
							// (coreD < mid) and the exit test could never fire -- the position sat vestigial through a 245%
							// monthly rally. Short legs never had this problem because they are re-sold at the current scale
							// on every resize. Charged at the traded increment.
							if (core != null)
							{
								double wantQty = acct;
								if (Math.Abs(wantQty - core.Qty) > 1e-9)
								{
									double cv = LegValue(core, S, iv, date);
									friction += (core.Stock ? StockSpreadFrac : SpreadFraction) * Math.Abs(wantQty - core.Qty) * Math.Max(0, cv);
									core.Qty = wantQty; core.VPrev = cv;
									coreD = core.Qty * LegDelta(core, S, iv, date) / acct;
								}
							}
							double Ts = ShortDteDays / 365.0; var expS = date.AddDays(ShortDteDays);
							if (core != null)
							{
								// sell calls against the core toward the MIDPOINT, capped -- skipped entirely under
								// the no-short-calls rule, which leaves core mode as pure long delta
								double scD = RangeShortCalls
									? Math.Min(RangeShortCallCap, Math.Max(0.0, coreD - mid)) : 0.0;
								if (scD > 1e-3)
									legs.Add(new Leg { Call = true, Qty = -scale, K = StrikeForDelta(true, S, iv, Ts, scD), Exp = expS });
							}
							else
							{
								// PUT MODE: aim at the midpoint, with 0.5 as a cap (not a target)
								double pd = RangePinHalf && lo <= 0.5 + 1e-9 && hi >= 0.5 - 1e-9
									? 0.5 : Math.Min(RangePutCap, mid);
								if (pd > 1e-3)
									legs.Add(new Leg { Call = false, Qty = -scale, K = StrikeForDelta(false, S, iv, Ts, pd), Exp = expS });
							}
							EnforceNoNakedCalls(legs);
							double cr = EnforceCashSecured(legs, S, iv, bankroll, date);
							if (cr > res.MaxShortPutCollateralRatio) res.MaxShortPutCollateralRatio = cr;
							foreach (var l in legs.Where(l => !l.Core)) { l.VPrev = LegValue(l, S, iv, date); l.VOpen = l.VPrev; friction += Cost(l, l.VPrev); }
							res.Rolls++; rngResized = true;
						}
					}
					rngLo = lo; rngHi = hi;
					if (legs.Count == 0) res.RangeFlatBars++;
					else if (legs.Any(l => l.Core)) res.RangeLeapBars++;
					else res.RangePutBars++;
				}
				else if (doClose)
				{
					if (legs.Count > 0) { foreach (var l in legs) friction += Cost(l, l.VPrev); legs.Clear(); res.Rolls++; closedToCash = true; }
				}
				else
				{
					if (HasCore(Strategy) && !legs.Any(l => l.Core))
					{ EstablishCore(legs, S, iv, date); foreach (var l in legs.Where(l => l.Core)) { l.Qty *= acct; l.VPrev = LegValue(l, S, iv, date); friction += Cost(l, l.VPrev); }
					  res.CoreEstablished++; if (closedToCash) { res.CoreFlatCloses++; closedToCash = false; }
					  res.CorePremiumPaid += legs.Where(l => l.Core && !l.Stock).Sum(l => l.Qty * l.VPrev) / Math.Max(1e-9, bankroll); }

					// roll the (option) LEAP core one day before expiry
					if (legs.Any(l => l.Core && !l.Stock && (l.Exp - date).TotalDays <= 1))
					{
						foreach (var l in legs.Where(l => l.Core && !l.Stock)) friction += Cost(l, l.VPrev);
						legs.RemoveAll(l => l.Core && !l.Stock); EstablishCore(legs, S, iv, date);
						foreach (var l in legs.Where(l => l.Core && !l.Stock)) { l.Qty *= acct; l.VPrev = LegValue(l, S, iv, date); friction += Cost(l, l.VPrev); }
						res.CoreEstablished++; res.CoreExpiryRolls++;
						res.CorePremiumPaid += legs.Where(l => l.Core && !l.Stock).Sum(l => l.Qty * l.VPrev) / Math.Max(1e-9, bankroll);
						// a core roll changes committed capital, so re-test the collateral of any live short puts
						EnforceCashSecured(legs, S, iv, bankroll, date);
					}

					double net = legs.Sum(l => l.Qty * LegDelta(l, S, iv, date));
					double spTgt = Math.Min(target * ShortPutTargetFrac, ShortPutCap);
				double tnet = Strategy == OverlayStrategy.ShortPut ? (spTgt > FlatEps ? spTgt : 0.0) : target;
					double band = (DeadbandStBull > 0 && k < engine.StState.Count && engine.StState[k] == ShortTermState.Bull)
						? DeadbandStBull : DeadbandDelta;
					bool shortExpiring = legs.Any(l => !l.Core && (l.Exp - date).TotalDays <= ShortRollDte);
						bool profitHit = ShortProfitTarget > 0 && legs.Any(l => l.Qty < 0 && l.VOpen > 1e-9 && l.VPrev <= ShortProfitTarget * l.VOpen);
					double tnetPS = tnet * acct, bandPS = band * acct;
					if (Strategy == OverlayStrategy.LeapScaled)
					{
						// size the core to the target; charge the spread only on the traded increment
						var coreS = legs.FirstOrDefault(l => l.Core && l.Call);
						if (coreS != null && Math.Abs(net - tnetPS) > bandPS)
						{
							double dS = LegDelta(coreS, S, iv, date);
							double vS = LegValue(coreS, S, iv, date);
							double want = dS > 1e-6 ? Math.Max(0.0, Math.Min(EffMaxNetDelta, tnet)) * acct / dS : 0.0;
							// NO MARGIN: the premium held cannot exceed the account
							if (vS > 1e-9 && want * vS > bankroll) want = bankroll / vS;
							friction += SpreadFraction * Math.Abs(want - coreS.Qty) * Math.Max(0, vS);
							coreS.Qty = want; coreS.VPrev = vS;
							res.Rolls++;
						}
					}
					else if (Math.Abs(net - tnetPS) > bandPS || shortExpiring || profitHit  || NeedsRebuild(legs, target))
					{
						foreach (var l in legs.Where(l => !l.Core)) friction += Cost(l, l.VPrev);
						// ResizeShorts works in per-share delta space, so the account target is scaled in
						ResizeShorts(legs, S, iv, Math.Max(0.0, Math.Min(EffMaxNetDelta, target + RebalanceEdge * band)) * acct, date);
						// the two invariants, applied to EVERY structure (see the definitions above)
						EnforceNoNakedCalls(legs);
						double collRatio = EnforceCashSecured(legs, S, iv, bankroll, date);
						if (collRatio > res.MaxShortPutCollateralRatio) res.MaxShortPutCollateralRatio = collRatio;
						foreach (var l in legs.Where(l => !l.Core)) { l.VPrev = LegValue(l, S, iv, date); l.VOpen = l.VPrev; friction += Cost(l, l.VPrev); }
						res.Rolls++;
					}
				}

				// ruin (the diagnostic lists are kept the same length as Returns so they can be zipped)
				if (bankroll <= 1e-6) { res.Returns.Add(-1.0); res.Dates.Add(date); res.FrictionFrac.Add(0); res.NetDeltas.Add(0); res.Targets.Add(0); res.CoreDeltas.Add(0); res.ShortDeltas.Add(0); res.Los.Add(double.NaN); res.His.Add(double.NaN); res.Resized.Add(0); break; }
				double netPnl = pnl - friction;
				double dr = netPnl / bankroll; if (double.IsNaN(dr) || double.IsInfinity(dr)) dr = 0;
				res.FrictionFrac.Add(bankroll > 1e-9 ? friction / bankroll : 0.0);
				bankroll += netPnl; res.Returns.Add(dr); res.Dates.Add(date);
					// time-in-trade / mean-exposure: |net delta| of the position held into the next bar
					double curNet = Math.Abs(legs.Sum(l => l.Qty * LegDelta(l, S, iv, date))) / acct;
					nBars++; if (curNet > 0.05) nInTrade++; sumExp += curNet;
					res.NetDeltas.Add(curNet); res.Targets.Add(target);
					res.CoreDeltas.Add(legs.Where(l => l.Core).Sum(l => l.Qty * LegDelta(l, S, iv, date)) / acct);
					res.ShortDeltas.Add(legs.Where(l => !l.Core).Sum(l => l.Qty * LegDelta(l, S, iv, date)) / acct);
					res.Los.Add(rngLo); res.His.Add(rngHi); res.Resized.Add(rngResized ? 1 : 0);
			}

			res.SharpeRatio = Sharpe(res.Returns);
			res.MaxDrawdownPct = MaxDrawdown(res.Returns);
			res.TotalReturnPct = TotalReturn(res.Returns);
			res.TimeInTradePct = nBars > 0 ? 100.0 * nInTrade / nBars : 0;
			res.MeanExposure = nBars > 0 ? sumExp / nBars : 0;
			return res;
		}

		private static bool HasCore(OverlayStrategy s) => s != OverlayStrategy.ShortPut && s != OverlayStrategy.SplitStockPut && s != OverlayStrategy.RangePutCall;

		// SplitStockPut only: the structure must be rebuilt when the target crosses the 0.5 regime line
		// (the delta deadband alone is too wide to catch it).
		private static bool NeedsRebuild(List<Leg> legs, double target)
		{
			if (Strategy == OverlayStrategy.ShortPut)
			{
				// coreless: build the put when we want exposure and have none; drop it when the target goes flat.
				// (independent of the deadband, so a wide band can't strand it un-built or stuck-on.)
				bool hasP = legs.Any(l => !l.Core);
				double t = Math.Min(target * ShortPutTargetFrac, ShortPutCap);
				return t > FlatEps ? !hasP : hasP;
			}
			if (Strategy != OverlayStrategy.SplitStockPut) return false;
			bool hasStock = legs.Any(l => l.Stock);
			bool hasPut = legs.Any(l => l.Qty < 0 && !l.Call);
			if (target >= 0.5) return !hasStock;              // want stock regime
			if (target > FlatEps) return hasStock || !hasPut; // want put regime
			return legs.Count > 0;                            // want flat
		}

		private static void EstablishCore(List<Leg> legs, double S, double iv, DateTime now)
		{
			double T = LeapDteDays / 365.0; var exp = now.AddDays(LeapDteDays);
			switch (Strategy)
			{
				case OverlayStrategy.Pmcc:
				case OverlayStrategy.PmccShortPut:
				case OverlayStrategy.PmccStrangle:
				case OverlayStrategy.PmccPutFloor:
				case OverlayStrategy.LeapScaled:
					legs.Add(new Leg { Core = true, Call = true, Qty = 1, K = StrikeForDelta(true, S, iv, T, CallLeapDelta), Exp = exp });
					break;
				case OverlayStrategy.ShortPut:
					break;
				case OverlayStrategy.CoveredStock:
					legs.Add(new Leg { Core = true, Stock = true, Qty = 1, Exp = now.AddYears(100) });
					break;
				case OverlayStrategy.CallSpread: {
					double Tc = ShortDteDays / 365.0; var expc = now.AddDays(ShortDteDays);
					legs.Add(new Leg { Core = true, Call = true, Qty = 1, K = StrikeForDelta(true, S, iv, Tc, CallLeapDelta), Exp = expc });
					break; }
				case OverlayStrategy.PutSpread: {
					double Tp = ShortDteDays / 365.0; var expp = now.AddDays(ShortDteDays);
					legs.Add(new Leg { Core = true, Call = false, Qty = 1, K = StrikeForDelta(false, S, iv, Tp, PutLeapDelta), Exp = expp });
					break; }
			}
		}

		// NO NAKED CALLS: reduce delta by `reduce` (>0) using at most ONE short call (covered 1:1 by the long core),
		// routing any remainder beyond one call's ~0.95 delta into a LONG PUT instead of a second (naked) short call.
		private static void AddCoveredShortCall(List<Leg> legs, double reduce, double S, double iv, double Ts, DateTime exp)
		{
			if (reduce <= 1e-4) return;
			double cd = Math.Min(reduce, 0.95);
			legs.Add(new Leg { Call = true, Qty = -1, K = StrikeForDelta(true, S, iv, Ts, cd), Exp = exp });
			double rem = reduce - cd;
			if (rem > 1e-3) legs.Add(new Leg { Call = false, Qty = 1, K = StrikeForDelta(false, S, iv, Ts, Math.Min(0.95, rem)), Exp = exp });
		}
		// ================= HARD INVARIANT 1: EVERY SHORT PUT IS CASH-SECURED, IN EVERY STRUCTURE =================
		// A short put must post its collateral in cash. Available cash is the bankroll MINUS capital already
		// committed to long legs (shares at spot, long options at market value) -- money spent on a call LEAP or on
		// stock cannot simultaneously secure a put. Collateral per short put is its STRIKE, netted down to the
		// spread width when a long put at a lower strike and the same expiry offsets it (a defined-risk vertical
		// genuinely needs only the difference). If the requirement exceeds available cash, every put leg in that
		// expiry is scaled down pro rata -- short and long together, so a spread keeps its shape.
		//
		// This used to be applied ONLY to the standalone ShortPut structure, which meant PMCC + short puts and
		// covered stock reached net delta > 1 with NAKED, UNFUNDED puts. That is what let them appear to improve
		// monotonically as the delta ceiling rose: the "leverage" was free money the model never had to fund.
		//
		// Accounting caveat: the overlay is a P&L/delta model, not a full cash-flow ledger (bankroll accrues
		// Sum qty*dV rather than tracking settlement), so "capital committed" is long legs at market value. That is
		// the right first-order test for whether a put could actually be secured; it is not a margin engine.
		// Returns the PRE-CAP collateral-to-bankroll ratio, for diagnostics.
		private static double EnforceCashSecured(List<Leg> legs, double S, double iv, double bankroll, DateTime now)
		{
			if (bankroll <= 1e-9) return 0;
			double longCapital = legs.Where(l => l.Qty > 0).Sum(l => l.Qty * (l.Stock ? S : LegValue(l, S, iv, now)));
			double available = Math.Max(0.0, bankroll - longCapital);

			double required = 0;
			foreach (var grp in legs.Where(l => !l.Call && !l.Stock).GroupBy(l => l.Exp))
			{
				double shortColl = grp.Where(l => l.Qty < 0).Sum(l => -l.Qty * l.K);
				double longOffset = grp.Where(l => l.Qty > 0).Sum(l => l.Qty * l.K);
				required += Math.Max(0.0, shortColl - longOffset);
			}
			if (required <= 1e-9) return 0;
			double ratio = required / bankroll;
			if (!CashSecuredPut || required <= available) return ratio;

			double f = available / required;                 // 0 when every dollar is already committed
			foreach (var l in legs.Where(l => !l.Call && !l.Stock).ToList()) l.Qty *= f;
			legs.RemoveAll(l => !l.Call && !l.Stock && Math.Abs(l.Qty) < 1e-6);
			return ratio;
		}

		// ================= HARD INVARIANT 2: NO NAKED SHORT CALLS, IN EVERY STRUCTURE =================
		// Short calls may never exceed the long core that covers them 1:1 (one LEAP or one share unit per call).
		// Every branch below is written to satisfy this (a single Qty = -1 call against a Qty = 1 core, with any
		// further reduction expressed as a LONG PUT), but it is clamped here as well so a future edit cannot
		// silently reintroduce the ~25-30% phantom theta that stacked naked calls used to harvest.
		private static void EnforceNoNakedCalls(List<Leg> legs)
		{
			double cover = legs.Where(l => l.Qty > 0 && (l.Call || l.Stock)).Sum(l => l.Qty);
			double shortCalls = legs.Where(l => l.Call && l.Qty < 0).Sum(l => -l.Qty);
			if (shortCalls <= cover + 1e-9) return;
			double f = cover / shortCalls;                   // 0 cover -> no short calls at all
			foreach (var l in legs.Where(l => l.Call && l.Qty < 0).ToList()) l.Qty *= f;
			legs.RemoveAll(l => l.Call && l.Qty < 0 && Math.Abs(l.Qty) < 1e-6);
		}

		private static void ResizeShorts(List<Leg> legs, double S, double iv, double target, DateTime now)
		{
			legs.RemoveAll(l => !l.Core); // drop all trim legs (short calls/puts + any remainder long put); keep the core
			double Ts = ShortDteDays / 365.0; var exp = now.AddDays(ShortDteDays);
			if (Strategy == OverlayStrategy.PmccPutFloor)
			{
				// PMCC where the short calls are capped at ShortCallCap of delta reduction; the rest of the
				// reduction (when target is low) comes from BUYING a put (long put = negative delta hedge).
				var coreL = legs.FirstOrDefault(l => l.Core && l.Call);
				if (coreL == null) return;
				double coreD = coreL.Qty * LegDelta(coreL, S, iv, now);
				double reduction = coreD - target;            // >0 when we must cut delta down to target
				if (reduction <= 1e-4) return;                // target >= core delta: hold the call alone (capped)
				double scRed = Math.Min(reduction, ShortCallCap);
				legs.Add(new Leg { Call = true, Qty = -1, K = StrikeForDelta(true, S, iv, Ts, Math.Min(0.95, scRed)), Exp = exp }); // ONE covered short call (delta=scRed)
				double putRed = reduction - scRed;            // remainder handled by a long put
				if (putRed > 1e-3) legs.Add(new Leg { Call = false, Qty = 1, K = StrikeForDelta(false, S, iv, Ts, Math.Min(0.90, putRed)), Exp = exp });
				return;
			}
			if (Strategy == OverlayStrategy.ShortPut)
			{
				double tgt = Math.Min(target * ShortPutTargetFrac, ShortPutCap); // net delta target (e.g. 0.50 cap)
				if (tgt > FlatEps)
				{
					if (ShortPutProtDelta > 0)
					{
						// bull put spread, both legs same (short) expiry: short deeper put + long protective put, net = tgt.
						legs.Add(new Leg { Call = false, Qty = -1, K = StrikeForDelta(false, S, iv, Ts, Math.Min(0.95, tgt + ShortPutProtDelta)), Exp = exp });
						legs.Add(new Leg { Call = false, Qty = 1,  K = StrikeForDelta(false, S, iv, Ts, ShortPutProtDelta), Exp = exp });
					}
					else legs.Add(new Leg { Call = false, Qty = -1, K = StrikeForDelta(false, S, iv, Ts, tgt), Exp = exp });
				}
				return;
			}
			if (Strategy == OverlayStrategy.SplitStockPut)
			{
				legs.Clear(); // rebuild the whole structure (stock churns only at the 0.5 crossing / deadband)
				if (target >= 0.5)
				{
					legs.Add(new Leg { Stock = true, Qty = 1, Exp = now.AddYears(100), VPrev = S });
					double need2 = target - 1.0; // stock delta 1 -> short calls to trim down to target
					if (need2 < -1e-4) AddCoveredShortCall(legs, -need2, S, iv, Ts, exp); // covered short call -- no naked
				}
				else if (target > FlatEps)
				{
					double d = Math.Min(0.95, target); double K = StrikeForDelta(false, S, iv, Ts, d);
					legs.Add(new Leg { Call = false, Qty = -1, K = K, Exp = exp, VPrev = Price(false, S, K, iv, Ts) });
				}
				return;
			}
			if (Strategy == OverlayStrategy.CallSpread)
			{
				// 1x1 bull call vertical: long call is the core; short a single call at the SAME expiry,
				// struck so net delta = target. No short when target >= the long call's delta (capped).
				var core = legs.FirstOrDefault(l => l.Qty > 0);
				if (core == null) return;
				double coreD = core.Qty * LegDelta(core, S, iv, now);
				double sd = coreD - target;
				if (sd > 1e-3) { double Kc = StrikeForDelta(true, S, iv, TimeToExp(core, now), Math.Min(0.95, sd)); legs.Add(new Leg { Call = true, Qty = -1, K = Kc, Exp = core.Exp }); }
				return;
			}
			if (Strategy == OverlayStrategy.PutSpread)
			{
				// 1x1 bull put vertical: long put (protection) is the core; short a higher-strike put at the
				// SAME expiry, struck so net delta = target. net = coreDelta(≈−PutLeapDelta) + |shortPutDelta|.
				var core = legs.FirstOrDefault(l => l.Qty > 0);
				if (core == null) return;
				double coreDp = core.Qty * LegDelta(core, S, iv, now); // ≈ −PutLeapDelta
				double sp = target - coreDp; // short-put delta magnitude to reach target (> 0)
				if (sp > 1e-3) { double Kp = StrikeForDelta(false, S, iv, TimeToExp(core, now), Math.Min(0.90, sp)); legs.Add(new Leg { Call = false, Qty = -1, K = Kp, Exp = core.Exp }); }
				return;
			}
			if (Strategy == OverlayStrategy.PmccStrangle)
			{
				// always 1 short call + 1 short put; nearer leg pinned at StrangleMinDelta, the other floats.
				double coreD = legs.Where(l => l.Qty > 0).Sum(l => l.Qty * LegDelta(l, S, iv, now));
				double diff = target - coreD;   // >0 need more delta (deepen the put), <0 need less (deepen the call)
				double callD = Math.Min(0.95, diff < 0 ? StrangleMinDelta - diff : StrangleMinDelta);
				double putD  = Math.Min(0.95, diff > 0 ? StrangleMinDelta + diff : StrangleMinDelta);
				legs.Add(new Leg { Call = true,  Qty = -1, K = StrikeForDelta(true,  S, iv, Ts, callD), Exp = exp });
				legs.Add(new Leg { Call = false, Qty = -1, K = StrikeForDelta(false, S, iv, Ts, putD),  Exp = exp });
				return;
			}
			double coreDelta = legs.Where(l => l.Qty > 0).Sum(l => l.Qty * LegDelta(l, S, iv, now));
			double needed = target - coreDelta;
			if (Strategy == OverlayStrategy.CoveredStock && CoveredCollar && needed < -1e-4)
			{
				// collar: 1 long put + 1 short call, deltas summing to the reduction R (= 1 - target at core delta 1).
				double R = -needed;
				double pd = Math.Min(R, CoveredPutDelta);
				double cd = R - pd;
				if (pd > 1e-3) legs.Add(new Leg { Call = false, Qty = 1,  K = StrikeForDelta(false, S, iv, Ts, Math.Min(0.95, pd)), Exp = exp }); // long put
				if (cd > 1e-3) legs.Add(new Leg { Call = true,  Qty = -1, K = StrikeForDelta(true,  S, iv, Ts, Math.Min(0.95, cd)), Exp = exp }); // short call
				return;
			}
			if (needed < -1e-4)
				AddCoveredShortCall(legs, -needed, S, iv, Ts, exp); // covered short call (+ long-put remainder) -- no naked
			else if (needed > 1e-4 && Strategy != OverlayStrategy.Pmcc)
				legs.Add(new Leg { Call = false, Qty = -(needed / ShortLegDelta), K = StrikeForDelta(false, S, iv, Ts, ShortLegDelta), Exp = exp }); // short puts
		}

		private static double LegValue(Leg l, double S, double iv, DateTime now) => l.Stock ? S : Price(l.Call, S, l.K, iv, TimeToExp(l, now));
		private static double LegDelta(Leg l, double S, double iv, DateTime now) => l.Stock ? 1.0 : Delta(l.Call, S, l.K, iv, TimeToExp(l, now));
		private static double TimeToExp(Leg l, DateTime now) => Math.Max(1.0 / 365.0, (l.Exp - now).TotalDays / 365.0);
		private static double Cost(Leg l, double v) => (l.Stock ? StockSpreadFrac : SpreadFraction) * Math.Abs(l.Qty) * Math.Max(0, v);

		// ---- trailing realized vol (annualized, decimal) indexed by bar date ----
		private static Dictionary<DateTime, double> TrailingHv(IReadOnlyList<OhlcBar> bars)
		{
			var map = new Dictionary<DateTime, double>();
			for (int i = 0; i < bars.Count; i++)
			{
				int j0 = Math.Max(1, i - (HvWindow - 1)); var lr = new List<double>();
				for (int j = j0; j <= i; j++) if (bars[j - 1].Close > 0 && bars[j].Close > 0) lr.Add(Math.Log(bars[j].Close / bars[j - 1].Close));
				if (lr.Count >= 5) { double m = lr.Average(); double v = lr.Select(x => (x - m) * (x - m)).Sum() / (lr.Count - 1); map[bars[i].Date] = Math.Max(HvFloor, Math.Sqrt(v) * Math.Sqrt(252.0)); }
				else map[bars[i].Date] = double.NaN;
			}
			return map;
		}

		// ---- Black-Scholes (r = 0) ----
		private static double Erf(double x) { int s = x < 0 ? -1 : 1; x = Math.Abs(x); double t = 1 / (1 + 0.3275911 * x); double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x); return s * y; }
		private static double N(double x) => 0.5 * (1 + Erf(x / Math.Sqrt(2)));
		private static double InvN(double p)
		{
			p = Math.Min(1 - 1e-9, Math.Max(1e-9, p));
			double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
			double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
			double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
			double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
			double plow = 0.02425, phigh = 1 - plow, q, r;
			if (p < plow) { q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
			if (p <= phigh) { q = p - 0.5; r = q * q; return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1); }
			q = Math.Sqrt(-2 * Math.Log(1 - p)); return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
		}
		private static double D1(double S, double K, double sig, double T) => (Math.Log(S / K) + 0.5 * sig * sig * T) / (sig * Math.Sqrt(T));
		private static double Price(bool call, double S, double K, double sig, double T)
		{ if (T <= 0) return call ? Math.Max(0, S - K) : Math.Max(0, K - S); double d1 = D1(S, K, sig, T), d2 = d1 - sig * Math.Sqrt(T); return call ? S * N(d1) - K * N(d2) : K * N(-d2) - S * N(-d1); }
		private static double Delta(bool call, double S, double K, double sig, double T)
		{ if (T <= 0) return call ? (S > K ? 1 : 0) : (S < K ? -1 : 0); double d1 = D1(S, K, sig, T); return call ? N(d1) : N(d1) - 1; }
		private static double StrikeForDelta(bool call, double S, double sig, double T, double tgt)
		{ double d1 = call ? InvN(tgt) : InvN(1.0 - tgt); return S / Math.Exp(d1 * sig * Math.Sqrt(T) - 0.5 * sig * sig * T); }

		// ---- stats ----
		private static double Sharpe(List<double> r) { if (r.Count < 2) return double.NaN; double m = r.Average(); double v = r.Select(x => (x - m) * (x - m)).Sum() / (r.Count - 1); double sd = Math.Sqrt(v); return sd <= 0 ? double.NaN : m / sd * Math.Sqrt(252.0); }
		private static double MaxDrawdown(List<double> r) { double eq = 1, pk = 1, md = 0; foreach (var x in r) { eq *= (1 + x); if (eq > pk) pk = eq; double d = (pk - eq) / pk; if (d > md) md = d; } return md * 100.0; }
		private static double TotalReturn(List<double> r) { double eq = 1; foreach (var x in r) eq *= (1 + x); return (eq - 1) * 100.0; }
	}
}
