using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Close the spread on the qualified delta floor (delta < 0.10 AND spot above the short strike = a win), then
	// re-open ONLY once the intraday chart prints ST Bear -- i.e. wait for a pullback instead of re-selling at the
	// top of the move that just paid you.
	//
	// The rationale is sound: a floor trip means price rallied away, so re-arming immediately sells a fresh spread
	// struck off an extended price. Waiting for short-term weakness should give a better strike and more premium
	// for the same delta.
	//
	// The ST state here is computed on the INTRADAY bars, not the daily ones -- the engine is scale-free, so the
	// same state machine runs on 1h or 5m. That is the whole point of the rule and it is why the existing roll
	// harness could not express it: that one only knows the prior DAILY state.
	//
	// SAMPLE. Floor trips run ~0.27 per session, so 1h over 730 days gives roughly 130 rolls to filter -- thin but
	// measurable. 5m is capped at 60 days (~59 sessions, so under 20 rolls) and cannot support a conclusion; it is
	// run anyway because the question was asked about 5m, and labelled accordingly.
	//
	// Same caveats as every intraday test here: IV frozen through the session, linear intraday theta, measured
	// 2.6%-of-credit drag per fill, and no re-entry inside the last 2 bars.
	public static class RollReentryFilter
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;          // daily ST Bear skip, as shipped
		public static double CostPct = 2.6;
		public static int    MinBarsLeftToRoll = 2;

		private sealed record Sess(DateTime D, double Ret, int Rolls, int Waits);

		public static async Task Run(string symbol = "SPY")
		{
			foreach (var (interval, range) in new[] { ("1h", "730d"), ("5m", "60d") })
				await One(symbol, interval, range);
		}

		// mode 0 = re-open immediately (the shipped floor-qualified roll)
		// mode 1 = re-open only if the intraday state is ST Bear at the roll bar, else stand down
		// mode 2 = after rolling, WAIT for the first later bar in ST Bear and re-open there
		// mode 3 = never re-open (bank and stand down) -- isolates how much the re-entry is worth at all
		// mode 4 = re-open at the first later BEAR CANDLE (close < prior bar's low)
		// mode 5 = re-open at the first later DOWN CLOSE (close < prior close) -- a weaker, more frequent trigger
		//
		// Modes 4-5 exist because the ST-state version could not fire: a floor trip means price RALLIED, and a
		// run-based state machine cannot print Bear within the few bars that remain. A single-bar condition can.
		private static async Task One(string symbol, string interval, string range)
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var dEng = BankrollSimulator.Run(daily, 10_000.0);
			var intra = await IntradayClient.GetAsync(symbol, interval, range);
			if (intra.Count < 200) { Console.WriteLine($"{interval}: not enough data"); return; }
			var iEng = BankrollSimulator.Run(intra, 10_000.0);      // scale-free: same engine, finer step

			var pos = new Dictionary<DateTime, double>();
			for (int k = 0; k < dEng.Positions.Count && k < dEng.ReturnDates.Count; k++)
				pos[dEng.ReturnDates[k].Date] = dEng.Positions[k];
			var dSt = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < dEng.StState.Count && k < dEng.ReturnDates.Count; k++)
				dSt[dEng.ReturnDates[k].Date] = dEng.StState[k];
			var iSt = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < iEng.StState.Count && k < iEng.ReturnDates.Count; k++)
				iSt[iEng.ReturnDates[k]] = iEng.StState[k];

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
			var sessions = intra.GroupBy(b => b.Date.Date).Where(g => g.Count() >= 5).OrderBy(g => g.Key).ToList();

			List<Sess> Sim(int mode)
			{
				var outp = new List<Sess>();
				foreach (var g in sessions)
				{
					var bars = g.OrderBy(b => b.Date).ToList();
					int n = bars.Count;
					if (!prevOf.TryGetValue(g.Key, out var dp)) continue;
					if (!hv.TryGetValue(dp, out double h)) continue;
					if (!pos.TryGetValue(dp, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(g.Key)) continue;
					if (SkipStBear && dSt.TryGetValue(dp, out var st0) && st0 == ShortTermState.Bear) continue;
					double iv = h * VolRiskPremium, S0 = bars[0].Open;
					if (S0 <= 0) continue;

					double total = 0; int rolls = 0, waits = 0, k = 0;
					while (k < n - 1)
					{
						double Topen = (double)(n - k) / n / 252.0;
						double S = k == 0 ? S0 : bars[k].Close;
						double kS = StrikeForPutDelta(S, iv, Topen, NetDelta + WingDelta);
						double kL = StrikeForPutDelta(S, iv, Topen, WingDelta);
						double cr = Put(S, kS, iv, Topen) - Put(S, kL, iv, Topen);
						double ml = (kS - kL) - cr;
						if (cr <= 1e-9 || ml <= 1e-9) break;
						double entryCost = cr * CostPct / 100.0;

						int j; bool rolled = false;
						for (j = k + 1; j < n; j++)
						{
							double Trem = (double)(n - j) / n / 252.0;
							if (Trem <= 1e-9) break;
							double Sp = bars[j].Close;
							double nd = PutDeltaMag(Sp, kS, iv, Trem) - PutDeltaMag(Sp, kL, iv, Trem);
							// qualified floor only: low delta AND spot above the short strike (a genuine win)
							if (!(nd < 0.10 && Sp > kS)) continue;
							if (n - j <= MinBarsLeftToRoll) break;
							double val = Put(Sp, kS, iv, Trem) - Put(Sp, kL, iv, Trem);
							total += Risk * (cr - val - entryCost - val * CostPct / 100.0) / ml;
							rolls++; rolled = true; break;
						}
						if (!rolled)
						{
							double ST = bars[n - 1].Close;
							total += Risk * (cr + (-Math.Max(0, kS - ST) + Math.Max(0, kL - ST)) - entryCost) / ml;
							break;
						}
						if (mode == 3) break;                                  // banked, stand down
						bool BearAt(int idx) => iSt.TryGetValue(bars[idx].Date, out var s) && s == ShortTermState.Bear;
						// single-bar weakness: a bear CANDLE closes below the prior bar's low; a down close merely
						// closes below the prior close. Neither needs a run, so both can trigger immediately.
						bool BearCandleAt(int idx) => idx >= 1 && bars[idx].Close < bars[idx - 1].Low;
						bool DownCloseAt(int idx) => idx >= 1 && bars[idx].Close < bars[idx - 1].Close;
						if (mode == 1)
						{
							if (!BearAt(j)) break;                             // not weak here, stand down
							k = j;
						}
						else if (mode == 2)
						{
							int w = -1;
							for (int q = j + 1; q < n - MinBarsLeftToRoll; q++) if (BearAt(q)) { w = q; break; }
							if (w < 0) break;                                  // never pulled back, stay flat
							waits++; k = w;
						}
						else if (mode == 4 || mode == 5)
						{
							int w = -1;
							for (int q = j + 1; q < n - MinBarsLeftToRoll; q++)
								if (mode == 4 ? BearCandleAt(q) : DownCloseAt(q)) { w = q; break; }
							if (w < 0) break;                                  // no weakness appeared; stay flat
							waits++; k = w;
						}
						else k = j;
					}
					outp.Add(new Sess(g.Key, total, rolls, waits));
				}
				return outp;
			}

			Console.WriteLine($"\n===== {symbol} {interval}: RE-OPEN ONLY IN INTRADAY ST BEAR =====");
			Console.WriteLine($"{sessions.Count} sessions {sessions.First().Key:yyyy-MM-dd} -> {sessions.Last().Key:yyyy-MM-dd} | " +
				$"ST state computed on the {interval} bars" + (interval == "5m" ? "  << 60-day cap, indicative only" : ""));
			Console.WriteLine($"{"rule",42} {"sess",6} {"rolls",7} {"waits",7} {"mean/sess%",11} {"win%",7} {"IR",8} " +
				$"{"maxDD%",8} {"worst%",8}");
			void Show(string lbl, List<Sess> s)
			{
				if (s.Count < 25) { Console.WriteLine($"{lbl,42} {s.Count,6}  (too few)"); return; }
				var r = s.Select(x => x.Ret).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				Console.WriteLine($"{lbl,42} {s.Count,6} {s.Average(x => (double)x.Rolls),7:0.00} " +
					$"{s.Average(x => (double)x.Waits),7:0.00} {100 * m,11:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {100 * r.Min(),8:0.00}");
			}
			Show("re-open immediately [current best]", Sim(0));
			Show("re-open only if ST Bear at the roll", Sim(1));
			Show("WAIT for ST Bear, then re-open", Sim(2));
			Show("bank and stand down (no re-open)", Sim(3));
			Show("WAIT for a BEAR CANDLE, then re-open", Sim(4));
			Show("WAIT for any DOWN CLOSE, then re-open", Sim(5));
			Console.WriteLine("  'stand down' bounds how much the re-entry is worth at all; the ST-Bear variants sit");
			Console.WriteLine("  between it and re-opening immediately, so that is the range they have to beat.");
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
			for (int i = 0; i < 80; i++) { double mid = 0.5 * (lo + hi); if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
		}
	}
}
