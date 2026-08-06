using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// The specific configuration: risk 5% of account per trade, only when the engine target is in [0.10, 0.50),
	// 0-DTE put credit spread (long 0.15 delta wing, short at target + 0.15), opened at the open and expiring at
	// that close. Gated on GEX > 0 vs ungated, against buy & hold.
	//
	// MARGIN NOTE: for a defined-risk spread the broker requirement IS the max loss, so 5% risk uses 5% of the
	// account as margin. The implied net delta at that size is ~240% of account -- real sensitivity, but not a
	// funding constraint. What 5% risk actually means is that a full loss costs 5%, and full losses ran at ~7%
	// of trades, so the binding question is the LOSING STREAK, which is reported.
	//
	// WINDOWS: GEX only starts 2011-05, so the ungated arm is shown over the full 21 years AND over the GEX era,
	// and only the GEX-era rows are a like-for-like comparison with the gate.
	public static class FiveperecentBandTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double MaxShortDelta = 0.95;
		public static int    YearsBack = 21;
		// Shipped sizing: a full loss costs 10% of the account. Margin equals max loss on a defined-risk spread,
		// so this is the margin too. SPY 228% CAGR / 36.9% DD; IWM 107% / 35.5%. Max losing streak is 4, and four
		// full losses compounds to -34%, i.e. nearly the whole drawdown -- 5% halves both if that is too much.
		public static double Risk = 0.10;
		public static double[] RiskLevels = { 0.05, 0.10, 0.15, 0.20 };
		// SHIPPED CONFIGURATION (matches the Pine indicator's 0DTE overlay):
		//   net delta FIXED at 0.25 -- the engine target is worthless as a strike input (paired t = -4.79
		//   against flat 0.25 on the same sessions) but valuable as a day filter, so it gates only.
		//   TargetHi = 0 means NO upper cap; the old 0.50 cap was strictly dominated.
		public static double FixedNetDelta = 0.20;   // <= 0 restores the old "net delta = target" behaviour
		// Skip ST Bear candles: the one bad short-term state (SPY IR 0.113 there vs 0.526 in ST Bull), and cutting
		// it drops max drawdown 27.8 -> 19.0 on SPY and 36.1 -> 19.8 on IWM while raising Sharpe on both.
		public static bool SkipStBear = true;

		// ENTRY TIMING.
		//   false (default)  signal at close t -> OPEN the position at the OPEN of t+1 -> settles at close t+1.
		//                    One intraday session, no overnight exposure.
		//   true             signal at close t -> OPEN the position AT THAT CLOSE -> settles at close t+1.
		//                    Now a 1-session overnight hold: it picks up the gap, in both directions.
		//
		// Two things change with it that are easy to miss:
		//   PRICING GETS MORE HONEST. T = 1/252 is exact for close-to-close, but overstates an open-to-close
		//   hold of ~6.5 hours, so the default arm is priced with slightly too much time and collects slightly
		//   too much credit. Close entry removes that.
		//   THE GEX GATE MUST SHIFT BACK A DAY. The vendor's day-t print is published AFTER day t's close, so
		//   it cannot gate an order executed AT that close. Close entry therefore reads gex[t-1]. The gate that
		//   would actually suit this entry -- gamma of the expiry landing tomorrow, computable at the close from
		//   standing OI -- is exactly the series that cannot be reconstructed historically.
		public static bool EntryAtPrevClose = false;
		public static double TargetLo = 0.10, TargetHi = 0;

		// SPY EXPIRY-AVAILABILITY CALENDAR (approximate, documented).
		// A 0-DTE trade requires an expiry that LANDS on the trade date. Daily SPY expiries are recent: the sample
		// starts with Friday-only weeklys, and Tue/Thu did not exist until 2022. Every "0 DTE every session" result
		// prior to these dates is therefore not a trade that could have been placed. Cutoffs are approximate to
		// within a few weeks, which is why a Friday-only arm is carried as the conservative bound.
		// Before weeklys existed on a given name, the ONLY expiry was the monthly third Friday -- roughly 12 dates
		// a year, not 250. That distinction barely matters for SPY (weeklys from ~2005, the start of the sample)
		// but is material for IWM, whose weeklys came years later.
		public static DateTime WeeklyFriFrom = new DateTime(2005, 1, 1);   // Friday weeklys
		public static DateTime WedFrom = new DateTime(2016, 2, 23);        // Wednesday weeklys
		public static DateTime MonFrom = new DateTime(2016, 8, 15);        // Monday weeklys
		public static DateTime TueThuFrom = new DateTime(2022, 11, 14);    // Tue/Thu -> daily expiries

		// Per-symbol rollout. THESE DATES ARE APPROXIMATE -- good to a few weeks for SPY, and less certain for the
		// others, where I am confident about the ORDER (Friday weeklys, then Mon/Wed, then Tue/Thu daily) and about
		// daily expiries being a 2022-23 phenomenon, but not the exact announcements. The Fridays-only arm is
		// carried in every run precisely because it does not depend on any of these guesses.
		public static void UseCalendar(string symbol)
		{
			switch (symbol.ToUpperInvariant())
			{
				case "SPY":
					WeeklyFriFrom = new DateTime(2005, 1, 1);
					WedFrom = new DateTime(2016, 2, 23); MonFrom = new DateTime(2016, 8, 15);
					TueThuFrom = new DateTime(2022, 11, 14);
					break;
				case "IWM":
				case "QQQ":
					WeeklyFriFrom = new DateTime(2010, 6, 4);      // ETF weeklys rolled out ~2010, well after SPY
					WedFrom = new DateTime(2016, 8, 15); MonFrom = new DateTime(2016, 8, 15);
					TueThuFrom = new DateTime(2023, 4, 3);         // daily expiries reached IWM/QQQ after SPY
					break;
				default:                                            // unknown name: assume monthly-only, most conservative
					WeeklyFriFrom = new DateTime(2100, 1, 1);
					WedFrom = MonFrom = TueThuFrom = new DateTime(2100, 1, 1);
					break;
			}
		}

		// Holiday shifts (a Friday holiday moving expiry to Thursday) are NOT modelled; that is a handful of days.
		private static bool IsThirdFriday(DateTime d) => d.DayOfWeek == DayOfWeek.Friday && d.Day >= 15 && d.Day <= 21;
		public static bool HasSameDayExpiry(DateTime d) => d.DayOfWeek switch
		{
			DayOfWeek.Friday => d >= WeeklyFriFrom || IsThirdFriday(d),
			DayOfWeek.Wednesday => d >= WedFrom,
			DayOfWeek.Monday => d >= MonFrom,
			DayOfWeek.Tuesday or DayOfWeek.Thursday => d >= TueThuFrom,
			_ => false,
		};

		private sealed record Tr(DateTime D, double R, double Target, double DeltaPerRisk, double Under, double Gex, bool HasGex, double GexSame, bool HasSame);

		public static async Task Run(string symbol = "SPY")
		{
			UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", YearsBack);
			var gex = await GexClient.ByDateAsync();
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

			double T = 1.0 / 252.0;
			var tr = new List<Tr>();
			var bhByDate = new List<(DateTime D, double R)>();
			for (int i = 1; i + 1 < bars.Count; i++)
			{
				double u = bars[i].Close > 0 ? (bars[i + 1].Close - bars[i].Close) / bars[i].Close : 0;
				bhByDate.Add((bars[i + 1].Date, u));

				var dSig = bars[i].Date;
				if (!hv.TryGetValue(dSig, out double sig)) continue;
				if (!posByDate.TryGetValue(dSig.Date, out double target)) continue;
				if (target < TargetLo) continue;                                    // the floor
				if (TargetHi > 0 && target >= TargetHi) continue;                   // optional cap (off by default)
				if (SkipStBear && stByDate.TryGetValue(dSig.Date, out var stv)
					&& stv == ShortTermState.Bear) continue;                        // the one bad ST state
				double S = EntryAtPrevClose ? bars[i].Close : bars[i + 1].Open;
				double ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;

				double iv = sig * VolRiskPremium;
				double netDWanted = FixedNetDelta > 0 ? FixedNetDelta : target;
				double shortMag = Math.Min(MaxShortDelta, netDWanted + WingDelta);
				double netD = shortMag - WingDelta;
				if (netD <= 1e-9) continue;
				double kShort = StrikeForPutDelta(S, iv, T, shortMag);
				double kLong = StrikeForPutDelta(S, iv, T, WingDelta);
				double width = kShort - kLong, cr = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
				double risk = width - cr;
				if (cr <= 1e-9 || risk <= 1e-9) continue;
				double payoff = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
				// tradeable gate: the newest GEX print that exists BEFORE the order goes in. Entering at the
				// open of t+1 that is day t's print; entering at the close of t it is only day t-1's.
				var dGate = EntryAtPrevClose ? bars[i - 1].Date : bars[i].Date;
				bool hasGex = gex.TryGetValue(dGate.Date, out var g);
				// LOOK-AHEAD reference: the print from the session the position is actually exposed to.
				bool hasSame = gex.TryGetValue((EntryAtPrevClose ? bars[i].Date : bars[i + 1].Date).Date, out var g2);
				tr.Add(new Tr(bars[i + 1].Date, (cr + payoff) / risk, target, netD * S / risk,
					(ST - S) / S, hasGex ? g!.Gex : double.NaN, hasGex,
					hasSame ? g2!.Gex : double.NaN, hasSame));
			}

			var gexEra = tr.Where(t => t.HasGex).ToList();
			DateTime gexStart = gexEra.Count > 0 ? gexEra.First().D : bars[^1].Date;

			Console.WriteLine($"\n===== {symbol}: RISK {100 * Risk:0.#}%/TRADE, EXPOSURE >= {TargetLo:0.00}" +
				(TargetHi > 0 ? $" and < {TargetHi:0.00}" : " (no upper cap)") + ", 0 DTE =====");
			Console.WriteLine($"entry: {(EntryAtPrevClose ? "AT THE PRIOR CLOSE (overnight hold, gate lagged to t-1)" : "at the next OPEN (intraday only)")}");
			Console.WriteLine($"structure: long {WingDelta:0.00}d put / short " +
				(FixedNetDelta > 0 ? $"{FixedNetDelta + WingDelta:0.00}d put (FIXED {FixedNetDelta:0.00} net delta, exposure gates only)"
				                   : $"(target + {WingDelta:0.00})d put (net delta = target)") + ", open -> same close");
			Console.WriteLine($"{tr.Count} qualifying trades of {bars.Count} bars | full range {tr.First().D:yyyy-MM-dd} -> {tr.Last().D:yyyy-MM-dd}");
			Console.WriteLine($"band selects {100.0 * tr.Count / bars.Count:0.0}% of days | mean implied net delta at {100 * Risk:0.#}% risk: " +
				$"{100 * Risk * tr.Average(t => t.DeltaPerRisk):0.0}% of account | margin used = {100 * Risk:0.#}% of account");

			Console.WriteLine($"\n{"arm",34} {"window",22} {"trades",7} {"total ret%",14} {"maxDD%",9} {"CAGR%",8} " +
				$"{"mean/tr%",10} {"win%",7} {"worstTr%",9} {"maxLossStreak",14}");

			void Row(string label, List<Tr> t, DateTime a, DateTime b, double haircut = 1.0)
			{
				if (t.Count < 5) { Console.WriteLine($"{label,34} {"",22} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R * haircut).ToList();
				var (final, dd) = Curve(r);
				double years = (b - a).TotalDays / 365.25;
				double cagr = years > 0 && final > -100 ? (Math.Pow(1 + final / 100.0, 1 / years) - 1) * 100 : double.NaN;
				int streak = 0, maxStreak = 0;
				foreach (var x in r) { if (x < 0) { streak++; maxStreak = Math.Max(maxStreak, streak); } else streak = 0; }
				Console.WriteLine($"{label,34} {$"{a:yyyy-MM} to {b:yyyy-MM}",22} {t.Count,7} {final,14:0.0} {dd,9:0.00} " +
					$"{cagr,8:0.00} {100 * r.Average(),10:+0.0000;-0.0000} {100.0 * r.Count(x => x > 0) / r.Count,7:0.0} " +
					$"{100 * r.Min(),9:0.00} {maxStreak,14}");
			}

			void BhRow(string label, DateTime a, DateTime b)
			{
				var u = bhByDate.Where(x => x.D >= a && x.D <= b).Select(x => x.R).ToList();
				var (final, dd) = Curve(u);
				double years = (b - a).TotalDays / 365.25;
				double cagr = years > 0 ? (Math.Pow(1 + final / 100.0, 1 / years) - 1) * 100 : double.NaN;
				Console.WriteLine($"{label,34} {$"{a:yyyy-MM} to {b:yyyy-MM}",22} {u.Count,7} {final,14:0.0} {dd,9:0.00} " +
					$"{cagr,8:0.00} {100 * u.Average(),10:+0.0000;-0.0000} {100.0 * u.Count(x => x > 0) / u.Count,7:0.0} " +
					$"{100 * u.Min(),9:0.00} {"",14}");
			}

			DateTime fullA = tr.First().D, fullB = tr.Last().D;
			double savedRisk = Risk;
			foreach (double rk in RiskLevels)
			{
				Risk = rk;
				Console.WriteLine($"\n### risk {100 * rk:0.#}% per trade  (max loss {100 * rk:0.#}%, implied delta " +
					$"{100 * rk * tr.Average(t => t.DeltaPerRisk):0.0}%, margin {100 * rk:0.#}%)");
				Row("NO GATE (full 21y)", tr, fullA, fullB);
				Row("NO GATE (GEX era)", gexEra, gexStart, fullB);
				Row("GATE gex > 0", gexEra.Where(t => t.Gex > 0).ToList(), gexStart, fullB);
				Row("GATE @80% credit", gexEra.Where(t => t.Gex > 0).ToList(), gexStart, fullB, 0.80);
				Row("GATE @70% credit", gexEra.Where(t => t.Gex > 0).ToList(), gexStart, fullB, 0.70);
				// LOOK-AHEAD CEILING: gates on the gex printed AFTER this trade closed. Not tradeable with an
				// end-of-day vendor series -- it measures how much of the signal is contemporaneous, which is the
				// bound on what a pre-open, OI-based same-day gamma calculation could recover.
				Row("[look-ahead] SAME-DAY gex > 0", tr.Where(t => t.HasSame && t.GexSame > 0).ToList(), gexStart, fullB);
				Row("[look-ahead] same-day @80%", tr.Where(t => t.HasSame && t.GexSame > 0).ToList(), gexStart, fullB, 0.80);

				// EXPIRY REALISM: keep only dates where an expiry actually landed on the trade date.
				var real = tr.Where(t => HasSameDayExpiry(t.D)).ToList();
				var realGex = real.Where(t => t.HasGex).ToList();
				var fri = tr.Where(t => t.D.DayOfWeek == DayOfWeek.Friday && HasSameDayExpiry(t.D)).ToList();
				var daily = tr.Where(t => t.D >= TueThuFrom).ToList();
				if (real.Count >= 5)
				{
					Row("REAL EXPIRIES (calendar)", real, real.First().D, real.Last().D);
					Row("REAL EXPIRIES + gate", realGex.Where(t => t.Gex > 0).ToList(),
						realGex.Count > 0 ? realGex.First().D : gexStart, fullB);
					Row("REAL EXPIRIES gate @80%", realGex.Where(t => t.Gex > 0).ToList(),
						realGex.Count > 0 ? realGex.First().D : gexStart, fullB, 0.80);
				}
				if (fri.Count >= 5) Row("Fridays only (conservative)", fri, fri.First().D, fri.Last().D);
				if (daily.Count >= 5) Row("daily-expiry era only (2022-11+)", daily, daily.First().D, daily.Last().D);
			}
			Risk = savedRisk;
			Console.WriteLine();
			BhRow("buy & hold (full 21y)", fullA, fullB);
			BhRow("buy & hold (GEX era)", gexStart, fullB);

			Console.WriteLine($"\n----- same, credit haircut to 80% of model -----");
			Row("NO GATE (full 21y) @80%", tr, fullA, fullB, 0.80);
			Row("GATE gex > 0 @80%", gexEra.Where(t => t.Gex > 0).ToList(), gexStart, fullB, 0.80);

			// crash windows for the gated arm
			Console.WriteLine($"\n----- crash windows, gated, 100% credit -----");
			Console.WriteLine($"{"window",14} {"trades",7} {"ret%",10} {"maxDD%",9} {"B&H ret%",10}");
			(string L, DateTime A, DateTime B)[] w =
			{
				("2008 GFC", new DateTime(2008,8,1), new DateTime(2009,3,31)),
				("2018 Feb", new DateTime(2018,1,20), new DateTime(2018,3,1)),
				("2020 COVID", new DateTime(2020,2,15), new DateTime(2020,4,15)),
				("2022 bear", new DateTime(2022,1,1), new DateTime(2022,12,31)),
			};
			foreach (var (L, A, B) in w)
			{
				var sub = tr.Where(t => t.D >= A && t.D <= B && (!t.HasGex || t.Gex > 0)).ToList();
				var bh = bhByDate.Where(x => x.D >= A && x.D <= B).Select(x => x.R).ToList();
				if (sub.Count < 2 || bh.Count == 0) { Console.WriteLine($"{L,14} {sub.Count,7}  (too few)"); continue; }
				var (f, d) = Curve(sub.Select(x => Risk * x.R).ToList());
				var (bf, _) = Curve(bh);
				Console.WriteLine($"{L,14} {sub.Count,7} {f,10:0.0} {d,9:0.00} {bf,10:0.0}");
			}
		}

		private static (double Final, double MaxDd) Curve(List<double> r)
		{
			double e = 1, peak = 1, dd = 0;
			foreach (var x in r)
			{
				e *= 1 + x;
				if (e <= 1e-12) return (-100, 100);
				if (e > peak) peak = e;
				double q = (peak - e) / peak * 100; if (q > dd) dd = q;
			}
			return ((e - 1) * 100, dd);
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
			double lo = S * 0.3, hi = S * 3.0;
			for (int i = 0; i < 60; i++)
			{
				double mid = 0.5 * (lo + hi);
				if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
		}
	}
}
