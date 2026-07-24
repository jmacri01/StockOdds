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
	// calls reduce delta, short puts add it. When the target is ~0 the structure is either held and
	// hedged to 0 delta (CloseAtZero = false, generally better on risk-adjusted terms) or liquidated to
	// cash (CloseAtZero = true).
	public enum OverlayStrategy
	{
		Straddle,     // long call LEAP (CallLeapDelta) + long put LEAP (PutLeapDelta) + short calls/puts
		Pmcc,         // long call LEAP + short calls only (can reduce delta, never add above the LEAP)
		ShortPut,     // no core; a single short put at delta = min(target, ShortPutCap); flat when target ~ 0
		CoveredStock, // long shares + short calls
		PutDiagonal,  // long put LEAP (PutLeapDelta) + short puts
		PmccStrangle, // long call LEAP + an always-on short strangle (1 call + 1 put); the nearer leg is
		              // pinned at StrangleMinDelta, the other floats so net delta hits the target
		SplitStockPut,// regime switch at 0.5: target >= 0.5 -> long stock + covered calls to target;
		              // target < 0.5 -> no stock, a single short put sized to the target
		CallSpread,   // short-dated (ShortDteDays) bull call spread as the core: long call at CallLeapDelta,
		              // short call struck so net delta = target (1x1 vertical, same expiry, rolled monthly)
		PutSpread,    // short-dated (ShortDteDays) bull PUT spread, both legs ~40 DTE: long put at
		              // PutLeapDelta (protection) + short higher-strike put struck so net delta = target
		PmccPutFloor  // PMCC whose short calls are capped at ShortCallCap of delta reduction; any remaining
		              // reduction (at low target) comes from BUYING a put instead of piling on more short calls
	}

	public sealed class OverlayResult
	{
		public List<DateTime> Dates { get; } = new();
		public List<double> Returns { get; } = new();   // per-bar fractional returns of the overlay bankroll
		public double SharpeRatio { get; set; }
		public double MaxDrawdownPct { get; set; }
		public double TotalReturnPct { get; set; }
		public int Rolls { get; set; }                   // count of short-leg resizes / liquidations
	}

	public static class OptionsOverlaySimulator
	{
		// ---- configuration (statics, mirroring BankrollSimulator) ----
		public static OverlayStrategy Strategy = OverlayStrategy.Pmcc;
		public static double VolRiskPremium   = 1.10;  // IV = HV * this
		public static double SpreadFraction   = 0.00;  // per-transaction cost as fraction of option premium (mid ≈ 0, full spread ≈ 0.03)
		public static double StockSpreadFrac  = 0.0005;// per-transaction cost for the stock leg
		public static double DeadbandDelta     = 0.30; // resize shorts when |netDelta - target| exceeds this (= engine RebalanceDrift)
		public static double RebalanceEdge     = 0.0;  // on a resize, aim net delta at target + this*DeadbandDelta: 0 = center (position exposure), -1 = lower edge (exposure - drift), +1 = upper edge
		public static double MaxNetDelta       = 1.0;  // ceiling on the resize target (raise to allow leveraged net delta > 1; only structures that can add — straddle/put-diagonal/covered-stock via short puts — reach it)
		public static double ShortRollDte      = 1;    // roll a trim leg when its remaining DTE <= this (1 = hold to expiry; ShortDteDays/2 = roll at half-life to dodge the final-week gamma/pin ramp)
		public static double ShortProfitTarget = 0.0;  // roll a SHORT leg once it decays to this fraction of its opening premium (0 = off; 0.5 = take 50% profit)
		public static double ShortDteDays      = 14;   // calendar DTE for the rolled short legs. Shorter harvests
		                                               // more theta (universal across strategies, robust to 2% spread);
		                                               // ~14 is the sweet spot — below it you mostly add gamma/gap risk.
		public static double LeapDteDays        = 365;  // calendar DTE for the long LEAP core (rolled at expiry)
		public static double ShortLegDelta     = 0.30; // delta magnitude at which short calls/puts are sold
		public static double ShortCallCap      = 0.50; // PmccPutFloor: cap on the delta reduction from short calls; remainder via a long put
		public static double CallLeapDelta      = 0.80;  // recommended PMCC starter: 0.80-delta, 365-DTE call
		public static double PutLeapDelta       = 0.15;  // shallow far-OTM base put (straddle put leg / put-diagonal base)
		public static double StrangleMinDelta   = 0.25;  // PmccStrangle: the always-on nearer leg's delta floor
		public static double ShortPutCap        = 0.50; // ShortPut: cap the short put at ~ATM (0.50Δ = peak theta, least directional risk). Deeper puts harvest less theta and carry more downside — 0.50 dominates 0.75/0.95 on every universe.
		public static double ShortPutTargetFrac = 1.0;  // ShortPut: fraction of the engine target to express (put delta = min(frac*target, cap)); 0.5 = run at half exposure
		public static double ShortPutProtDelta  = 0.0;  // ShortPut: if >0, buy a long put at this delta (same expiry) -> bull put spread; the short put deepens by this so net delta still = the cap
		public static double FlatEps            = 0.05; // target <= this is treated as "flat"
		// Behaviour at target ~ 0 ("flat"). FlatHoldDays: -1 = hold indefinitely (hedge to 0 delta, never close);
		// 0 = close out to cash on the first flat bar; N = hold-and-hedge for N consecutive flat bars, then close
		// to cash if it still hasn't budged off flat (captures quick recoveries, cuts the tail of a real decline).
		// 20 is the sweet spot: ≈ pure hold on every universe while keeping the position finite.
		public static int    FlatHoldDays       = 20;
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
				else foreach (var l in legs) { double v = LegValue(l, S, iv, date); if (double.IsNaN(v) || double.IsInfinity(v)) v = l.VPrev; pnl += l.Qty * (v - l.VPrev); l.VPrev = v; }

				bool flat = target <= FlatEps;
				if (flat) flatCount++; else flatCount = 0;
				bool doClose = flat && FlatHoldDays >= 0 && flatCount > FlatHoldDays;
				if (doClose)
				{
					if (legs.Count > 0) { foreach (var l in legs) friction += Cost(l, l.VPrev); legs.Clear(); res.Rolls++; }
				}
				else
				{
					if (HasCore(Strategy) && !legs.Any(l => l.Core))
					{ EstablishCore(legs, S, iv, date); foreach (var l in legs.Where(l => l.Core)) { l.VPrev = LegValue(l, S, iv, date); friction += Cost(l, l.VPrev); } }

					// roll the (option) LEAP core one day before expiry
					if (legs.Any(l => l.Core && !l.Stock && (l.Exp - date).TotalDays <= 1))
					{
						foreach (var l in legs.Where(l => l.Core && !l.Stock)) friction += Cost(l, l.VPrev);
						legs.RemoveAll(l => l.Core && !l.Stock); EstablishCore(legs, S, iv, date);
						foreach (var l in legs.Where(l => l.Core && !l.Stock)) { l.VPrev = LegValue(l, S, iv, date); friction += Cost(l, l.VPrev); }
					}

					double net = legs.Sum(l => l.Qty * LegDelta(l, S, iv, date));
					double spTgt = Math.Min(target * ShortPutTargetFrac, ShortPutCap);
				double tnet = Strategy == OverlayStrategy.ShortPut ? (spTgt > FlatEps ? spTgt : 0.0) : target;
					bool shortExpiring = legs.Any(l => !l.Core && (l.Exp - date).TotalDays <= ShortRollDte);
						bool profitHit = ShortProfitTarget > 0 && legs.Any(l => l.Qty < 0 && l.VOpen > 1e-9 && l.VPrev <= ShortProfitTarget * l.VOpen);
					if (Math.Abs(net - tnet) > DeadbandDelta || shortExpiring || profitHit || NeedsRebuild(legs, target))
					{
						foreach (var l in legs.Where(l => !l.Core)) friction += Cost(l, l.VPrev);
						ResizeShorts(legs, S, iv, Math.Max(0.0, Math.Min(MaxNetDelta, target + RebalanceEdge * DeadbandDelta)), date);
						foreach (var l in legs.Where(l => !l.Core)) { l.VPrev = LegValue(l, S, iv, date); l.VOpen = l.VPrev; friction += Cost(l, l.VPrev); }
						res.Rolls++;
					}
				}

				if (bankroll <= 1e-6) { res.Returns.Add(-1.0); res.Dates.Add(date); break; } // ruin
				double netPnl = pnl - friction;
				double dr = netPnl / bankroll; if (double.IsNaN(dr) || double.IsInfinity(dr)) dr = 0;
				bankroll += netPnl; res.Returns.Add(dr); res.Dates.Add(date);
			}

			res.SharpeRatio = Sharpe(res.Returns);
			res.MaxDrawdownPct = MaxDrawdown(res.Returns);
			res.TotalReturnPct = TotalReturn(res.Returns);
			return res;
		}

		private static bool HasCore(OverlayStrategy s) => s != OverlayStrategy.ShortPut && s != OverlayStrategy.SplitStockPut;

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
				case OverlayStrategy.Straddle:
					legs.Add(new Leg { Core = true, Call = true, Qty = 1, K = StrikeForDelta(true, S, iv, T, CallLeapDelta), Exp = exp });
					legs.Add(new Leg { Core = true, Call = false, Qty = 1, K = StrikeForDelta(false, S, iv, T, PutLeapDelta), Exp = exp });
					break;
				case OverlayStrategy.Pmcc:
				case OverlayStrategy.PmccStrangle:
				case OverlayStrategy.PmccPutFloor:
					legs.Add(new Leg { Core = true, Call = true, Qty = 1, K = StrikeForDelta(true, S, iv, T, CallLeapDelta), Exp = exp });
					break;
				case OverlayStrategy.ShortPut:
					break;
				case OverlayStrategy.CoveredStock:
					legs.Add(new Leg { Core = true, Stock = true, Qty = 1, Exp = now.AddYears(100) });
					break;
				case OverlayStrategy.PutDiagonal:
					legs.Add(new Leg { Core = true, Call = false, Qty = 1, K = StrikeForDelta(false, S, iv, T, PutLeapDelta), Exp = exp });
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
				legs.Add(new Leg { Call = true, Qty = -scRed / ShortLegDelta, K = StrikeForDelta(true, S, iv, Ts, ShortLegDelta), Exp = exp }); // short calls (capped)
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
					if (need2 < -1e-4) { double K = StrikeForDelta(true, S, iv, Ts, ShortLegDelta); legs.Add(new Leg { Call = true, Qty = need2 / ShortLegDelta, K = K, Exp = exp, VPrev = Price(true, S, K, iv, Ts) }); }
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
			if (needed < -1e-4)
				legs.Add(new Leg { Call = true, Qty = needed / ShortLegDelta, K = StrikeForDelta(true, S, iv, Ts, ShortLegDelta), Exp = exp }); // short calls
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
