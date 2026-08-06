using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Replaces the single most load-bearing ASSUMPTION in the 0-DTE work with a MEASUREMENT.
	//
	// Every result in this line prices options at IV = HV(60) * VolRiskPremium with VolRiskPremium = 1.10, a number
	// that was never observed. It matters more than any parameter: the structure ranking is a monotone function of
	// it and the wing-DTE effect inverts once it drops below ~0.95.
	//
	// CBOE publishes the implied vol directly, and one of the series is exactly the right tenor:
	//     ^VIX1D   1-DAY implied vol, from 2023-04. THE tenor a 0-DTE spread trades. Short history.
	//     ^VIX9D   9-day, from 2011. Compromise between tenor and sample length.
	//     ^VIX     30-day, from 2001. Long history, wrong tenor -- carried for context only.
	//
	// TWO THINGS ARE REPORTED, and the second is the one that matters:
	//   1. The realised multiplier IV / HV(60), i.e. what VolRiskPremium actually was, and critically how often it
	//      sat below the ~0.95 level where this family of results inverts.
	//   2. The shipped config RE-PRICED with observed IV in place of HV(60)*1.10. Both the strike selection and the
	//      credit use the real number, so this is the honest answer rather than a sensitivity.
	//
	// TENOR CAVEAT ON VIX1D: at the close of day t it prices the move through the next session, which includes the
	// overnight gap; the trade runs open t+1 -> close t+1 and so misses that gap. VIX1D therefore slightly
	// OVERSTATES the vol the position is actually exposed to, which flatters the seller. Using the close-of-t value
	// keeps it look-ahead-free.
	public static class VrpMeasurement
	{
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double MaxShortDelta = 0.95;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;
		public static int    YearsBack = 21;

		private sealed record Tr(DateTime D, double R, double Iv, double Hv, double Under);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", YearsBack);
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			var posByDate = new Dictionary<DateTime, double>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
				posByDate[eng.ReturnDates[k].Date] = eng.Positions[k];
			var stByDate = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < eng.StState.Count && k < eng.ReturnDates.Count; k++)
				stByDate[eng.ReturnDates[k].Date] = eng.StState[k];

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

			async Task<Dictionary<DateTime, double>> Vix(string sym)
			{
				try
				{
					var v = await YahooClient.GetBarsAsync(sym, "1d", 25);
					return v.Where(b => b.Close > 0).GroupBy(b => b.Date.Date)
					        .ToDictionary(g => g.Key, g => g.Last().Close / 100.0);   // quoted in vol POINTS
				}
				catch (Exception ex) { Console.WriteLine($"  {sym} fetch failed: {ex.Message}"); return new(); }
			}
			var v1d = await Vix("^VIX1D");
			var v9d = await Vix("^VIX9D");
			var v30 = await Vix("^VIX");

			Console.WriteLine($"\n===== {symbol}: MEASURING VolRiskPremium INSTEAD OF ASSUMING IT =====");
			Console.WriteLine($"the whole 0-DTE line prices at IV = HV(60) x 1.10. Below is what the multiplier ACTUALLY was.");
			Console.WriteLine($"\n{"series",10} {"tenor",8} {"days",7} {"from",10} {"mean",8} {"median",8} " +
				$"{"p10",8} {"p25",8} {"p75",8} {"p90",8} {"% < 0.95",10} {"% < 1.00",10}");

			// hv is keyed by the raw bar DateTime while the vix dicts are keyed by .Date -- normalise or every
			// lookup misses and the table silently reports zero rows.
			var hvN = hv.GroupBy(k => k.Key.Date).ToDictionary(g => g.Key, g => g.Last().Value);
			void Dist(string name, string tenor, Dictionary<DateTime, double> iv)
			{
				var xs = new List<double>();
				DateTime first = DateTime.MaxValue;
				foreach (var kv in iv)
					if (hvN.TryGetValue(kv.Key, out double h) && h > 0)
					{ xs.Add(kv.Value / h); if (kv.Key < first) first = kv.Key; }
				if (xs.Count < 30) { Console.WriteLine($"{name,10} {tenor,8} {xs.Count,7}  (too few)"); return; }
				xs.Sort();
				double P(double q) => xs[Math.Min(xs.Count - 1, (int)(xs.Count * q))];
				Console.WriteLine($"{name,10} {tenor,8} {xs.Count,7} {first,10:yyyy-MM} {xs.Average(),8:0.000} {P(0.50),8:0.000} " +
					$"{P(0.10),8:0.000} {P(0.25),8:0.000} {P(0.75),8:0.000} {P(0.90),8:0.000} " +
					$"{100.0 * xs.Count(x => x < 0.95) / xs.Count,10:0.0} {100.0 * xs.Count(x => x < 1.00) / xs.Count,10:0.0}");
			}
			Dist("^VIX1D", "1 day", v1d);
			Dist("^VIX9D", "9 day", v9d);
			Dist("^VIX", "30 day", v30);

			// ---- re-price the shipped config with each IV source -------------------------------------------
			List<Tr> Build(Func<DateTime, double, double?> ivOf)
			{
				double T = 1.0 / 252.0;
				var tr = new List<Tr>();
				for (int i = 1; i + 1 < bars.Count; i++)
				{
					var dSig = bars[i].Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!posByDate.TryGetValue(dSig.Date, out double target)) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(bars[i + 1].Date)) continue;
					if (target < TargetLo) continue;
					if (SkipStBear && stByDate.TryGetValue(dSig.Date, out var st) && st == ShortTermState.Bear) continue;
					double? ivN = ivOf(dSig.Date, h);
					if (ivN is not double iv || iv <= 0) continue;

					double S = bars[i + 1].Open, ST = bars[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double shortMag = Math.Min(MaxShortDelta, NetDelta + WingDelta);
					double netD = shortMag - WingDelta;
					double kShort = StrikeForPutDelta(S, iv, T, shortMag);
					double kLong = StrikeForPutDelta(S, iv, T, WingDelta);
					double width = kShort - kLong, cr = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
					double risk = width - cr;
					if (cr <= 1e-9 || risk <= 1e-9) continue;
					double payoff = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
					tr.Add(new Tr(bars[i + 1].Date, (cr + payoff) / risk, iv, h, (ST - S) / S));
				}
				return tr;
			}

			Console.WriteLine($"\n--- SHIPPED CONFIG RE-PRICED WITH OBSERVED IV (risk {100 * Risk:0.#}%, net delta {NetDelta:0.00}) ---");
			Console.WriteLine($"{"IV source",34} {"trades",7} {"from",9} {"meanIV/HV",10} {"mean/tr%",10} {"win%",7} " +
				$"{"IR/tr",8} {"maxDD%",8} {"CAGR%",9}");
			void Show(string label, List<Tr> t)
			{
				if (t.Count < 30) { Console.WriteLine($"{label,34} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				var (final, dd) = Curve(r);
				double cagr = final > -100 ? (Math.Pow(1 + final / 100.0, 1 / yrs) - 1) * 100 : double.NaN;
				Console.WriteLine($"{label,34} {t.Count,7} {t.First().D,9:yyyy-MM} {t.Average(x => x.Iv / x.Hv),10:0.000} " +
					$"{100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {cagr,9:0.0}");
			}

			// Matched windows matter: VIX1D only exists from 2023-04, so the assumed-1.10 arm must be restricted
			// to the SAME dates or the comparison is a regime comparison rather than a pricing comparison.
			var d1 = Build((d, h) => v1d.TryGetValue(d, out var x) ? x : null);
			var d1Assumed = Build((d, h) => v1d.ContainsKey(d) ? h * 1.10 : null);
			var d9 = Build((d, h) => v9d.TryGetValue(d, out var x) ? x : null);
			var d9Assumed = Build((d, h) => v9d.ContainsKey(d) ? h * 1.10 : null);
			Show("OBSERVED ^VIX1D (right tenor)", d1);
			Show("  assumed 1.10, same dates", d1Assumed);
			Show("OBSERVED ^VIX9D", d9);
			Show("  assumed 1.10, same dates", d9Assumed);
			Show("assumed 1.10, full history", Build((d, h) => h * 1.10));

			// Paired: same sessions, only the pricing differs.
			var byDate = d1Assumed.ToDictionary(x => x.D, x => Risk * x.R);
			var diff = d1.Where(x => byDate.ContainsKey(x.D)).Select(x => Risk * x.R - byDate[x.D]).ToList();
			if (diff.Count > 30)
			{
				double dm = diff.Average();
				double dsd = Math.Sqrt(diff.Sum(z => (z - dm) * (z - dm)) / (diff.Count - 1));
				Console.WriteLine($"\npaired (observed VIX1D - assumed 1.10) over {diff.Count} shared sessions: " +
					$"{100 * dm:+0.0000;-0.0000}%/trade, t = {(dsd > 0 ? dm / (dsd / Math.Sqrt(diff.Count)) : 0):+0.00;-0.00}");
			}
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
