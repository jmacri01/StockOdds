using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Drive the 0-DTE spread from an INTRADAY signal instead of the prior daily close.
	//
	// The state machines are scale-free, so the same engine runs on 1h bars and produces a target and an ST state
	// every hour. 1h over 730 days is used rather than 5m over 60: 5m gives ~78 decision points but only ~60
	// sessions, and the differences that decided every call in this line have been 0.03-0.10 of IR, which 60
	// observations cannot resolve. 1h trades resolution for ~500 sessions, which can.
	//
	// THREE ARMS, because "follow the same rules" is ambiguous once the signal can change mid-session:
	//   BASELINE   daily signal from the prior close, enter at the open, hold to expiry. What ships today.
	//   ENTRY-ONLY 1h signal picks the first qualifying bar of the session; then all later signals are IGNORED and
	//              the spread is held to expiry. Tests whether intraday TIMING beats entering at the open.
	//   FULL       enter on the first qualifying bar and CLOSE when the signal turns off. Faithful to the rules,
	//              but the position usually dies before expiry, so it stops being a 0-DTE-to-expiry trade at all.
	//
	// One trade per session in every arm. Entry filters are the shipped ones -- target >= 0.10 and not ST Bear --
	// evaluated on the 1h bar rather than the daily one. Nothing here can see past the bar it acts on.
	//
	// Same modelling caveats as the roll test: IV frozen through the session, linear intraday theta, and a
	// mid-to-cross drag of 2.6% of credit per fill (measured, not assumed). Mid-session exits in the FULL arm are
	// marked with Black-Scholes at the remaining time, which is where frozen IV is least defensible.
	public static class HourlySignalTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;
		public static double CostPct = 2.6;
		public static string Interval = "1h";
		public static string Range = "730d";
		// Entry is only permitted in the first this-much of the session. 3 of 6.5 hours = 0.4615. The late-session
		// entries a fine grid makes available are exactly where the frozen-IV model is least trustworthy and where
		// the spread gets thin enough that the max-loss denominator levers size up, so bounding entry is a real
		// control rather than a preference.
		public static double MaxEntryFraction = 1.0;
		// Restrict to sessions on/after this date, so a 5m run (60d cap) and a 1h run (730d) can be compared on the
		// SAME sessions instead of confounding bar resolution with which era each series happens to cover.
		public static DateTime FromDate = DateTime.MinValue;

		private sealed record Sess(DateTime D, double Ret, int EntryBar, int Bars, bool Held, double Notional);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var hourly = await IntradayClient.GetAsync(symbol, Interval, Range);
			if (hourly.Count < 500) { Console.WriteLine("not enough hourly data"); return; }

			// ---- daily engine (for the baseline) ----
			var dEng = BankrollSimulator.Run(daily, 10_000.0);
			var dPos = new Dictionary<DateTime, double>();
			for (int k = 0; k < dEng.Positions.Count && k < dEng.ReturnDates.Count; k++)
				dPos[dEng.ReturnDates[k].Date] = dEng.Positions[k];
			var dSt = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < dEng.StState.Count && k < dEng.ReturnDates.Count; k++)
				dSt[dEng.ReturnDates[k].Date] = dEng.StState[k];

			// ---- the SAME engine run on 1h bars: scale-free, so this is the identical object at a finer step ----
			var hEng = BankrollSimulator.Run(hourly, 10_000.0);
			var hPos = new Dictionary<DateTime, double>();
			for (int k = 0; k < hEng.Positions.Count && k < hEng.ReturnDates.Count; k++)
				hPos[hEng.ReturnDates[k]] = hEng.Positions[k];
			var hSt = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < hEng.StState.Count && k < hEng.ReturnDates.Count; k++)
				hSt[hEng.ReturnDates[k]] = hEng.StState[k];

			var hv = new Dictionary<DateTime, double>();
			for (int i = 1; i < daily.Count; i++)
			{
				int j0 = Math.Max(1, i - (HvWindow - 1));
				var lr = new List<double>();
				for (int j = j0; j <= i; j++)
					if (daily[j - 1].Close > 0 && daily[j].Close > 0) lr.Add(Math.Log(daily[j].Close / daily[j - 1].Close));
				if (lr.Count >= 10)
				{
					double m = lr.Average();
					hv[daily[i].Date.Date] = Math.Max(0.05, Math.Sqrt(lr.Sum(x => (x - m) * (x - m)) / (lr.Count - 1)) * Math.Sqrt(252.0));
				}
			}
			var prevOf = new Dictionary<DateTime, DateTime>();
			for (int i = 1; i < daily.Count; i++) prevOf[daily[i].Date.Date] = daily[i - 1].Date.Date;

			var sessions = hourly.GroupBy(b => b.Date.Date).Where(g => g.Count() >= 4 && g.Key >= FromDate)
			                     .OrderBy(g => g.Key).ToList();

			Console.WriteLine($"\n===== {symbol}: INTRADAY ({Interval}) SIGNAL vs THE DAILY SIGNAL, one trade per session =====");
			Console.WriteLine($"{sessions.Count} sessions {sessions.First().Key:yyyy-MM-dd} -> {sessions.Last().Key:yyyy-MM-dd} | " +
				$"structure long {WingDelta:0.00}d / short {NetDelta + WingDelta:0.00}d | risk {100 * Risk:0.#}% | cost {CostPct:0.0}%/fill" +
				(MaxEntryFraction < 1.0 ? $" | ENTRY ONLY IN FIRST {100 * MaxEntryFraction:0}% OF SESSION" : ""));

			bool Qualifies(double t, ShortTermState st) => t >= TargetLo && !(SkipStBear && st == ShortTermState.Bear);

			// mode 0 = baseline (daily signal, enter at open, hold), 1 = entry-only, 2 = full (exit on signal off)
			List<Sess> Simulate(int mode)
			{
				var outp = new List<Sess>();
				foreach (var g in sessions)
				{
					var bars = g.OrderBy(b => b.Date).ToList();
					DateTime d = g.Key;
					int n = bars.Count;
					if (!prevOf.TryGetValue(d, out var dPrev)) continue;
					if (!hv.TryGetValue(dPrev, out double h)) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(d)) continue;
					double iv = h * VolRiskPremium;

					int entry;
					if (mode == 0)
					{
						if (!dPos.TryGetValue(dPrev, out double dt)) continue;
						dSt.TryGetValue(dPrev, out var dstate);
						if (!Qualifies(dt, dstate)) continue;
						entry = 0;                                  // at the open
					}
					else
					{
						// first bar of the session whose OWN intraday signal qualifies; entry is at that bar's close,
						// so nothing is used before it exists. Never the last bar -- no time left to sell. Entry is
						// additionally confined to the first MaxEntryFraction of the session.
						entry = -1;
						int lastAllowed = Math.Min(n - 2, Math.Max(0, (int)Math.Floor(MaxEntryFraction * n) - 1));
						for (int i = 0; i <= lastAllowed; i++)
						{
							if (!hPos.TryGetValue(bars[i].Date, out double t)) continue;
							hSt.TryGetValue(bars[i].Date, out var s);
							if (Qualifies(t, s)) { entry = i; break; }
						}
						if (entry < 0) continue;
					}

					double Topen = (double)(n - entry) / n / 252.0;
					double S = entry == 0 && mode == 0 ? bars[0].Open : bars[entry].Close;
					if (S <= 0 || Topen <= 1e-9) continue;
					double kS = StrikeForPutDelta(S, iv, Topen, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S, iv, Topen, WingDelta);
					double cr = Put(S, kS, iv, Topen) - Put(S, kL, iv, Topen);
					double maxLoss = (kS - kL) - cr;
					if (cr <= 1e-9 || maxLoss <= 1e-9) continue;
					double entryCost = cr * CostPct / 100.0;

					if (mode == 2)
					{
						for (int j = entry + 1; j < n; j++)
						{
							hPos.TryGetValue(bars[j].Date, out double t);
							hSt.TryGetValue(bars[j].Date, out var s);
							if (Qualifies(t, s)) continue;
							double Trem = (double)(n - j) / n / 252.0;
							double val = Put(bars[j].Close, kS, iv, Trem) - Put(bars[j].Close, kL, iv, Trem);
							double exitCost = val * CostPct / 100.0;
							outp.Add(new Sess(d, Risk * (cr - val - entryCost - exitCost) / maxLoss, entry, j - entry, false,
							100 * Risk * NetDelta * S / maxLoss));
							goto next;
						}
					}
					{
						double ST = bars[n - 1].Close;
						double payoff = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
						outp.Add(new Sess(d, Risk * (cr + payoff - entryCost) / maxLoss, entry, n - 1 - entry, true,
						100 * Risk * NetDelta * S / maxLoss));
					}
					next: ;
				}
				return outp;
			}

			Console.WriteLine($"\n{"arm",34} {"sess",7} {"entryBar",9} {"impDelta%",10} {"%toExpiry",10} " +
				$"{"mean/sess%",11} {"win%",7} {"IR",8} {"Sharpe",8} {"maxDD%",8} {"worst%",8} {"SE mean",8}");
			void Show(string label, List<Sess> s)
			{
				if (s.Count < 12) { Console.WriteLine($"{label,34} {s.Count,7}  (too few)"); return; }
				var r = s.Select(x => x.Ret).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double yrs = Math.Max(0.5, (s.Last().D - s.First().D).TotalDays / 365.25);
				var (_, dd) = Curve(r);
				Console.WriteLine($"{label,34} {s.Count,7} {s.Average(x => (double)x.EntryBar),9:0.00} " +
					$"{s.Average(x => x.Notional),10:0.0} {100.0 * s.Count(x => x.Held) / s.Count,10:0.0} " +
					$"{100 * m,11:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {(sd > 0 ? m / sd * Math.Sqrt(s.Count / yrs) : 0),8:0.000} " +
					$"{dd,8:0.00} {100 * r.Min(),8:0.00} {(sd > 0 ? 100 * sd / Math.Sqrt(r.Count) : 0),8:0.000}" +
					(s.Count < 60 ? "  << thin" : ""));
			}
			var b0 = Simulate(0); var b1 = Simulate(1); var b2 = Simulate(2);
			Show("BASELINE daily signal, open->close", b0);
			Show("1h ENTRY-ONLY, hold to expiry", b1);
			Show("1h FULL, exit when signal off", b2);

			// The 1h arms trade MORE sessions than the baseline, because the daily signal has to qualify at the
			// prior close while an intraday signal can qualify at any bar. That is extra SELECTION, not better
			// TIMING, and the two have to be separated before the gap means anything.
			var baseDays = b0.Select(x => x.D).ToHashSet();
			Console.WriteLine("");
			Show("  1h entry-only, BASELINE days", b1.Where(x => baseDays.Contains(x.D)).ToList());
			Show("  1h entry-only, EXTRA days", b1.Where(x => !baseDays.Contains(x.D)).ToList());
			Show("  baseline on shared days", b0.Where(x => b1.Any(y => y.D == x.D)).ToList());
			var sameBar0 = b1.Where(x => x.EntryBar == 0).ToList();
			var later = b1.Where(x => x.EntryBar > 0).ToList();
			Console.WriteLine("");
			Show("  1h entry-only, entered at open", sameBar0);
			Show("  1h entry-only, entered LATER", later);
			Console.WriteLine("\nentryBar is the hourly bar the position opened on (0 = the open). %toExpiry is the share of");
			Console.WriteLine("sessions that actually reached expiry -- the FULL arm's value tells you whether 'the same rules'");
			Console.WriteLine("leaves a 0-DTE-to-expiry trade at all, or turns it into an intraday scalp.");
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
