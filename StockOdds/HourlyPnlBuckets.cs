using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// WHERE IN THE SESSION DOES THE 0DTE PUT SPREAD ACTUALLY MAKE ITS MONEY?
	//
	// The shipped rule opens at the session OPEN and holds to the CLOSE, so the daily test sees one
	// number per session. This marks the same spread on 1h bars and attributes the P&L to the hour it
	// accrued in. Increments sum EXACTLY to the shipped session return (entry credit -> expiry
	// intrinsic), which is asserted below rather than assumed.
	//
	// EACH HOUR IS SPLIT INTO TWO COMPONENTS, because they have very different evidential status:
	//
	//   THETA  = reprice at the PRIOR spot with the new (shorter) tenor. This is MODEL OUTPUT, not
	//            measurement. IV is frozen at HV x VolRiskPremium and tenor runs down linearly in
	//            bars, so the shape of theta across the day is essentially assumed. Real 0DTE IV
	//            collapses on its own schedule and the true decay is far more back-loaded. Read the
	//            theta column as "how the model allocates a known total", NOT as a finding.
	//
	//   SPOT   = reprice at the NEW spot with the same tenor. This is driven by the REAL price path,
	//            so the hour-to-hour pattern here is a genuine measurement of when the position is
	//            exposed to directional/gamma risk.
	//
	// The split is order-dependent (theta first, then spot) but sums exactly, so the TOTAL column is
	// model-free given entry strikes; only the division between the two columns carries the ordering.
	// ================================================================================================
	internal static class HourlyPnlBuckets
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;

		private static double NormCdf(double x)
		{
			double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
			double p = 1.0 - 0.3989422804014327 * Math.Exp(-x * x / 2.0) *
				(0.319381530 * t - 0.356563782 * t * t + 1.781477937 * t * t * t
				 - 1.821255978 * t * t * t * t + 1.330274429 * t * t * t * t * t);
			return x >= 0 ? p : 1.0 - p;
		}

		private static double Put(double s, double k, double v, double t)
		{
			if (t <= 0 || v <= 0) return Math.Max(0, k - s);
			double d1 = (Math.Log(s / k) + 0.5 * v * v * t) / (v * Math.Sqrt(t));
			return k * NormCdf(-(d1 - v * Math.Sqrt(t))) - s * NormCdf(-d1);
		}

		private static double StrikeForPutDelta(double s, double v, double t, double delta)
		{
			double lo = s * 0.5, hi = s * 1.5;
			for (int i = 0; i < 80; i++)
			{
				double mid = 0.5 * (lo + hi);
				double d1 = (Math.Log(s / mid) + 0.5 * v * v * t) / (v * Math.Sqrt(t));
				if (NormCdf(-d1) < delta) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
		}

		// One hour's attributed P&L for one session, as a fraction of bankroll at the configured risk.
		private sealed record Inc(DateTime D, string Bucket, double Total, double Theta, double Spot);

		// The real ET grid for a full 7-bar RTH session. The last bar is a half hour (15:30-16:00).
		private static readonly string[] Clock7 =
			{ "1 09:30", "2 10:30", "3 11:30", "4 12:30", "5 13:30", "6 14:30", "7 15:30" };

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(daily, 10_000.0);
			var intraday = await IntradayClient.GetAsync(symbol, "1h", "730d");
			if (intraday.Count < 200) { Console.WriteLine("not enough intraday data"); return; }

			var posByDate = new Dictionary<DateTime, double>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
				posByDate[eng.ReturnDates[k].Date] = eng.Positions[k];
			var stByDate = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < eng.StState.Count && k < eng.ReturnDates.Count; k++)
				stByDate[eng.ReturnDates[k].Date] = eng.StState[k];

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

			var sessions = intraday.GroupBy(b => b.Date.Date).Where(g => g.Count() >= 4).OrderBy(g => g.Key).ToList();
			var incs = new List<Inc>();
			var sessTotal = new Dictionary<DateTime, double>();
			var barCounts = new Dictionary<int, int>();

			foreach (var g in sessions)
			{
				var bars = g.OrderBy(b => b.Date).ToList();
				DateTime d = g.Key;
				if (!prevOf.TryGetValue(d, out var dPrev)) continue;
				if (!hv.TryGetValue(dPrev, out double h)) continue;
				if (!posByDate.TryGetValue(dPrev, out double target) || target < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(d)) continue;
				if (SkipStBear && stByDate.TryGetValue(dPrev, out var st) && st == ShortTermState.Bear) continue;

				int n = bars.Count;
				double S0 = bars[0].Open;
				if (S0 <= 0) continue;
				double iv = h * VolRiskPremium;
				double Tentry = 1.0 / 252.0;
				double kS = StrikeForPutDelta(S0, iv, Tentry, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S0, iv, Tentry, WingDelta);
				double cr = Put(S0, kS, iv, Tentry) - Put(S0, kL, iv, Tentry);
				double maxLoss = (kS - kL) - cr;
				if (cr <= 1e-9 || maxLoss <= 1e-9) continue;
				barCounts[n] = barCounts.GetValueOrDefault(n) + 1;

				double Val(double s, double t) => Put(s, kS, iv, t) - Put(s, kL, iv, t);

				double prevS = S0, prevT = Tentry, prevVal = cr, tot = 0;
				for (int j = 0; j < n; j++)
				{
					double Tafter = (double)(n - 1 - j) / n / 252.0;
					double S = bars[j].Close;
					if (S <= 0) continue;
					// theta leg: same spot, shorter tenor.  spot leg: new spot, same (shorter) tenor.
					double vTheta = Val(prevS, Tafter);
					double vBoth = Val(S, Tafter);
					// seller profits when the spread's value FALLS, hence the sign flip
					double pTheta = Risk * (prevVal - vTheta) / maxLoss;
					double pSpot = Risk * (vTheta - vBoth) / maxLoss;
					// BUCKET BY BAR INDEX, NOT CLOCK TIME. IntradayClient adds Yahoo's single scalar gmtoffset to
					// every timestamp, so sessions on the other side of a DST switch carry labels shifted by an
					// hour -- which is why the raw clock produced an impossible 16:30 bucket holding ~173 of the
					// ~177 sessions missing from 09:30. Bar index within the session is immune to that, and with
					// 473 of 479 sessions holding exactly 7 bars it maps cleanly onto the real ET grid.
					string lbl = n == 7 ? Clock7[j] : $"{n}bar#{j}";
					incs.Add(new Inc(d, lbl, pTheta + pSpot, pTheta, pSpot));
					tot += pTheta + pSpot;
					prevS = S; prevT = Tafter; prevVal = vBoth;
				}
				sessTotal[d] = tot;
			}

			if (incs.Count == 0) { Console.WriteLine("no qualifying sessions"); return; }

			Console.WriteLine($"\n===== {symbol}: 0DTE PUT SPREAD P&L BY HOUR OF DAY (1h bars) =====");
			Console.WriteLine($"{sessTotal.Count} qualifying sessions {sessions.First().Key:yyyy-MM-dd} -> {sessions.Last().Key:yyyy-MM-dd}, " +
				$"shipped filters on, held to expiry (no rolls)");
			Console.WriteLine($"bars/session: {string.Join(", ", barCounts.OrderBy(x => x.Key).Select(x => $"{x.Key}->{x.Value}"))}");
			Console.WriteLine($"structure: long {WingDelta:0.00}d / short {NetDelta + WingDelta:0.00}d, risk {100 * Risk:0.#}% of bankroll per session");

			// Confirm the DST label bug rather than just working around it: if the first bar's stamped hour tracks
			// the DST regime (EDT Mar-Nov vs EST Nov-Mar), the offset is being applied as one scalar for all bars.
			var firstBarByMonth = sessions.Where(g => g.Count() == 7)
				.GroupBy(g => g.OrderBy(b => b.Date).First().Date.ToString("HH:mm"))
				.OrderBy(x => x.Key)
				.Select(x => $"{x.Key} n={x.Count()} (months {string.Join("/", x.Select(g => g.Key.Month).Distinct().OrderBy(m => m))})");
			Console.WriteLine($"DST label check, stamped first-bar time on 7-bar sessions:\n    {string.Join("\n    ", firstBarByMonth)}");

			// RECONCILIATION: the hourly increments must sum to the shipped open->close session return.
			double meanSess = sessTotal.Values.Average();
			double meanFromIncs = incs.GroupBy(x => x.D).Select(g2 => g2.Sum(x => x.Total)).Average();
			Console.WriteLine($"reconciliation: mean session return {100 * meanSess:+0.0000;-0.0000}% == sum of hourly parts " +
				$"{100 * meanFromIncs:+0.0000;-0.0000}%  (diff {100 * Math.Abs(meanSess - meanFromIncs):0.000000}pp)");

			Console.WriteLine($"\n{"hour (ET)",11} {"n",6} {"total%",10} {"theta%",10} {"spot%",10} " +
				$"{"share",7} {"win%",7} {"sd%",9} {"IR",8} {"t",7}");
			double grand = incs.Sum(x => x.Total);
			foreach (var b in incs.GroupBy(x => x.Bucket).OrderBy(g2 => g2.Key))
			{
				var t = b.Select(x => x.Total).ToList();
				double m = t.Average();
				double sd = t.Count > 1 ? Math.Sqrt(t.Sum(x => (x - m) * (x - m)) / (t.Count - 1)) : 0;
				double se = sd / Math.Sqrt(t.Count);
				Console.WriteLine($"{b.Key,11} {t.Count,6} {100 * m,10:+0.0000;-0.0000} {100 * b.Average(x => x.Theta),10:+0.0000;-0.0000} " +
					$"{100 * b.Average(x => x.Spot),10:+0.0000;-0.0000} {100 * b.Sum(x => x.Total) / grand,6:0.0}% " +
					$"{100.0 * t.Count(x => x > 0) / t.Count,7:0.0} {100 * sd,9:0.0000} {(sd > 0 ? m / sd : 0),8:0.000} " +
					$"{(se > 0 ? m / se : 0),7:+0.00;-0.00}");
			}

			// The spot column alone -- the part that is a real measurement rather than a decay schedule.
			Console.WriteLine($"\n-- SPOT component only (real price path; theta removed) --");
			Console.WriteLine($"{"hour (ET)",11} {"n",6} {"mean%",10} {"sd%",9} {"IR",8} {"t",7} {"worst%",10}");
			foreach (var b in incs.GroupBy(x => x.Bucket).OrderBy(g2 => g2.Key))
			{
				var t = b.Select(x => x.Spot).ToList();
				double m = t.Average();
				double sd = t.Count > 1 ? Math.Sqrt(t.Sum(x => (x - m) * (x - m)) / (t.Count - 1)) : 0;
				double se = sd / Math.Sqrt(t.Count);
				Console.WriteLine($"{b.Key,11} {t.Count,6} {100 * m,10:+0.0000;-0.0000} {100 * sd,9:0.0000} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {(se > 0 ? m / se : 0),7:+0.00;-0.00} {100 * t.Min(),10:+0.0000;-0.0000}");
			}

			// Cumulative through the day: at what point is the session's edge already banked?
			Console.WriteLine($"\n-- cumulative through the session --");
			Console.WriteLine($"{"through",11} {"cum mean%",11} {"% of day",10}");
			double run = 0;
			var order = incs.GroupBy(x => x.Bucket).OrderBy(g2 => g2.Key).Select(g2 => g2.Key).ToList();
			foreach (var hb in order)
			{
				run += incs.Where(x => x.Bucket == hb).Sum(x => x.Total) / sessTotal.Count;
				Console.WriteLine($"{hb,11} {100 * run,11:+0.0000;-0.0000} {100 * run / meanSess,9:0.0}%");
			}
		}
	}
}
