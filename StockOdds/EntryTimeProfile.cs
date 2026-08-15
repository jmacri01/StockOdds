using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// EVERY 5m CANDLE AS A CANDIDATE ENTRY.
	//
	// Every backtest in this project opens at the session OPEN and holds to expiry. That bakes in an
	// assumption nobody tested: that 9:30 is the right time to enter. This prices the SAME structure
	// (0.35d/0.15d, held to the close) entered at every 5m bar of the session, so the entry-time
	// profile is measured rather than assumed.
	//
	// IT IS ALSO THE CONTROL THE DIP RESULT NEEDS. Dip entry fires at a median of bar 7 and beat
	// open entry by +0.336pp. But if entering at bar 7 is simply better than entering at bar 0 on ANY
	// session, then "dip entry" is a time-of-day effect wearing an exposure label. The comparison that
	// separates them is dip-entry return against the average return of entering at the SAME BAR across
	// all sessions -- a time-matched control. Only the excess over that is attributable to exposure.
	//
	// A shorter tenor mechanically changes the trade: less credit, narrower strikes, a smaller max-loss
	// denominator. Return per unit of risk stays comparable because everything is expressed as a
	// fraction of that session's own max loss, but the frozen-IV artifact means very late entries
	// should be read with suspicion regardless of what they show.
	// ================================================================================================
	internal static class EntryTimeProfile
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static double Gate = 0.10;
		public static int MinBarsLeft = 6;
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };

		// Rets[j] = return entering at bar j and holding to the close (NaN if unpriceable).
		private sealed record Sess(string Sym, DateTime D, double PriorExp, int DipBar, int NBars, double[] Rets, double ROpen);

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

		private static double Trade(double S, double ST, double iv, double frac)
		{
			double T = Math.Max(1e-9, frac) / 252.0;
			double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
			double kL = StrikeForPutDelta(S, iv, T, WingDelta);
			double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
			double ml = (kS - kL) - cr;
			if (cr <= 1e-9 || ml <= 1e-9) return double.NaN;
			return (cr - Math.Max(0, kS - ST) + Math.Max(0, kL - ST)) / ml;
		}

		private static string Wk(DateTime d) => $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";

		public static async Task Run()
		{
			var all = new List<Sess>();
			int maxBars = 0;
			foreach (var symbol in Symbols)
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
				List<OhlcBar> intra;
				try { intra = await IntradayClient.GetAsync(symbol, "5m", "60d"); }
				catch { continue; }
				if (intra.Count < 100) continue;

				var pos = new Dictionary<DateTime, double>();
				for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
					pos[eng.ReturnDates[k].Date] = eng.Positions[k];
				var stm = new Dictionary<DateTime, ShortTermState>();
				for (int k = 0; k < eng.StState.Count && k < eng.ReturnDates.Count; k++)
					stm[eng.ReturnDates[k].Date] = eng.StState[k];
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
				var iEng = BankrollSimulator.Run(intra, 10_000.0);
				var expPath = new Dictionary<DateTime, List<double>>();
				for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
				{
					var d = iEng.ReturnDates[k].Date;
					if (!expPath.TryGetValue(d, out var lst)) expPath[d] = lst = new List<double>();
					lst.Add(iEng.Positions[k]);
				}
				var barsOf = intra.GroupBy(b => b.Date.Date).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());

				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					if (!expPath.TryGetValue(dSig, out var pPrev) || pPrev.Count == 0) continue;
					if (!expPath.TryGetValue(dTr, out var pToday) || !barsOf.TryGetValue(dTr, out var tb)) continue;
					int n = Math.Min(pToday.Count, tb.Count);
					if (n < 20) continue;
					double iv = h * VolRiskPremium, ST = tb[n - 1].Close;
					if (ST <= 0) continue;
					double rOpen = Trade(tb[0].Open, ST, iv, 1.0);
					if (double.IsNaN(rOpen)) continue;

					var rets = new double[n];
					for (int j = 0; j < n; j++)
					{
						double Sj = tb[j].Close;
						rets[j] = (Sj > 0 && n - 1 - j >= MinBarsLeft) ? Trade(Sj, ST, iv, (double)(n - 1 - j) / n) : double.NaN;
					}
					int dip = -1;
					for (int j = 0; j < n - MinBarsLeft; j++) if (pToday[j] < Gate) { dip = j; break; }
					maxBars = Math.Max(maxBars, n);
					all.Add(new Sess(symbol, dTr, pPrev[^1], dip, n, rets, rOpen));
				}
			}
			if (all.Count == 0) { Console.WriteLine("no data"); return; }

			var worst = all.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.ROpen)).First().Key;
			var exW = all.Where(x => Wk(x.D) != worst).ToList();
			Console.WriteLine($"\n===== ENTRY-TIME PROFILE: the same spread entered at every 5m bar =====");
			Console.WriteLine($"{all.Count} sessions ({exW.Count} ex-{worst}), up to {maxBars} bars; " +
				$"structure and exit are identical, only the entry bar moves");

			// The profile itself, on the W23-removed sample.
			Console.WriteLine($"\n{"bar",5} {"~time",8} {"n",5} {"mean%",10} {"win%",7} {"IR",8}");
			var profile = new Dictionary<int, double>();
			for (int j = 0; j < maxBars; j += 3)
			{
				var v = exW.Where(x => j < x.NBars && !double.IsNaN(x.Rets[j])).Select(x => Risk * x.Rets[j]).ToList();
				if (v.Count < 20) continue;
				double m = v.Average();
				double sd = Math.Sqrt(v.Sum(z => (z - m) * (z - m)) / (v.Count - 1));
				profile[j] = m;
				var t0 = new TimeSpan(9, 30, 0).Add(TimeSpan.FromMinutes(5 * (j + 1)));
				Console.WriteLine($"{j,5} {t0:hh\\:mm,8} {v.Count,5} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * v.Count(z => z > 0) / v.Count,7:0.0} {(sd > 1e-12 ? m / sd : 0),8:0.000}");
			}

			// TIME-MATCHED CONTROL. For each dip session, compare its dip-bar return against the average
			// return of entering at that SAME bar across all sessions. The excess over that is what the
			// exposure condition contributes beyond simply entering later in the day.
			double Bar(int j)
			{
				var v = exW.Where(x => j < x.NBars && !double.IsNaN(x.Rets[j])).Select(x => Risk * x.Rets[j]).ToList();
				return v.Count >= 15 ? v.Average() : double.NaN;
			}
			var barMean = new Dictionary<int, double>();
			for (int j = 0; j < maxBars; j++) barMean[j] = Bar(j);

			var dipS = exW.Where(x => x.PriorExp >= Gate && x.DipBar >= 0 && !double.IsNaN(x.Rets[x.DipBar])).ToList();
			Console.WriteLine($"\n-- TIME-MATCHED CONTROL on the {dipS.Count} sessions the gate would skip --");
			if (dipS.Count >= 12)
			{
				var vsOpen = dipS.Select(x => Risk * (x.Rets[x.DipBar] - x.ROpen)).ToList();
				var vsTime = dipS.Where(x => !double.IsNaN(barMean[x.DipBar]))
					.Select(x => Risk * x.Rets[x.DipBar] - barMean[x.DipBar]).ToList();
				void P(string lbl, List<double> d)
				{
					double m = d.Average();
					double sd = Math.Sqrt(d.Sum(z => (z - m) * (z - m)) / (d.Count - 1));
					Console.WriteLine($"   {lbl,-46} n={d.Count,3}  {100 * m,9:+0.0000;-0.0000}pp  t {m / (sd / Math.Sqrt(d.Count)),6:+0.00;-0.00}");
				}
				P("dip entry minus SAME-SESSION open entry", vsOpen);
				P("dip entry minus AVERAGE entry at that same bar", vsTime);
				Console.WriteLine("   the second line is the one attributable to exposure; the first mixes in time-of-day");
			}

			// Does entering later help ON ITS OWN, ignoring exposure entirely?
			Console.WriteLine($"\n-- entering at a FIXED bar, every session, no exposure condition --");
			Console.WriteLine($"{"bar",5} {"~time",8} {"n",5} {"mean%",10} {"IR",8}  (vs bar 0)");
			double b0 = barMean.TryGetValue(0, out var v0) ? v0 : double.NaN;
			foreach (int j in new[] { 0, 3, 7, 12, 19, 30, 45 })
			{
				var v = exW.Where(x => j < x.NBars && !double.IsNaN(x.Rets[j])).Select(x => Risk * x.Rets[j]).ToList();
				if (v.Count < 20) continue;
				double m = v.Average();
				double sd = Math.Sqrt(v.Sum(z => (z - m) * (z - m)) / (v.Count - 1));
				var t0 = new TimeSpan(9, 30, 0).Add(TimeSpan.FromMinutes(5 * (j + 1)));
				Console.WriteLine($"{j,5} {t0:hh\\:mm,8} {v.Count,5} {100 * m,10:+0.0000;-0.0000} {(sd > 1e-12 ? m / sd : 0),8:0.000}  " +
					$"{(double.IsNaN(b0) ? 0 : 100 * (m - b0)),+8:+0.0000;-0.0000}");
			}
		}
	}
}
