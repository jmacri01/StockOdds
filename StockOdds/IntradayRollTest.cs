using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Take profit at X% of the credit intraday and re-open, instead of holding the 0-DTE spread to the close.
	//
	// This cannot be answered on daily bars -- they cannot say WHEN a profit level was reached -- so the session is
	// walked on 1h bars (Yahoo serves 730 days of them; 5m is capped at 60 days and is not stitchable).
	//
	// WHY THE ANSWER IS NOT OBVIOUS. Closing at 50% caps the winner but frees the position from the last hours of
	// gamma, which for a 0-DTE short put is when a late slide does the real damage. Re-opening then re-arms a fresh
	// spread with less time on it. So the rule trades tail exposure for capped upside AND adds transaction count --
	// and the transaction cost is now MEASURED rather than guessed: a live SPY chain put the mid-to-cross drag at
	// 2.6% of the credit, and every roll pays it TWICE (close one, open the next).
	//
	// MODELLING CHOICES, all of which flatter rolling and are stated so the result is read correctly:
	//   * IV is held constant through the session at the entry value. Real 0-DTE IV does not sit still, and the
	//     late-day gamma regime is exactly where that assumption is weakest.
	//   * Time decays linearly across the session: after k of n hourly bars, T = (n-k)/n * (1/252).
	//   * TouchMode.High treats the target as a resting limit order filled exactly at the target if the bar's HIGH
	//     would have reached it (profit on a put spread rises with spot). TouchMode.Close only ever acts on bar
	//     closes -- strictly executable, and the honest lower bound. Both are reported.
	//   * Each re-opened leg is sized to the SAME max-loss budget, so a session that rolls twice puts more capital
	//     at risk than one that does not. Legs-per-session is reported for that reason.
	public static class IntradayRollTest
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;
		public static double[] ProfitTargets = { 0.25, 0.50, 0.75 };
		public static double[] CostPctOfCredit = { 0.0, 2.6 };   // per transaction; 2.6% is the measured mid->cross
		// LATE-SESSION RE-OPEN GUARD. With IV frozen and theta deterministic, the modelled spread value converges
		// to intrinsic as T -> 0, so a profit target gets hit by DECAY ALONE near the close whether or not spot
		// cooperated. Re-opening there sells a razor-thin spread whose tiny max-loss denominator is then levered to
		// the full risk budget -- enormous modelled size in a spread that would be unquotable, with a bid-ask
		// comparable to the entire credit. Requiring bars to remain before re-opening is what separates a real
		// effect from that artifact.
		public static int MinBarsLeftToReopen = 0;
		// Take the profit and STAND DOWN for the session, rather than re-arming. Isolates "cap the winner and dodge
		// late gamma" from "trade more times per day", which the re-opening version conflates.
		public static bool NoReopen = false;

		private sealed record Sess(DateTime D, double Ret, int Legs);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(daily, 10_000.0);
			var intraday = await IntradayClient.GetAsync(symbol, "1h", "730d");

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

			// signal from the PRIOR daily close; the session traded is the next one
			var prevOf = new Dictionary<DateTime, DateTime>();
			for (int i = 1; i < daily.Count; i++) prevOf[daily[i].Date.Date] = daily[i - 1].Date.Date;

			var sessions = intraday.GroupBy(b => b.Date.Date).Where(g => g.Count() >= 4)
			                       .OrderBy(g => g.Key).ToList();
			Console.WriteLine($"\n===== {symbol}: INTRADAY PROFIT-TAKE AND RE-OPEN (1h bars) =====");
			Console.WriteLine($"{sessions.Count} intraday sessions {sessions.First().Key:yyyy-MM-dd} -> {sessions.Last().Key:yyyy-MM-dd} | " +
				$"median {sessions.Select(g => g.Count()).OrderBy(x => x).ElementAt(sessions.Count / 2)} bars/session");
			Console.WriteLine($"structure: long {WingDelta:0.00}d / short {NetDelta + WingDelta:0.00}d, net {NetDelta:0.00}; " +
				$"risk {100 * Risk:0.#}% per LEG; measured mid->cross drag 2.6% of credit per transaction");

			List<Sess> Simulate(double? profitTarget, bool touchHigh, double costPct)
			{
				var outp = new List<Sess>();
				foreach (var g in sessions)
				{
					var bars = g.OrderBy(b => b.Date).ToList();
					DateTime d = g.Key;
					if (!prevOf.TryGetValue(d, out var dPrev)) continue;
					if (!hv.TryGetValue(dPrev, out double h)) continue;
					if (!posByDate.TryGetValue(dPrev, out double target)) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(d)) continue;
					if (target < TargetLo) continue;
					if (SkipStBear && stByDate.TryGetValue(dPrev, out var st) && st == ShortTermState.Bear) continue;

					double iv = h * VolRiskPremium;
					int n = bars.Count;
					double total = 0; int legs = 0;
					int k = 0;                                   // bar index at which the current leg is open
					double S0 = bars[0].Open;
					if (S0 <= 0) continue;

					while (k < n)
					{
						double Topen = (double)(n - k) / n / 252.0;
						if (Topen <= 1e-9) break;
						double S = k == 0 ? S0 : bars[k].Close;
						double kS = StrikeForPutDelta(S, iv, Topen, NetDelta + WingDelta);
						double kL = StrikeForPutDelta(S, iv, Topen, WingDelta);
						double cr = Put(S, kS, iv, Topen) - Put(S, kL, iv, Topen);
						double width = kS - kL, maxLoss = width - cr;
						if (cr <= 1e-9 || maxLoss <= 1e-9) break;
						legs++;
						double entryCost = cr * costPct / 100.0;

						bool closedEarly = false;
						int j = k;
						for (j = k + 1; j < n; j++)
						{
							double Trem = (double)(n - j) / n / 252.0;
							if (profitTarget is not double pt) continue;
							// profit on a put credit spread rises with spot, so the best point inside a bar is its HIGH
							double Sp = touchHigh ? bars[j].High : bars[j].Close;
							double val = Put(Sp, kS, iv, Trem) - Put(Sp, kL, iv, Trem);
							if (cr - val >= pt * cr)
							{
								// resting limit order: assume the fill happens AT the target, not at the extreme
								double gain = pt * cr;
								double exitCost = (cr - gain) * costPct / 100.0;
								total += Risk * (gain - entryCost - exitCost) / maxLoss;
								closedEarly = true;
								break;
							}
						}
						if (!closedEarly)
						{
							double ST = bars[n - 1].Close;
							double payoff = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
							total += Risk * (cr + payoff - entryCost) / maxLoss;   // expires, no exit crossing
							break;
						}
						if (NoReopen) break;                      // banked the profit, flat for the rest of the session
						if (n - j <= MinBarsLeftToReopen) break;  // too little time left for a quotable spread
						k = j;                                    // re-open at the bar where the target was hit
					}
					outp.Add(new Sess(d, total, legs));
				}
				return outp;
			}

			void Show(string label, List<Sess> s)
			{
				if (s.Count < 30) { Console.WriteLine($"{label,38} {s.Count,7}  (too few)"); return; }
				var r = s.Select(x => x.Ret).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double yrs = Math.Max(0.5, (s.Last().D - s.First().D).TotalDays / 365.25);
				var (final, dd) = Curve(r);
				double cagr = final > -100 ? (Math.Pow(1 + final / 100.0, 1 / yrs) - 1) * 100 : double.NaN;
				Console.WriteLine($"{label,38} {s.Count,7} {s.Average(x => (double)x.Legs),7:0.00} " +
					$"{100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {(sd > 0 ? m / sd * Math.Sqrt(s.Count / yrs) : 0),8:0.000} " +
					$"{dd,8:0.00} {cagr,9:0.0} {100 * r.Min(),8:0.00}");
			}

			foreach (double cost in CostPctOfCredit)
			{
				Console.WriteLine($"\n### transaction cost {cost:0.0}% of credit per fill" +
					(cost > 0 ? "  (MEASURED mid->cross)" : "  (frictionless -- upper bound only)"));
				Console.WriteLine($"{"rule",38} {"sess",7} {"legs",7} {"mean/sess%",10} {"win%",7} {"IR",8} " +
					$"{"Sharpe",8} {"maxDD%",8} {"CAGR%",9} {"worst%",8}");
				Show("hold to close [SHIPPED]", Simulate(null, false, cost));
				int savedMin = MinBarsLeftToReopen; bool savedNo = NoReopen;
				foreach (double pt in ProfitTargets)
				{
					NoReopen = true; MinBarsLeftToReopen = 0;
					Show($"take {100 * pt:0}%, STAND DOWN (touch)", Simulate(pt, true, cost));
					NoReopen = false;
					foreach (int guard in new[] { 0, 2 })
					{
						MinBarsLeftToReopen = guard;
						Show($"take {100 * pt:0}%, re-open (>{guard} bars left)", Simulate(pt, true, cost));
					}
				}
				MinBarsLeftToReopen = savedMin; NoReopen = savedNo;
			}
			Console.WriteLine("\nA 1h grid sees at most ~6 decision points, so it UNDERSTATES how often a target is touched;");
			Console.WriteLine("the intrabar-touch rows bracket that. Both assume IV is frozen through the session, which is");
			Console.WriteLine("least true in the final hours -- precisely the window the rule is meant to avoid.");
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
