using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// SCAN EVERY 5m CANDLE: enter on exposure < 0.10, bank the winner at delta < 0.10, re-enter when
	// exposure is low again, repeat until the close.
	//
	// This is the natural synthesis of the pieces: every prior backtest here opened once at 9:30 and
	// held, which silently assumed both the entry time and a single trade per day.
	//
	// WHY FLOOR-ONLY ROLLING PRESERVES DEFINED RISK. Closing only when net delta has fallen below the
	// floor AND spot is above the short strike means every CLOSED leg is a winner. Only the leg still
	// open at the bell can lose, so a session's worst case remains one leg's max loss. That is a real
	// structural difference from the [0.10, 0.30] band, which also closes losers and therefore allows
	// two losing legs in a session -- it took the worst session from -10.08% to -14.71%. The worst%
	// column below is the check, not an afterthought.
	//
	// THE FLOOR MUST BE QUALIFIED ON spot > shortStrike. Net delta also collapses back under 0.10 when
	// price has fallen through BOTH strikes, i.e. at maximum loss. Without the qualifier the same
	// trigger fires on winners and on catastrophes alike.
	//
	// Costs are charged on every fill, entry and exit, at the measured mid-to-cross drag. A rule that
	// trades several times a session has to pay for the privilege.
	// ================================================================================================
	internal static class FiveMinRollLoop
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static double Gate = 0.10;          // 5m exposure entry condition
		public static double FloorDelta = 0.10;    // bank the winner when net delta drops below this
		public static double CostPct = 2.6;        // % of credit, per fill
		public static int MinBarsLeft = 6;         // ~30 min must remain to open a new leg
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };

		private sealed record Sess(string Sym, DateTime D, double Ret, int Legs, double PriorExp);

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

		private static double PutDeltaMag(double s, double k, double v, double t)
		{
			if (t <= 0 || v <= 0) return s < k ? 1.0 : 0.0;
			return NormCdf(-((Math.Log(s / k) + 0.5 * v * v * t) / (v * Math.Sqrt(t))));
		}

		private static double StrikeForPutDelta(double s, double v, double t, double delta)
		{
			double lo = s * 0.5, hi = s * 1.5;
			for (int i = 0; i < 80; i++)
			{
				double mid = 0.5 * (lo + hi);
				if (PutDeltaMag(s, mid, v, t) < delta) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
		}

		private static string Wk(DateTime d) => $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";

		// mode: 0 = single entry at open, hold      1 = single entry at first dip, hold
		//       2 = loop, re-enter only when exposure < Gate    3 = loop, re-enter at the next bar
		private static Sess? Simulate(string sym, DateTime d, List<OhlcBar> tb, List<double> exp, double iv,
			double priorExp, int mode)
		{
			int n = Math.Min(tb.Count, exp.Count);
			if (n < 20) return null;
			double ST = tb[n - 1].Close;
			if (ST <= 0) return null;
			double total = 0; int legs = 0;
			int k = 0;
			bool first = true;

			while (k < n - MinBarsLeft)
			{
				// ---- find the entry bar for this leg
				int j;
				if (mode == 0 && first) j = 0;
				else if (mode == 3 && !first) j = k;
				else
				{
					j = -1;
					for (int q = k; q < n - MinBarsLeft; q++) if (exp[q] < Gate) { j = q; break; }
					if (j < 0) break;
				}
				double S = (mode == 0 && first) ? tb[0].Open : tb[j].Close;
				if (S <= 0) break;
				double frac = (mode == 0 && first) ? 1.0 : (double)(n - 1 - j) / n;
				double T = Math.Max(1e-9, frac) / 252.0;
				double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) break;
				double entryCost = cr * CostPct / 100.0;
				legs++; first = false;

				// ---- walk forward looking for the floor-qualified exit
				bool banked = false;
				int e;
				for (e = j + 1; e < n; e++)
				{
					double Trem = (double)(n - 1 - e) / n / 252.0;
					if (Trem <= 1e-9) break;
					double Sp = tb[e].Close;
					if (Sp <= 0) continue;
					double nd = PutDeltaMag(Sp, kS, iv, Trem) - PutDeltaMag(Sp, kL, iv, Trem);
					// QUALIFIED: low delta only counts as a win when spot is above the short strike
					if (nd >= FloorDelta || Sp <= kS) continue;
					double val = Put(Sp, kS, iv, Trem) - Put(Sp, kL, iv, Trem);
					total += Risk * (cr - val - entryCost - val * CostPct / 100.0) / ml;
					banked = true;
					break;
				}
				if (!banked)
				{
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					total += Risk * (cr + po - entryCost) / ml;      // rides to expiry, no exit crossing
					break;
				}
				if (mode == 0 || mode == 1) break;                    // single-entry modes stop after one leg
				k = e + 1;
			}
			return legs == 0 ? null : new Sess(sym, d, total, legs, priorExp);
		}

		public static async Task Run()
		{
			var byMode = new Dictionary<int, List<Sess>>();
			for (int m = 0; m < 4; m++) byMode[m] = new List<Sess>();

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
					double iv = h * VolRiskPremium;
					for (int m = 0; m < 4; m++)
					{
						var s = Simulate(symbol, dTr, tb, pToday, iv, pPrev[^1], m);
						if (s != null) byMode[m].Add(s);
					}
				}
			}
			if (byMode[0].Count == 0) { Console.WriteLine("no data"); return; }

			var worst = byMode[0].GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.Ret)).First().Key;
			Console.WriteLine($"\n===== 5m LOOP: enter on exposure < {Gate:0.00}, bank at delta < {FloorDelta:0.00} IF IN PROFIT, re-enter =====");
			Console.WriteLine($"{byMode[0].Count} sessions, {byMode[0].Select(x => Wk(x.D)).Distinct().Count()} weeks; " +
				$"cost {CostPct:0.0}% of credit per fill; >= {MinBarsLeft} bars (~{5 * MinBarsLeft} min) required to open a leg");
			Console.WriteLine($"floor is QUALIFIED on spot > short strike, so every CLOSED leg is a winner and only the");
			Console.WriteLine($"leg open at the bell can lose -- watch the worst% column to confirm the -10% floor holds.");

			string[] names = { "1. single entry at OPEN, hold [SHIPPED]", "2. single entry at first dip, hold",
				"3. LOOP: re-enter on exposure < gate", "4. LOOP: re-enter at next bar (no gate)" };
			foreach (var (tag, filt) in new[] { ("FULL SAMPLE", (Func<Sess, bool>)(_ => true)), ("W23 REMOVED", x => Wk(x.D) != worst) })
			{
				Console.WriteLine($"\n-- {tag} --");
				Console.WriteLine($"{"arm",-42} {"sess",5} {"legs/s",7} {"mean/s%",9} {"win%",7} {"IR",8} {"maxDD%",8} {"worst%",8}");
				for (int m = 0; m < 4; m++)
				{
					var s = byMode[m].Where(filt).ToList();
					if (s.Count < 20) { Console.WriteLine($"{names[m],-42} {s.Count,5}  (too few)"); continue; }
					var r = s.Select(x => x.Ret).ToList();
					double mm = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - mm) * (z - mm)) / (r.Count - 1));
					double e = 1, pk = 1, dd = 0;
					foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
					Console.WriteLine($"{names[m],-42} {s.Count,5} {s.Average(x => x.Legs),7:0.00} {100 * mm,9:+0.0000;-0.0000} " +
						$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 1e-12 ? mm / sd : 0),8:0.000} {dd,8:0.00} {100 * r.Min(),8:0.00}");
				}
			}

			// Paired: the loop vs holding, on the SAME sessions.
			var hold = byMode[0].ToDictionary(x => (x.Sym, x.D), x => x.Ret);
			foreach (int m in new[] { 2, 3 })
			{
				var pairs = byMode[m].Where(x => hold.ContainsKey((x.Sym, x.D)) && Wk(x.D) != worst).ToList();
				if (pairs.Count < 20) continue;
				var d = pairs.Select(x => x.Ret - hold[(x.Sym, x.D)]).ToList();
				double md = d.Average();
				double sd = Math.Sqrt(d.Sum(z => (z - md) * (z - md)) / (d.Count - 1));
				Console.WriteLine($"\n{names[m]} minus hold, paired, W23 removed: n={pairs.Count} " +
					$"{100 * md:+0.0000;-0.0000}pp/session, t {md / (sd / Math.Sqrt(d.Count)):+0.00;-0.00}, " +
					$"better on {100.0 * d.Count(z => z > 0) / d.Count:0.0}% of sessions");
			}
			// ---- MATCHED-HAIRCUT CONTROL --------------------------------------------------------------
			// The loop earns more per session but deploys ~1.5 legs, so part of the gain is simply more
			// capital at work. Raising the stake on the SINGLE-entry arm is the signal-free way to buy the
			// same extra return, so the loop only deserves credit for what it achieves BEYOND that. The
			// multiplier is analytic -- match the session-return standard deviations, never bisect.
			{
				var h = byMode[0].Where(x => Wk(x.D) != worst).Select(x => x.Ret).ToList();
				var lp = byMode[2].Where(x => Wk(x.D) != worst).Select(x => x.Ret).ToList();
				double hm = h.Average(), lm = lp.Average();
				double hs = Math.Sqrt(h.Sum(z => (z - hm) * (z - hm)) / (h.Count - 1));
				double ls = Math.Sqrt(lp.Sum(z => (z - lm) * (z - lm)) / (lp.Count - 1));
				double mult = hs > 1e-12 ? ls / hs : 1.0;
				(double mean, double ir, double dd, double wst) Stat(List<double> r)
				{
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					double e = 1, pk = 1, dd = 0;
					foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
					return (100 * m, sd > 1e-12 ? m / sd : 0, dd, 100 * r.Min());
				}
				var a = Stat(h); var b = Stat(h.Select(x => x * mult).ToList()); var c = Stat(lp);
				Console.WriteLine($"\n-- matched-haircut control: hold scaled x{mult:0.000} to the loop's session volatility --");
				Console.WriteLine($"{"arm",-40} {"mean/s%",9} {"IR",8} {"maxDD%",8} {"worst%",8} {"ret/DD",8}");
				Console.WriteLine($"{"hold, unscaled",-40} {a.mean,9:+0.0000;-0.0000} {a.ir,8:0.000} {a.dd,8:0.00} {a.wst,8:0.00} {a.mean / Math.Max(0.01, a.dd),8:0.000}");
				Console.WriteLine($"{"hold, VOL-MATCHED to the loop",-40} {b.mean,9:+0.0000;-0.0000} {b.ir,8:0.000} {b.dd,8:0.00} {b.wst,8:0.00} {b.mean / Math.Max(0.01, b.dd),8:0.000}");
				Console.WriteLine($"{"LOOP",-40} {c.mean,9:+0.0000;-0.0000} {c.ir,8:0.000} {c.dd,8:0.00} {c.wst,8:0.00} {c.mean / Math.Max(0.01, c.dd),8:0.000}");
				Console.WriteLine($"   the loop beats the vol-matched haircut only where it shows a BETTER ret/DD and a");
				Console.WriteLine($"   shallower worst session -- equal mean at equal volatility is not an edge.");
			}

			Console.WriteLine($"\nleg distribution (mode 3): " +
				string.Join(", ", byMode[2].GroupBy(x => x.Legs).OrderBy(g => g.Key).Select(g => $"{g.Key} leg{(g.Key == 1 ? "" : "s")}: {g.Count()}")));
		}
	}
}
