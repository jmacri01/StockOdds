using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// GATE THE 0DTE SPREAD ON THE *INTRADAY* ENGINE'S EXPOSURE.
	//
	// Idea: only sell the put spread when the fast (intraday) engine is flat -- exposure 0, or < 0.10.
	// The structure is bullish/neutral, so this is deliberately contrarian: trade it when the fast
	// signal is washed out, on the theory that a flushed intraday tape is where premium is richest and
	// the bounce lives.
	//
	// TIMING. The spread opens at the session OPEN, so the gate may only use information available by
	// then. Exposure is therefore read at the LAST intraday bar of the PRIOR session. Reading the
	// current session's own bars would be look-ahead relative to the entry.
	//
	// INTERVAL. 5m is what was asked for, but Yahoo serves 5m for 60 days only and it is not
	// stitchable, which caps that arm at ~59 sessions -- far too few to conclude anything. 1h reaches
	// 730d and carries the real test; the 5m arm runs anyway, labelled for what it is.
	// ================================================================================================
	internal static class IntradayExposureGate
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;

		private sealed record Tr(DateTime D, double R, double IntraExp);

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

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(daily, 10_000.0);

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

			var byInterval = new Dictionary<string, List<Tr>>();
			foreach (var (interval, range) in new[] { ("1h", "730d"), ("5m", "60d") })
			{
				var intra = await IntradayClient.GetAsync(symbol, interval, range);
				if (intra.Count < 100) { Console.WriteLine($"\n{interval}: not enough data"); continue; }

				// Intraday engine -> exposure at the CLOSE of each session (the last bar of that date).
				var iEng = BankrollSimulator.Run(intra, 10_000.0);
				var lastExpOfDay = new Dictionary<DateTime, double>();
				for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
					lastExpOfDay[iEng.ReturnDates[k].Date] = iEng.Positions[k];   // later bars overwrite -> last wins

				var tr = new List<Tr>();
				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					// the gate: intraday exposure as of the PRIOR session's last bar
					if (!lastExpOfDay.TryGetValue(dSig, out double iexp)) continue;
					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double T = 1.0 / 252.0, iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					tr.Add(new Tr(dTr, (cr + po) / ml, iexp));
				}

				byInterval[interval] = tr;
				Console.WriteLine($"\n===== {symbol}: 0DTE GATED ON {interval} INTRADAY EXPOSURE =====");
				if (tr.Count < 20) { Console.WriteLine($"only {tr.Count} sessions overlap -- nothing to say"); continue; }
				Console.WriteLine($"{tr.Count} sessions {tr.Min(x => x.D):yyyy-MM-dd} -> {tr.Max(x => x.D):yyyy-MM-dd}" +
					(interval == "5m" ? "   *** ~60d Yahoo cap: UNDERPOWERED, directional peek only ***" : ""));
				var ex = tr.Select(x => x.IntraExp).OrderBy(v => v).ToList();
				Console.WriteLine($"intraday exposure distribution: min {ex[0]:0.00}, p25 {ex[ex.Count / 4]:0.00}, " +
					$"median {ex[ex.Count / 2]:0.00}, p75 {ex[3 * ex.Count / 4]:0.00}, max {ex[^1]:0.00}   " +
					$"exactly 0: {100.0 * tr.Count(x => x.IntraExp <= 1e-9) / tr.Count:0.0}%, " +
					$"< 0.10: {100.0 * tr.Count(x => x.IntraExp < 0.10) / tr.Count:0.0}%");

				double yrs = (tr.Max(x => x.D) - tr.Min(x => x.D)).TotalDays / 365.25;
				Console.WriteLine($"\n{"gate",-30} {"n",6} {"%kept",7} {"mean%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"annSh",8}");
				void G(string lbl, IEnumerable<Tr> src)
				{
					var t = src.ToList();
					if (t.Count < 15) { Console.WriteLine($"{lbl,-30} {t.Count,6}   (too few)"); return; }
					var r = t.Select(x => Risk * x.R).ToList();
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					double e = 1, pk = 1, dd = 0;
					foreach (var z in r) { e *= 1 + z; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
					double ir = sd > 0 ? m / sd : 0, perYr = t.Count / yrs;
					Console.WriteLine($"{lbl,-30} {t.Count,6} {100.0 * t.Count / tr.Count,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
						$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {ir,8:0.000} {dd,8:0.00} {ir * Math.Sqrt(perYr),8:0.00}");
				}
				G("no gate [SHIPPED]", tr);
				G("intraday exp == 0", tr.Where(x => x.IntraExp <= 1e-9));
				G("intraday exp < 0.10", tr.Where(x => x.IntraExp < 0.10));
				G("intraday exp < 0.25", tr.Where(x => x.IntraExp < 0.25));
				G("intraday exp < 0.50", tr.Where(x => x.IntraExp < 0.50));
				G("intraday exp >= 0.50 (control)", tr.Where(x => x.IntraExp >= 0.50));
				G("intraday exp >= 1.00 (control)", tr.Where(x => x.IntraExp >= 1.00));

				// Dose-response by quartile of the name's own intraday-exposure distribution. A real signal
				// should be monotone here; a lucky threshold will not be.
				Console.WriteLine($"\n  -- quartiles of intraday exposure (dose-response check) --");
				var srt = tr.OrderBy(x => x.IntraExp).ToList();
				for (int q = 0; q < 4; q++)
				{
					var slice = srt.Skip(q * srt.Count / 4).Take(srt.Count / 4 + (q == 3 ? srt.Count % 4 : 0)).ToList();
					G($"  Q{q + 1} exp {slice.Min(x => x.IntraExp):0.00}-{slice.Max(x => x.IntraExp):0.00}", slice);
				}
			}

			// ---- IS 5m A DIFFERENT SIGNAL, OR JUST A DIFFERENT WINDOW? ---------------------------------
			// The 5m arm can only see ~60 days. If the 1h gate ALSO looks good when restricted to those same
			// sessions, the apparent 5m edge is the regime, not the interval -- and no amount of 5m data
			// would have shown it. This is the only way to tell the two apart without more history.
			if (byInterval.TryGetValue("1h", out var h1) && byInterval.TryGetValue("5m", out var m5) && m5.Count >= 20)
			{
				var lo = m5.Min(x => x.D); var hi = m5.Max(x => x.D);
				var win = h1.Where(x => x.D >= lo && x.D <= hi).ToList();
				Console.WriteLine($"\n===== CONTROL: the 1h gate on the SAME {lo:yyyy-MM-dd} -> {hi:yyyy-MM-dd} window =====");
				Console.WriteLine($"{win.Count} sessions (vs {m5.Count} for 5m)");
				if (win.Count >= 15)
				{
					double yy = Math.Max(0.01, (hi - lo).TotalDays / 365.25);
					Console.WriteLine($"{"gate",-30} {"n",6} {"%kept",7} {"mean%",10} {"win%",7} {"IR",8}");
					void W(string lbl, IEnumerable<Tr> src)
					{
						var t = src.ToList();
						if (t.Count < 8) { Console.WriteLine($"{lbl,-30} {t.Count,6}   (too few)"); return; }
						var r = t.Select(x => Risk * x.R).ToList();
						double m = r.Average();
						double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
						Console.WriteLine($"{lbl,-30} {t.Count,6} {100.0 * t.Count / win.Count,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
							$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000}");
					}
					W("1h: no gate, this window", win);
					W("1h: exp < 0.10, this window", win.Where(x => x.IntraExp < 0.10));
					W("1h: exp < 0.25, this window", win.Where(x => x.IntraExp < 0.25));
				}
			}
		}
	}
}
