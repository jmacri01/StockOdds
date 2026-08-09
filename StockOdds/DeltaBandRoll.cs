using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Re-centre the 0-DTE put spread intraday whenever its net delta leaves a band, instead of holding the
	// original strikes to expiry. Walked on 1h bars (730 days; 5m is capped at 60 and cannot carry a conclusion).
	//
	// THE RULE IS TWO TRADES WEARING ONE NAME, and they must be measured apart:
	//   delta DRIFTS BELOW the floor  -> spot RALLIED, the spread is nearly worthless, rolling banks the gain and
	//                                    re-arms closer to the money. This is profit-taking.
	//   delta DRIFTS ABOVE the ceiling -> spot FELL, the short leg is under pressure, rolling CRYSTALLISES the loss
	//                                    and re-sells at a lower strike. This is rolling a loser.
	// A combined result can easily be one of these carrying the other, so each side is also run alone.
	//
	// NON-MONOTONE DELTA. Net delta of a put spread is hump-shaped in spot, not monotone: as price collapses both
	// legs approach delta 1 and the DIFFERENCE returns toward zero. So a deep enough sell-off exits the top of the
	// band on the way down and re-enters it from above -- the ceiling trigger fires on MODERATE declines and can go
	// quiet in a rout, which is the opposite of a stop.
	//
	// Same modelling caveats as the profit-target roll: IV frozen through the session, linear intraday theta, and
	// the measured 2.6%-of-credit mid-to-cross drag charged on every fill (a roll pays it twice). Late-session
	// re-entry is guarded, because with frozen IV the modelled spread converges to intrinsic as T -> 0 and the
	// max-loss denominator collapses, levering a razor-thin unquotable spread to the full risk budget.
	public static class DeltaBandRoll
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;
		public static double CostPct = 2.6;
		public static int    MinBarsLeftToRoll = 2;
		// Report ST Bear separately instead of dropping it. Every ST Bear test so far used HOLD-to-expiry, so the
		// state has never been given the rolling rule -- and rolling out of a winner early is plausibly worth more
		// on the state whose problem is giving profits back.
		public static bool SplitStBear = false;

		private sealed record Sess(DateTime D, double Ret, int Rolls);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(daily, 10_000.0);
			var intra = await IntradayClient.GetAsync(symbol, "1h", "730d");

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
			var prevOf = new Dictionary<DateTime, DateTime>();
			for (int i = 1; i < daily.Count; i++) prevOf[daily[i].Date.Date] = daily[i - 1].Date.Date;
			var sessions = intra.GroupBy(b => b.Date.Date).Where(g => g.Count() >= 5).OrderBy(g => g.Key).ToList();

			Console.WriteLine($"\n===== {symbol}: RE-CENTRE ON A DELTA BAND (1h bars) =====");
			Console.WriteLine($"{sessions.Count} sessions {sessions.First().Key:yyyy-MM-dd} -> {sessions.Last().Key:yyyy-MM-dd} | " +
				$"target net delta {NetDelta:0.00} | cost {CostPct:0.0}%/fill | no re-entry inside the last {MinBarsLeftToRoll} bars");

			// lo/hi < 0 disables that side of the band
			// qualifyFloor: only treat a low delta as a roll signal when spot is ABOVE the short strike, i.e. the
			// delta is low because the spread WON. Without it the same trigger fires on a collapse through both
			// strikes, where deltas both approach 1 and their difference falls back under the floor at ~-97% of
			// max loss -- 10 of 237 trips, and the source of every -20% session.
			List<Sess> Sim(double lo, double hi, bool qualifyFloor = false)
			{
				var outp = new List<Sess>();
				foreach (var g in sessions)
				{
					var bars = g.OrderBy(b => b.Date).ToList();
					DateTime d = g.Key;
					int n = bars.Count;
					if (!prevOf.TryGetValue(d, out var dp)) continue;
					if (!hv.TryGetValue(dp, out double h)) continue;
					if (!pos.TryGetValue(dp, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(d)) continue;
					bool isBear = stm.TryGetValue(dp, out var st) && st == ShortTermState.Bear;
					if (SkipStBear && isBear) continue;
					if (SplitStBear && !isBear) continue;        // isolate the state rather than excluding it
					double iv = h * VolRiskPremium, S0 = bars[0].Open;
					if (S0 <= 0) continue;

					double total = 0; int rolls = 0, k = 0;
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
							bool floorTrip = lo >= 0 && nd < lo && (!qualifyFloor || Sp > kS);
							bool trip = floorTrip || (hi >= 0 && nd > hi);
							if (!trip) continue;
							if (n - j <= MinBarsLeftToRoll) break;      // too little left to re-arm sensibly
							double val = Put(Sp, kS, iv, Trem) - Put(Sp, kL, iv, Trem);
							total += Risk * (cr - val - entryCost - val * CostPct / 100.0) / ml;
							rolls++; rolled = true; break;
						}
						if (!rolled)
						{
							double ST = bars[n - 1].Close;
							double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
							total += Risk * (cr + po - entryCost) / ml;
							break;
						}
						k = j;
					}
					outp.Add(new Sess(d, total, rolls));
				}
				return outp;
			}

			Console.WriteLine($"\n{"rule",40} {"sess",6} {"rolls",7} {"mean/sess%",11} {"win%",7} {"IR",8} " +
				$"{"Sharpe",8} {"maxDD%",8} {"worst%",8}");
			void Show(string lbl, List<Sess> s)
			{
				if (s.Count < 30) { Console.WriteLine($"{lbl,40} {s.Count,6}  (too few)"); return; }
				var r = s.Select(x => x.Ret).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(0.5, (s.Last().D - s.First().D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,40} {s.Count,6} {s.Average(x => (double)x.Rolls),7:0.00} {100 * m,11:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} " +
					$"{(sd > 0 ? m / sd * Math.Sqrt(s.Count / yrs) : 0),8:0.000} {dd,8:0.00} {100 * r.Min(),8:0.00}");
			}
			// DIAGNOSTIC: a floor trip means net delta < 0.10, which happens BOTH when spot rallied away (won) and
			// when spot collapsed through both strikes (both legs deep ITM, deltas -> 1, difference -> 0 = max
			// loss). The rule cannot tell those apart from delta alone. Count them.
			{
				int won = 0, crashed = 0, other = 0; double wonPnl = 0, crashPnl = 0;
				foreach (var g in sessions)
				{
					var bs = g.OrderBy(b => b.Date).ToList(); int n = bs.Count;
					if (!prevOf.TryGetValue(g.Key, out var dp)) continue;
					if (!hv.TryGetValue(dp, out double h)) continue;
					if (!pos.TryGetValue(dp, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(g.Key)) continue;
					if (SkipStBear && stm.TryGetValue(dp, out var st0) && st0 == ShortTermState.Bear) continue;
					double iv = h * VolRiskPremium, S0 = bs[0].Open;
					if (S0 <= 0) continue;
					double Topen = 1.0 / 252.0;
					double kS = StrikeForPutDelta(S0, iv, Topen, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S0, iv, Topen, WingDelta);
					double cr = Put(S0, kS, iv, Topen) - Put(S0, kL, iv, Topen);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					for (int j = 1; j < n; j++)
					{
						double Trem = (double)(n - j) / n / 252.0;
						if (Trem <= 1e-9) break;
						double Sp = bs[j].Close;
						double nd = PutDeltaMag(Sp, kS, iv, Trem) - PutDeltaMag(Sp, kL, iv, Trem);
						if (nd >= 0.10) continue;
						double val = Put(Sp, kS, iv, Trem) - Put(Sp, kL, iv, Trem);
						double pnl = (cr - val) / ml;
						if (Sp > kS) { won++; wonPnl += pnl; }
						else if (Sp < kL) { crashed++; crashPnl += pnl; }
						else other++;
						break;
					}
				}
				int tot = won + crashed + other;
				Console.WriteLine($"\nFLOOR TRIPS (net delta < 0.10), by WHY the delta was low -- {tot} sessions:");
				Console.WriteLine($"   spot ABOVE short strike (won)      {won,5} ({100.0 * won / Math.Max(1, tot),4:0.0}%)  " +
					$"mean P&L at trip {(won > 0 ? 100 * wonPnl / won : 0),8:+0.00;-0.00}% of max loss");
				Console.WriteLine($"   spot BELOW long strike (max loss)  {crashed,5} ({100.0 * crashed / Math.Max(1, tot),4:0.0}%)  " +
					$"mean P&L at trip {(crashed > 0 ? 100 * crashPnl / crashed : 0),8:+0.00;-0.00}% of max loss");
				Console.WriteLine($"   spot between the strikes           {other,5} ({100.0 * other / Math.Max(1, tot),4:0.0}%)");
				Console.WriteLine("   The rule sees one number and cannot distinguish these. Rolling the second kind");
				Console.WriteLine("   realises a full loss and re-arms, which is where the -20% sessions come from.");
			}

			// CEILING DIAGNOSTIC. The floor needed a qualifier because a low delta has two opposite causes. Does
			// the ceiling? Delta should only RISE when spot falls -- a rally pushes both legs OTM and shrinks the
			// difference -- so a high delta ought to be unambiguous. Verified rather than assumed, and the P&L at
			// the trip shows how deep the hole is when the rule fires.
			{
				int below = 0, between = 0, above = 0; double pnlSum = 0; var moves = new List<double>();
				foreach (var g in sessions)
				{
					var bs = g.OrderBy(b => b.Date).ToList(); int n = bs.Count;
					if (!prevOf.TryGetValue(g.Key, out var dp)) continue;
					if (!hv.TryGetValue(dp, out double h)) continue;
					if (!pos.TryGetValue(dp, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(g.Key)) continue;
					if (SkipStBear && stm.TryGetValue(dp, out var st0) && st0 == ShortTermState.Bear) continue;
					double iv = h * VolRiskPremium, S0 = bs[0].Open;
					if (S0 <= 0) continue;
					double T0 = 1.0 / 252.0;
					double kS = StrikeForPutDelta(S0, iv, T0, NetDelta + WingDelta);
					double kL = StrikeForPutDelta(S0, iv, T0, WingDelta);
					double cr = Put(S0, kS, iv, T0) - Put(S0, kL, iv, T0);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					for (int j = 1; j < n; j++)
					{
						double Trem = (double)(n - j) / n / 252.0;
						if (Trem <= 1e-9) break;
						double Sp = bs[j].Close;
						double nd = PutDeltaMag(Sp, kS, iv, Trem) - PutDeltaMag(Sp, kL, iv, Trem);
						if (nd <= 0.30) continue;
						double val = Put(Sp, kS, iv, Trem) - Put(Sp, kL, iv, Trem);
						pnlSum += (cr - val) / ml;
						moves.Add((Sp - S0) / S0);
						if (Sp < kL) below++; else if (Sp < kS) between++; else above++;
						break;
					}
				}
				int tot = below + between + above;
				Console.WriteLine($"\nCEILING TRIPS (net delta > 0.30) -- {tot} sessions:");
				Console.WriteLine($"   spot ABOVE short strike (still OTM)   {above,5} ({100.0 * above / Math.Max(1, tot),4:0.0}%)");
				Console.WriteLine($"   spot BETWEEN the strikes             {between,5} ({100.0 * between / Math.Max(1, tot),4:0.0}%)");
				Console.WriteLine($"   spot BELOW long strike (max loss)    {below,5} ({100.0 * below / Math.Max(1, tot),4:0.0}%)");
				if (tot > 0)
				{
					moves.Sort();
					Console.WriteLine($"   mean P&L at the trip {100 * pnlSum / tot:+0.0;-0.0}% of max loss | " +
						$"spot move from the open: median {100 * moves[moves.Count / 2]:+0.00;-0.00}%, " +
						$"worst {100 * moves[0]:+0.00;-0.00}%");
				}
			}

			// ---- ST BEAR UNDER THE ROLL RULE -------------------------------------------------------------
			bool sk = SkipStBear, sp = SplitStBear;
			SkipStBear = false; SplitStBear = true;
			Console.WriteLine("");
			Console.WriteLine("--- ST BEAR SESSIONS ONLY (currently skipped), under each rule ---");
			Show("  ST Bear: hold to expiry", Sim(-1, -1));
			Show("  ST Bear: floor QUALIFIED", Sim(0.10, -1, true));
			Show("  ST Bear: band [0.10,0.30] QUALIFIED", Sim(0.10, 0.30, true));
			SplitStBear = false;
			Console.WriteLine("--- FULL BOOK with ST Bear RE-ADMITTED ---");
			Show("  all states: hold to expiry", Sim(-1, -1));
			Show("  all states: floor QUALIFIED", Sim(0.10, -1, true));
			Show("  all states: band QUALIFIED", Sim(0.10, 0.30, true));
			SkipStBear = sk; SplitStBear = sp;
			Console.WriteLine("--- shipped book, ST Bear skipped ---");

			Show("hold to expiry [SHIPPED]", Sim(-1, -1));
			Show("band [0.10, 0.30]  (both sides)", Sim(0.10, 0.30));
			Show("  floor only: roll if delta < 0.10", Sim(0.10, -1));
			Show("  ceiling only: roll if delta > 0.30", Sim(-1, 0.30));
			Show("band [0.05, 0.40]  (wider)", Sim(0.05, 0.40));
			Show("band [0.15, 0.25]  (tighter)", Sim(0.15, 0.25));
			Console.WriteLine("  --- floor qualified: only roll a low delta when spot is ABOVE the short strike ---");
			Show("floor QUALIFIED (delta<0.10 & winning)", Sim(0.10, -1, true));
			Show("band [0.10,0.30], floor QUALIFIED", Sim(0.10, 0.30, true));
			Show("band [0.15,0.25], floor QUALIFIED", Sim(0.15, 0.25, true));

			// CAGR by risk level. Compounded figures over this window (2023-09+) are era-inflated and should be
			// read ordinally against each other, not as forecasts -- the drawdown and worst-session columns are the
			// durable parts. Risk is swept because a rule that improves IR can always be re-levered, so comparing
			// two rules at ONE risk level says less than comparing them along the whole curve.
			Console.WriteLine("");
			Console.WriteLine($"--- CAGR / drawdown by risk level ---");
			Console.WriteLine($"{"arm",40} {"risk",6} {"sess",6} {"mean/sess%",11} {"IR",8} {"maxDD%",8} {"CAGR%",10} {"worst%",8}");
			var armHold = Sim(-1, -1);
			var armFloorQ = Sim(0.10, -1, true);
			var armBandQ = Sim(0.10, 0.30, true);
			void Lev(string lbl, List<Sess> s, double mult)
			{
				var r = s.Select(x => x.Ret * mult).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(0.5, (s.Last().D - s.First().D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,40} {100 * Risk * mult,5:0.#}% {s.Count,6} {100 * m,11:+0.0000;-0.0000} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} " +
					$"{100 * r.Min(),8:0.00}");
			}
			foreach (double mult in new[] { 0.5, 1.0, 1.5 })
			{
				Lev("hold to expiry [SHIPPED]", armHold, mult);
				Lev("floor QUALIFIED (delta<0.10 & winning)", armFloorQ, mult);
				Lev("band [0.10,0.30], floor QUALIFIED", armBandQ, mult);
				Console.WriteLine();
			}
			Console.WriteLine("\nFloor-only is profit-taking; ceiling-only is rolling a loser. If the combined rule looks");
			Console.WriteLine("acceptable while one side is clearly bad, the other side is carrying it.");
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
