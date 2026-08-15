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
		// Optional gamma gate, applied to whichever sessions the run is looking at. ST Bear has now been attacked
		// with strikes (no pair helps) and with the ratio under HOLD (no threshold helps with sample left). The one
		// untested combination is the ratio gate AND the roll rule together.
		public static double RatioMax = -1;                  // <= 0 disables
		public static Dictionary<DateTime, double> Ratio = new();

		// CALL/PUT convention (higher = more call gamma = favourable), distinct from `Ratio` above which
		// is put/call and gates with >=. Kept separate rather than reusing one dict, because silently
		// flipping the convention is exactly how a gate ends up inverted.
		public static Dictionary<DateTime, double> CallPut = new();
		public static double GexGate = -1;                   // <= 0 disables; skip when callPut < GexGate
		public static bool GexSizing = false;                // risk = Risk * min(callPut, GexCap)
		public static double GexCap = 2.0;
		// 5m exposure gate. Only ~60 days of 5m history exists, so this shrinks the window hard.
		public static Dictionary<DateTime, double> Exp5 = new();
		public static double FiveMinGate = -1;               // <= 0 disables; require exposure < gate

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
					if (RatioMax > 0 && (!Ratio.TryGetValue(dp, out double rr) || rr >= RatioMax)) continue;
					// gex gate + 5m gate, both keyed on the PRIOR session so they are known at the open
					bool haveCp = CallPut.TryGetValue(dp, out double cpv);
					if (GexGate > 0 && (!haveCp || cpv < GexGate)) continue;
					if (FiveMinGate > 0 && (!Exp5.TryGetValue(dp, out double e5v) || e5v >= FiveMinGate)) continue;
					// SIZING is per session and must scale every leg of the session, rolls included, or the
					// stake would silently reset to flat after the first re-entry.
					double riskI = Risk * (GexSizing && haveCp ? Math.Min(GexCap, cpv) : 1.0);
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
							total += riskI * (cr - val - entryCost - val * CostPct / 100.0) / ml;
							rolls++; rolled = true; break;
						}
						if (!rolled)
						{
							double ST = bars[n - 1].Close;
							double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
							total += riskI * (cr + po - entryCost) / ml;
							break;
						}
						k = j;
					}
					outp.Add(new Sess(d, total, rolls));
				}
				return outp;
			}

			Console.WriteLine($"\n{"rule",40} {"sess",6} {"rolls",7} {"mean/sess%",11} {"win%",7} {"IR",8} " +
				$"{"Sharpe",8} {"CAGR%",11} {"maxDD%",8} {"worst%",8}");
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
					$"{(sd > 0 ? m / sd * Math.Sqrt(s.Count / yrs) : 0),8:0.000} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),11:0.0} {dd,8:0.00} {100 * r.Min(),8:0.00}");
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

			// ---- ST BEAR: ROLL RULE x GAMMA GATE ---------------------------------------------------------
			Ratio = LoadRatioMap();
			if (Ratio.Count > 0)
			{
				bool k0 = SkipStBear, s0 = SplitStBear; double r0 = RatioMax;
				SkipStBear = false; SplitStBear = true;
				Console.WriteLine("");
				Console.WriteLine("--- ST BEAR: the two salvage levers TOGETHER (roll rule x ratio gate) ---");
				foreach (double th in new[] { -1.0, 1.00, 0.90, 0.84 })
				{
					RatioMax = th;
					string tag = th < 0 ? "no gate" : $"ratio<{th:0.00}";
					Show($"  ST Bear {tag}: hold", Sim(-1, -1));
					Show($"  ST Bear {tag}: floor QUALIFIED", Sim(0.10, -1, true));
					Show($"  ST Bear {tag}: band QUALIFIED", Sim(0.10, 0.30, true));
				}
				RatioMax = r0; SkipStBear = k0; SplitStBear = s0;
				Console.WriteLine("  (UW gamma starts 2022-03, so gated ST Bear rows are thin -- read the sess column)");
			}

			// ---- ARE THE GATE AND THE ROLL ADDITIVE OR REDUNDANT? ----------------------------------------
			// The gate was measured on daily bars with hold-to-expiry; the roll was measured on 1h bars with no
			// gate. They have never been run together, so it is unknown whether they remove the same bad outcomes.
			// Both plausibly work by avoiding the sessions that go wrong -- the gate ex ante, the roll during --
			// in which case stacking them buys much less than the two effects suggest separately.
			if (Ratio.Count > 0)
			{
				double r0 = RatioMax;
				Console.WriteLine("");
				Console.WriteLine("--- GATE x ROLL on the full book (shipped filters, 1h window) ---");
				// Floor-qualified swept finely: it is the variant that preserves the -10.08% defined-risk floor,
				// so the only question is where the gate should sit under it. Hold-to-expiry is shown at each
				// level as the do-nothing baseline for that same day-set.
				foreach (double th in new[] { -1.0, 1.00, 0.95, 0.90, 0.85, 0.80, 0.75, 0.70 })
				{
					RatioMax = th;
					string tag = th < 0 ? "no gate " : $"ratio<{th:0.00}";
					Show($"  {tag}: hold (baseline)", Sim(-1, -1));
					Show($"  {tag}: FLOOR QUALIFIED", Sim(0.10, -1, true));
				}
				RatioMax = r0;
				Console.WriteLine("  If the roll's gain SHRINKS as the gate tightens, they are redundant --");
				Console.WriteLine("  both are removing the same losing sessions, just at different moments.");
			}

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

			// ---- FULL STACK: band roll + gex gate + gex sizing (+ optional 5m gate) --------------------
			// Layers are added ONE AT A TIME against the same baseline so it stays visible which one moves
			// the result. The 5m arm is last because it costs most of the window: 5m history is ~60 days,
			// so it collapses a 2-year backtest to a handful of weeks, and that gate is itself retracted
			// (its entire effect was 2026-W23).
			CallPut = LoadCallPutRatio(symbol);
			if (CallPut.Count > 0)
			{
				Console.WriteLine($"{Environment.NewLine}--- FULL STACK on {symbol}: band [0.10,0.30] floor-qualified + gex ---");
				Console.WriteLine($"{"arm",44} {"sess",6} {"mean/sess%",11} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10} {"worst%",8}");
				void Stack(string lbl, List<Sess> s)
				{
					if (s.Count < 25) { Console.WriteLine($"{lbl,44} {s.Count,6}   (too few)"); return; }
					var r = s.Select(x => x.Ret).ToList();
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					double e = 1, pk = 1, dd = 0;
					foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
					double yrs = Math.Max(0.5, (s.Last().D - s.First().D).TotalDays / 365.25);
					Console.WriteLine($"{lbl,44} {s.Count,6} {100 * m,11:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
						$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {100 * r.Min(),8:0.00}");
				}
				GexGate = -1; GexSizing = false; FiveMinGate = -1;
				Stack("1. hold to expiry, flat [SHIPPED]", Sim(-1, -1));
				Stack("2. + band roll", Sim(0.10, 0.30, true));
				GexGate = 1.0;
				Stack("3. + gex gate (callPut >= 1)", Sim(0.10, 0.30, true));
				GexSizing = true;
				Stack("4. + gex sizing (x min(cp,2))", Sim(0.10, 0.30, true));
				GexGate = -1;
				Stack("   gex SIZING only, no gex gate", Sim(0.10, 0.30, true));
				GexGate = 1.0;

				try
				{
					var i5 = await IntradayClient.GetAsync(symbol, "5m", "60d");
					if (i5.Count >= 100)
					{
						var e5eng = BankrollSimulator.Run(i5, 10_000.0);
						Exp5 = new Dictionary<DateTime, double>();
						for (int k = 0; k < e5eng.Positions.Count && k < e5eng.ReturnDates.Count; k++)
							Exp5[e5eng.ReturnDates[k].Date] = e5eng.Positions[k];
						FiveMinGate = 0.10;
						Console.WriteLine($"   (5m coverage: {Exp5.Count} sessions, {Exp5.Keys.Min():yyyy-MM-dd} -> {Exp5.Keys.Max():yyyy-MM-dd})");
						Stack("5. + 5m exposure < 0.10  [RETRACTED gate]", Sim(0.10, 0.30, true));
						GexGate = -1; GexSizing = false;
						Stack("   5m gate alone, no gex", Sim(0.10, 0.30, true));
					}
				}
				catch (Exception ex) { Console.WriteLine($"   5m unavailable: {ex.Message}"); }
				GexGate = -1; GexSizing = false; FiveMinGate = -1; Exp5 = new(); CallPut = new();
			}
		}

		// call/put (higher = favourable). Deliberately separate from the put/call loader above, because
		// silently reusing one dictionary across two conventions is how a gate ends up inverted.
		private static Dictionary<DateTime, double> LoadCallPutRatio(string symbol)
		{
			var m = new Dictionary<DateTime, double>();
			string dat = symbol.ToUpperInvariant() switch
				{ "SPY" => "spx", "QQQ" => "qqq", "IWM" => "iwm", "GLD" => "gld", _ => "" };
			if (dat == "") return m;
			string path = System.IO.Path.Combine(System.IO.Path.GetFullPath(Universe.DataDir), $"gex_uw_{dat}.csv");
			if (!System.IO.File.Exists(path)) return m;
			var lines = System.IO.File.ReadAllLines(path);
			var h = lines[0].Split(',');
			int di = Array.IndexOf(h, "date"), ci = Array.IndexOf(h, "call_gex"), pi = Array.IndexOf(h, "put_gex");
			if (di < 0 || ci < 0 || pi < 0) return m;
			for (int i = 1; i < lines.Length; i++)
			{
				var f = lines[i].Split(',');
				if (f.Length <= Math.Max(ci, pi)) continue;
				if (DateTime.TryParse(f[di], System.Globalization.CultureInfo.InvariantCulture,
						System.Globalization.DateTimeStyles.None, out var d)
					&& double.TryParse(f[ci], System.Globalization.NumberStyles.Any,
						System.Globalization.CultureInfo.InvariantCulture, out var cg)
					&& double.TryParse(f[pi], System.Globalization.NumberStyles.Any,
						System.Globalization.CultureInfo.InvariantCulture, out var pg)
					&& Math.Abs(pg) > 0)
					m[d.Date] = cg / Math.Abs(pg);
			}
			return m;
		}

		private static Dictionary<DateTime, double> LoadRatioMap()
		{
			var m = new Dictionary<DateTime, double>();
			string p = System.IO.Path.Combine(System.IO.Path.GetFullPath(Universe.DataDir), "gex_uw_spx.csv");
			if (!System.IO.File.Exists(p)) return m;
			var lines = System.IO.File.ReadAllLines(p);
			var h = lines[0].Split(',');
			int di = Array.IndexOf(h, "date"), ci = Array.IndexOf(h, "call_gex"), pi = Array.IndexOf(h, "put_gex");
            for (int i = 1; i < lines.Length; i++)
			{
				var f = lines[i].Split(',');
				if (f.Length <= Math.Max(ci, pi)) continue;
				if (DateTime.TryParse(f[di], System.Globalization.CultureInfo.InvariantCulture,
						System.Globalization.DateTimeStyles.None, out var d)
					&& double.TryParse(f[ci], System.Globalization.NumberStyles.Any,
						System.Globalization.CultureInfo.InvariantCulture, out var cg)
					&& double.TryParse(f[pi], System.Globalization.NumberStyles.Any,
						System.Globalization.CultureInfo.InvariantCulture, out var pg)
					&& cg > 0)
					m[d.Date] = Math.Abs(pg) / cg;
			}
			return m;
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
