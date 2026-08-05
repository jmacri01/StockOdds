using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// The 0-DTE put credit spread with the protective wing pushed OUT in time: short put still expires at that
	// session's close, long put has 7/14/21/28/35/42 sessions left. That makes it a put DIAGONAL, not a vertical,
	// and three things change that the vertical's accounting cannot express:
	//
	//   1. THE WING NO LONGER SETTLES. At the close the short leg goes to intrinsic but the long put is SOLD back
	//      with (N-1)/252 left, so it retains time value. Cost of carrying it for the session is one day of ITS
	//      theta, not its whole premium -- much cheaper than it first looks.
	//   2. THE WIDTH GROWS. Holding delta fixed at 0.15, a longer-dated put sits FURTHER out of the money
	//      (ln(S/K) = d1*sig*sqrt(T) - 0.5*sig^2*T rises with T). So kLong falls, width rises, and at a fixed 5%
	//      risk budget the position SHRINKS. Implied delta per unit of risk is reported for that reason.
	//   3. THE TAIL SOFTENS BUT DOES NOT CLOSE. As ST -> 0 the legs converge to intrinsic and P&L -> -(width -
	//      credit), the same floor as the vertical. The diagonal only helps at MODERATE downside, where the wing's
	//      residual time value is still alive.
	//
	// MARGIN: broker requirement for a diagonal whose long expires later is the width, so max loss is taken as
	// (width - netOpen), consistent with the vertical arms. netOpen can be NEGATIVE (a debit) once the wing is
	// expensive enough, and the formula handles that -- a debit raises capital at risk.
	//
	// FLAT TERM STRUCTURE IS THE KEY LIMITATION. Both legs are priced at IV = HV(60) * VolRiskPremium, so the wing
	// gets the same vol as the expiring leg. Real term structure normally slopes UP, which would make the wing
	// strictly more expensive than modelled -- so these numbers are OPTIMISTIC for long wings. A WingIvMult arm is
	// carried to size that sensitivity.
	//
	// Expiry realism from FiveperecentBandTest is applied to the SHORT leg (a same-day expiry must exist). The wing
	// horizon is taken as exactly N sessions rather than snapped to a listed expiry date.
	public static class WingDteSweep
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double MaxShortDelta = 0.95;
		public static int    YearsBack = 21;
		public static double Risk = 0.05;
		public static double TargetLo = 0.10, TargetHi = 0.50;
		public static int[]  WingDtes = { 1, 7, 14, 21, 28, 35, 42 };   // 1 == same expiry (the shipped vertical)
		public static double[] WingIvMults = { 1.00, 1.05 };

		private sealed record Tr(DateTime D, double R, double NetOpen, double MaxLoss, double Width,
			double DeltaPerRisk, double Gex, bool HasGex, bool RealExpiry, double Move, double QtyDelta);

		public static async Task Run(string symbol = "SPY")
		{
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", YearsBack);
			var gex = await GexClient.ByDateAsync();
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			var posByDate = new Dictionary<DateTime, double>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
				posByDate[eng.ReturnDates[k].Date] = eng.Positions[k];

			var hv = new Dictionary<DateTime, double>();
			for (int i = 1; i < bars.Count; i++)
			{
				int j0 = Math.Max(1, i - (HvWindow - 1));
				var lr = new List<double>();
				for (int j = j0; j <= i; j++)
					if (bars[j - 1].Close > 0 && bars[j].Close > 0) lr.Add(Math.Log(bars[j].Close / bars[j - 1].Close));
				if (lr.Count >= 10)
				{
					double m = lr.Average();
					hv[bars[i].Date] = Math.Max(0.05, Math.Sqrt(lr.Sum(x => (x - m) * (x - m)) / (lr.Count - 1)) * Math.Sqrt(252.0));
				}
			}

			Console.WriteLine($"\n===== {symbol}: LONG-PUT WING DTE SWEEP (0-DTE short leg, risk {100 * Risk:0.#}%/trade, target [{TargetLo:0.00},{TargetHi:0.00})) =====");
			Console.WriteLine($"short put at (target + {WingDelta:0.00})d expiring THAT CLOSE | long put at {WingDelta:0.00}d with N sessions left");
			Console.WriteLine("wing DTE 1 == same expiry == the vertical reported earlier. All arms delta-matched at entry (net delta = target).");

			foreach (double ivm in WingIvMults)
			{
				Console.WriteLine($"\n### wing priced at IV x {ivm:0.00}" +
					(ivm > 1.0 ? "  (crude upward term structure -- the wing costs more)" : "  (flat term structure)"));
				Console.WriteLine($"{"wingDTE",8} {"trades",7} {"netOpen%ml",11} {"credit?",8} {"width%S",9} {"impDelta%",10} " +
					$"{"mean/tr%",10} {"win%",7} {"worstTr%",9} {"total ret%",13} {"maxDD%",8} {"gated mean/tr%",15}");

				foreach (int nd in WingDtes)
				{
					var tr = Build(bars, gex, posByDate, hv, nd, ivm);
					var real = tr.Where(t => t.RealExpiry).ToList();
					if (real.Count < 20) { Console.WriteLine($"{nd,8} {real.Count,7}  (too few)"); continue; }

					var r = real.Select(t => Risk * t.R).ToList();
					var (final, dd) = Curve(r);
					var gated = real.Where(t => t.HasGex && t.Gex > 0).Select(t => Risk * t.R).ToList();
					double meanNetOpenPctMl = 100 * real.Average(t => t.NetOpen / t.MaxLoss);

					Console.WriteLine($"{nd,8} {real.Count,7} {meanNetOpenPctMl,11:+0.0;-0.0} " +
						$"{(meanNetOpenPctMl > 0 ? "credit" : "DEBIT"),8} {100 * real.Average(t => t.Width),9:0.00} " +
						$"{100 * Risk * real.Average(t => t.DeltaPerRisk),10:0.0} " +
						$"{100 * r.Average(),10:+0.0000;-0.0000} {100.0 * r.Count(x => x > 0) / r.Count,7:0.0} " +
						$"{100 * r.Min(),9:0.00} {final,13:0.0} {dd,8:0.00} " +
						$"{(gated.Count >= 20 ? (100 * gated.Average()).ToString("+0.0000;-0.0000") : "n/a"),15}");
				}
			}

			// ---------------------------------------------------------------------------------------------
			// DELTA-MATCHED SIZING -- the comparison that is actually apples-to-apples.
			//
			// The table above sizes every arm to "5% risk", but the risk DENOMINATOR (width - credit) means
			// different things across arms. A 0.80%-wide vertical really can lose its full width in one session; a
			// 6.17%-wide diagonal whose wing still has 41 days of life essentially cannot. So "5% risk" is nominal
			// for long wings and the position collapses to a fifth of the delta -- most of the drawdown improvement
			// above is just HOLDING LESS, the flat-haircut artifact.
			//
			// Here every arm instead carries the engine's target delta (qty = target / netD), so directional
			// exposure is identical by construction and the structures differ only in tail shape and capital tied
			// up. Capital at risk becomes an OUTPUT rather than a fixed input.
			// ---------------------------------------------------------------------------------------------
			Console.WriteLine($"\n### DELTA-MATCHED sizing (every arm carries the engine target delta; flat term structure)");
			Console.WriteLine($"{"wingDTE",8} {"trades",7} {"meanCap%",9} {"maxCap%",9} {"mean/tr%",10} {"win%",7} " +
				$"{"worstTr%",9} {"total ret%",13} {"maxDD%",8} {"Sharpe",8} {"ret/DD",8}");
			foreach (int nd in WingDtes)
			{
				var tr = Build(bars, gex, posByDate, hv, nd, 1.00).Where(t => t.RealExpiry).ToList();
				if (tr.Count < 20) { Console.WriteLine($"{nd,8} {tr.Count,7}  (too few)"); continue; }
				// R is P&L per unit of max loss; multiply back up to per-unit-delta sizing
				var r = tr.Select(t => t.R * t.MaxLoss * t.QtyDelta).ToList();
				var (final, dd) = Curve(r);
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / Math.Max(1, r.Count - 1));
				double perYear = 252.0 * tr.Count / Math.Max(1.0, (tr.Last().D - tr.First().D).TotalDays / 365.25) / 252.0;
				double sharpe = sd > 0 ? m / sd * Math.Sqrt(tr.Count / Math.Max(1.0, (tr.Last().D - tr.First().D).TotalDays / 365.25)) : 0;
				Console.WriteLine($"{nd,8} {tr.Count,7} {100 * tr.Average(t => t.MaxLoss * t.QtyDelta),9:0.00} " +
					$"{100 * tr.Max(t => t.MaxLoss * t.QtyDelta),9:0.00} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(x => x > 0) / r.Count,7:0.0} {100 * r.Min(),9:0.00} {final,13:0.0} {dd,8:0.00} " +
					$"{sharpe,8:0.000} {(dd > 0 ? final / dd : 0),8:0.0}");
			}
			Console.WriteLine("Same delta in every row, so this ranks the STRUCTURES rather than the position sizes.");

			// ---------------------------------------------------------------------------------------------
			// THE DECISIVE CONTROL: is "longer wing wins" a structure result or just "sell more premium"?
			//
			// At matched delta, a longer-dated wing at the same 0.15 delta sits further OTM, so less of the short
			// leg's premium is handed back to it -- the position simply retains more net premium. If that is the
			// whole mechanism, the ranking is a monotone function of VolRiskPremium and must COLLAPSE or INVERT
			// once IV stops exceeding realised vol. That is exactly how the earlier options-structure ranking
			// turned out to be an assumption rather than a fact, so it gets tested directly here.
			//
			// WingIvMult is swept jointly because an upward term structure penalises the long wing specifically,
			// and the two effects work in opposite directions.
			// ---------------------------------------------------------------------------------------------
			Console.WriteLine($"\n### VRP x TERM-STRUCTURE CONTROL, delta-matched (Sharpe; wing DTE across, assumptions down)");
			double savedVrp = VolRiskPremium;
			Console.Write($"{"VRP",6} {"wingIV",7}");
			foreach (int nd in WingDtes) Console.Write($" {"dte" + nd,9}");
			Console.WriteLine($" {"1->42",9}");
			foreach (double vrp in new[] { 1.10, 1.05, 1.00, 0.95, 0.90 })
			{
				foreach (double ivm in new[] { 1.00, 1.05, 1.10 })
				{
					VolRiskPremium = vrp;
					Console.Write($"{vrp,6:0.00} {ivm,7:0.00}");
					double first = double.NaN, last = double.NaN;
					foreach (int nd in WingDtes)
					{
						var tr = Build(bars, gex, posByDate, hv, nd, ivm).Where(t => t.RealExpiry).ToList();
						if (tr.Count < 20) { Console.Write($" {"(few)",9}"); continue; }
						var r = tr.Select(t => t.R * t.MaxLoss * t.QtyDelta).ToList();
						double m = r.Average();
						double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / Math.Max(1, r.Count - 1));
						double yrs = Math.Max(1.0, (tr.Last().D - tr.First().D).TotalDays / 365.25);
						double sh = sd > 0 ? m / sd * Math.Sqrt(tr.Count / yrs) : 0;
						if (double.IsNaN(first)) first = sh;
						last = sh;
						Console.Write($" {sh,9:+0.000;-0.000}");
					}
					Console.WriteLine($" {last - first,9:+0.000;-0.000}");
				}
			}
			VolRiskPremium = savedVrp;
			Console.WriteLine("The last column is the wing-DTE EFFECT. If it stays positive as VRP falls through 1.00, the");
			Console.WriteLine("longer wing is doing something structural. If it shrinks toward zero or flips, the result was");
			Console.WriteLine("the premium assumption restated -- the same failure mode as the earlier structure ranking.");

			// Where does the diagonal actually differ? Split by the session's underlying move.
			Console.WriteLine($"\n----- mean/trade% by session move, flat term structure (real expiries only) -----");
			Console.WriteLine($"{"wingDTE",8} {"< -2%",10} {"-2..-1%",10} {"-1..0%",10} {"0..+1%",10} {"> +1%",10}");
			foreach (int nd in WingDtes)
			{
				var tr = Build(bars, gex, posByDate, hv, nd, 1.00).Where(t => t.RealExpiry).ToList();
				if (tr.Count < 20) continue;
				Console.Write($"{nd,8}");
				(string L, Func<double, bool> P)[] bands =
				{
					("<-2", u => u < -0.02), ("-2..-1", u => u >= -0.02 && u < -0.01),
					("-1..0", u => u >= -0.01 && u < 0), ("0..1", u => u >= 0 && u < 0.01), (">1", u => u >= 0.01),
				};
				foreach (var (L, P) in bands)
				{
					var sub = tr.Where(t => P(t.Move)).ToList();
					Console.Write(sub.Count >= 10 ? $" {100 * Risk * sub.Average(t => t.R),9:+0.0000;-0.0000}" : $" {"(few)",9}");
				}
				Console.WriteLine();
			}
			Console.WriteLine("A diagonal should beat the vertical in the MODERATE-DOWN bands (wing still has time value) and");
			Console.WriteLine("lag it on quiet/up days (wing theta paid for nothing). That is the shape to look for.");
		}

		private static List<Tr> Build(List<OhlcBar> bars, Dictionary<DateTime, GexDay> gex,
			Dictionary<DateTime, double> posByDate, Dictionary<DateTime, double> hv, int wingDte, double wingIvMult)
		{
			double Ts = 1.0 / 252.0;
			double Tl = wingDte / 252.0;
			double TlClose = Math.Max(0.0, (wingDte - 1) / 252.0);
			var tr = new List<Tr>();

			for (int i = 1; i + 1 < bars.Count; i++)
			{
				var dSig = bars[i].Date;
				if (!hv.TryGetValue(dSig, out double sig)) continue;
				if (!posByDate.TryGetValue(dSig.Date, out double target)) continue;
				if (target < TargetLo || target >= TargetHi) continue;
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;

				double iv = sig * VolRiskPremium;
				// Term structure must vanish at wingDte == 1, where the wing shares the short leg's expiry and so
				// MUST carry the same IV. Applying a flat multiplier to every arm (the first version of this control)
				// invented a within-expiry vol difference and penalised the baseline. Scale linearly in tenor instead.
				double termFrac = WingDtes.Max() > 1 ? (wingDte - 1.0) / (WingDtes.Max() - 1.0) : 0.0;
				double ivw = iv * (1.0 + (wingIvMult - 1.0) * termFrac);
				double shortMag = Math.Min(MaxShortDelta, target + WingDelta);
				double netD = shortMag - WingDelta;
				if (netD <= 1e-9) continue;

				double kShort = StrikeForPutDelta(S, iv, Ts, shortMag);
				double kLong = StrikeForPutDelta(S, ivw, Tl, WingDelta);
				double width = kShort - kLong;
				if (width <= 1e-9) continue;

				// open: sell the expiring put, buy the longer-dated wing
				double netOpen = Put(S, kShort, iv, Ts) - Put(S, kLong, ivw, Tl);
				// close: short settles intrinsic, wing sold back with TlClose left
				double closeVal = -Math.Max(0, kShort - ST) + Put(ST, kLong, ivw, TlClose);
				double pnl = netOpen + closeVal;

				double maxLoss = width - netOpen;
				if (maxLoss <= 1e-9) continue;

				bool hasGex = gex.TryGetValue(dSig.Date, out var g);
				tr.Add(new Tr(bars[i + 1].Date, pnl / maxLoss, netOpen, maxLoss, width / S,
					netD * S / maxLoss, hasGex ? g!.Gex : double.NaN, hasGex,
					FiveperecentBandTest.HasSameDayExpiry(bars[i + 1].Date), (ST - S) / S,
					(1.0 / S) * (target / netD)));
			}
			return tr;
		}

		private static double Nd(double x) => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));
		private static double Erf(double x)
		{
			double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
			double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
			return x >= 0 ? y : -y;
		}
		private static double Put(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return Math.Max(0, K - S);
			double v = iv * Math.Sqrt(T);
			double d1 = (Math.Log(S / K) + 0.5 * iv * iv * T) / v;
			return K * Nd(v - d1) - S * Nd(-d1);
		}
		private static double PutDeltaMag(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S < K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd(-((Math.Log(S / K) + 0.5 * iv * iv * T) / v));
		}
		private static double StrikeForPutDelta(double S, double iv, double T, double mag)
		{
			double lo = S * 0.05, hi = S * 3.0;
			for (int i = 0; i < 80; i++)
			{
				double mid = 0.5 * (lo + hi);
				if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
		}
		private static (double Final, double MaxDd) Curve(List<double> r)
		{
			double e = 1, p = 1, d = 0;
			foreach (var x in r) { e *= 1 + x; if (e <= 0) return (-100, 100); if (e > p) p = e; double q = (p - e) / p; if (q > d) d = q; }
			return ((e - 1) * 100, d * 100);
		}
	}
}
