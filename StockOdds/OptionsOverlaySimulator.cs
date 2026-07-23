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
		PutDiagonal   // long put LEAP (PutLeapDelta) + short puts
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
		public static double ShortDteDays      = 40;   // calendar DTE for the rolled short legs
		public static double LeapDteDays        = 365;  // calendar DTE for the long LEAP core (rolled at expiry)
		public static double ShortLegDelta     = 0.30; // delta magnitude at which short calls/puts are sold
		public static double CallLeapDelta      = 0.75;
		public static double PutLeapDelta       = 0.25;
		public static double ShortPutCap        = 0.75; // ShortPut strategy: cap the single short put's delta
		public static double FlatEps            = 0.05; // target <= this is treated as "flat"
		public static bool   CloseAtZero        = false;// at flat: true = liquidate to cash, false = hold core + hedge to 0 delta
		public static int    HvWindow           = 60;   // trailing bars for realized-vol estimate
		public static double HvFloor            = 0.08; // floor on annualized HV

		private sealed class Leg { public bool Call; public bool Stock; public double Qty; public double K; public DateTime Exp; public double VPrev; }

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

			double bankroll = 0; bool started = false; var legs = new List<Leg>();
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
				if (CloseAtZero && flat)
				{
					if (legs.Count > 0) { foreach (var l in legs) friction += Cost(l, l.VPrev); legs.Clear(); res.Rolls++; }
				}
				else
				{
					if (HasCore(Strategy) && !legs.Any(l => l.Qty > 0))
					{ EstablishCore(legs, S, iv, date); foreach (var l in legs.Where(l => l.Qty > 0)) { l.VPrev = LegValue(l, S, iv, date); friction += Cost(l, l.VPrev); } }

					// roll the (option) LEAP core one day before expiry
					if (legs.Any(l => l.Qty > 0 && !l.Stock && (l.Exp - date).TotalDays <= 1))
					{
						foreach (var l in legs.Where(l => l.Qty > 0 && !l.Stock)) friction += Cost(l, l.VPrev);
						legs.RemoveAll(l => l.Qty > 0 && !l.Stock); EstablishCore(legs, S, iv, date);
						foreach (var l in legs.Where(l => l.Qty > 0 && !l.Stock)) { l.VPrev = LegValue(l, S, iv, date); friction += Cost(l, l.VPrev); }
					}

					double net = legs.Sum(l => l.Qty * LegDelta(l, S, iv, date));
					double tnet = Strategy == OverlayStrategy.ShortPut ? (Math.Min(target, ShortPutCap) > FlatEps ? Math.Min(target, ShortPutCap) : 0.0) : target;
					bool shortExpiring = legs.Any(l => l.Qty < 0 && (l.Exp - date).TotalDays <= 1);
					if (Math.Abs(net - tnet) > DeadbandDelta || shortExpiring)
					{
						foreach (var l in legs.Where(l => l.Qty < 0)) friction += Cost(l, l.VPrev);
						ResizeShorts(legs, S, iv, target, date);
						foreach (var l in legs.Where(l => l.Qty < 0)) { l.VPrev = LegValue(l, S, iv, date); friction += Cost(l, l.VPrev); }
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

		private static bool HasCore(OverlayStrategy s) => s != OverlayStrategy.ShortPut;

		private static void EstablishCore(List<Leg> legs, double S, double iv, DateTime now)
		{
			double T = LeapDteDays / 365.0; var exp = now.AddDays(LeapDteDays);
			switch (Strategy)
			{
				case OverlayStrategy.Straddle:
					legs.Add(new Leg { Call = true, Qty = 1, K = StrikeForDelta(true, S, iv, T, CallLeapDelta), Exp = exp });
					legs.Add(new Leg { Call = false, Qty = 1, K = StrikeForDelta(false, S, iv, T, PutLeapDelta), Exp = exp });
					break;
				case OverlayStrategy.Pmcc:
					legs.Add(new Leg { Call = true, Qty = 1, K = StrikeForDelta(true, S, iv, T, CallLeapDelta), Exp = exp });
					break;
				case OverlayStrategy.ShortPut:
					break;
				case OverlayStrategy.CoveredStock:
					legs.Add(new Leg { Stock = true, Qty = 1, Exp = now.AddYears(100) });
					break;
				case OverlayStrategy.PutDiagonal:
					legs.Add(new Leg { Call = false, Qty = 1, K = StrikeForDelta(false, S, iv, T, PutLeapDelta), Exp = exp });
					break;
			}
		}

		private static void ResizeShorts(List<Leg> legs, double S, double iv, double target, DateTime now)
		{
			legs.RemoveAll(l => l.Qty < 0);
			double Ts = ShortDteDays / 365.0; var exp = now.AddDays(ShortDteDays);
			if (Strategy == OverlayStrategy.ShortPut)
			{
				double tgt = Math.Min(target, ShortPutCap);
				if (tgt > FlatEps) legs.Add(new Leg { Call = false, Qty = -1, K = StrikeForDelta(false, S, iv, Ts, tgt), Exp = exp });
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
