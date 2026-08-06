using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// How much is the engine's exposure actually worth to the 0-DTE put credit spread?
	//
	// The signal is doing TWO separable jobs and the shipped configuration conflates them:
	//   SELECTION  the [0.10, 0.50) band decides WHICH DAYS to trade at all
	//   STRIKE     the target value sets the short leg at (target + wing), i.e. the net delta carried
	// Replacing the target with a CONSTANT delta kills the second job while leaving the first intact.
	// Running that against a version with no band at all completes the 2x2 and prices each job separately:
	//
	//                        strike from engine        strike fixed
	//     band selection     shipped                   selection only
	//     no selection       strike only               neither (pure short premium)
	//
	// READING THE TABLE. The arms carry DIFFERENT amounts of delta -- a fixed 0.50 is far more directional than
	// the ~0.28 mean target inside the band -- so raw return is not comparable and higher return may be nothing
	// but more exposure. Two scale-free columns are therefore carried:
	//   IR/tr    mean/sd per trade -- edge quality, invariant to a constant position multiplier
	//   ret/dlt  mean/trade per unit of implied delta -- return per unit of directional exposure taken
	// Annualised Sharpe is also shown, but it REWARDS TRADE COUNT, so it favours the no-band arms by construction
	// and should not be used to judge selection on its own.
	//
	// SPY, real expiry dates only, 5% risk per trade, wing fixed at 0.15.
	public static class FixedDeltaAblation
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double MaxShortDelta = 0.95;
		public static int    YearsBack = 21;
		public static double Risk = 0.05;
		public static double TargetLo = 0.10, TargetHi = 0.50;
		public static double[] FixedDeltas = { 0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.65 };

		private sealed record Tr(DateTime D, double R, double DeltaPerRisk, double Target, double NetD, double WidthPctS, double CrPctMl, double Under, ShortTermState St, double Gex, bool HasGex);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
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

			// A delta RULE maps the engine target to the net delta the spread should carry. Constants and the
			// engine-passthrough are both special cases, which is what makes them comparable on one axis.
			List<Tr> BuildRule(Func<double, double> deltaRule, bool bandOnly, bool needTarget)
			{
				double T = 1.0 / 252.0;
				var tr = new List<Tr>();
				for (int i = 1; i + 1 < bars.Count; i++)
				{
					var dSig = bars[i].Date;
					if (!hv.TryGetValue(dSig, out double sig)) continue;
					if (!posByDate.TryGetValue(dSig.Date, out double target)) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(bars[i + 1].Date)) continue;
					if (bandOnly && (target < TargetLo || target >= TargetHi)) continue;
					// with no band the engine can still say "hold nothing"; a 0 target is not a trade under the
					// engine-strike arm. The fixed-delta arms deliberately ignore the target entirely.
					if (needTarget && target <= 1e-6) continue;

					double netDWanted = deltaRule(target);
					if (netDWanted <= 1e-9) continue;
					double S = bars[i + 1].Open, ST = bars[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;

					double iv = sig * VolRiskPremium;
					double shortMag = Math.Min(MaxShortDelta, netDWanted + WingDelta);
					double netD = shortMag - WingDelta;
					if (netD <= 1e-9) continue;

					double kShort = StrikeForPutDelta(S, iv, T, shortMag);
					double kLong = StrikeForPutDelta(S, iv, T, WingDelta);
					double width = kShort - kLong, cr = Put(S, kShort, iv, T) - Put(S, kLong, iv, T);
					double risk = width - cr;
					if (cr <= 1e-9 || risk <= 1e-9) continue;
					double payoff = -Math.Max(0, kShort - ST) + Math.Max(0, kLong - ST);
					bool hasGex = gex.TryGetValue(dSig.Date, out var g);
					tr.Add(new Tr(bars[i + 1].Date, (cr + payoff) / risk, netD * S / risk, target, netD,
						100.0 * width / S, 100.0 * cr / risk, (ST - S) / S,
						stByDate.TryGetValue(dSig.Date, out var stv) ? stv : ShortTermState.BearNeutral,
						hasGex ? g!.Gex : double.NaN, hasGex));
				}
				return tr;
			}
			List<Tr> Build(double fixedDelta, bool bandOnly) =>
				fixedDelta >= 0 ? BuildRule(_ => fixedDelta, bandOnly, false)
				                : BuildRule(t => t, bandOnly, true);

			Console.WriteLine($"\n===== {symbol}: IS THE EXPOSURE SIGNAL WORTH ANYTHING? (0 DTE, {100 * Risk:0.#}% risk, real expiries) =====");
			Console.WriteLine($"wing fixed at {WingDelta:0.00}; 'engine' sets net delta = target, 'fixed X' ignores the target and always carries X");
			var shipped = Build(-1, true);
			Console.WriteLine($"mean engine target inside the band: {shipped.Average(t => t.Target):0.000} " +
				$"(so a fixed 0.30 carries slightly MORE delta than the shipped arm, and 0.50 far more)");

			Console.WriteLine($"\n{"arm",30} {"trades",7} {"impDelta%",10} {"mean/tr%",10} {"win%",7} {"IR/tr",8} " +
				$"{"Sharpe",8} {"ret/dlt",9} {"maxDD%",8} {"CAGR%",9}");

			void Row(string label, List<Tr> t)
			{
				if (t.Count < 20) { Console.WriteLine($"{label,30} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				var (final, dd) = Curve(r);
				double cagr = final > -100 ? (Math.Pow(1 + final / 100.0, 1 / yrs) - 1) * 100 : double.NaN;
				double impD = 100 * Risk * t.Average(x => x.DeltaPerRisk);
				Console.WriteLine($"{label,30} {t.Count,7} {impD,10:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(x => x > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} " +
					$"{(sd > 0 ? m / sd * Math.Sqrt(t.Count / yrs) : 0),8:0.000} " +
					$"{(impD > 0 ? 100 * m / impD : 0),9:0.0000} {dd,8:0.00} {cagr,9:0.0}");
			}

			Console.WriteLine("\n--- BAND SELECTION ON (trade only when engine target is in the band) ---");
			Row("engine target  [SHIPPED]", shipped);
			foreach (double d in FixedDeltas) Row($"fixed {d:0.00} delta", Build(d, true));

			Console.WriteLine("\n--- BAND SELECTION OFF (trade every session with a real expiry) ---");
			Row("engine target", Build(-1, false));
			foreach (double d in FixedDeltas) Row($"fixed {d:0.00} delta", Build(d, false));

			// Does the signal carry anything WITHIN the band? Hold the strike fixed and split by target quartile:
			// if the target has information the fixed-delta trades should still perform differently across buckets.
			Console.WriteLine($"\n--- with the strike held FIXED at 0.30, does the target still separate outcomes? ---");
			var f30 = Build(0.30, false).Where(t => t.Target > 1e-6).ToList();
			var qs = f30.Select(t => t.Target).OrderBy(x => x).ToList();
			double q1 = qs[(int)(qs.Count * 0.25)], q2 = qs[(int)(qs.Count * 0.50)], q3 = qs[(int)(qs.Count * 0.75)];
			Console.WriteLine($"{"target bucket",30} {"trades",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8}");
			(string L, Func<double, bool> P)[] buckets =
			{
				($"target < {q1:0.00}", x => x < q1),
				($"{q1:0.00} - {q2:0.00}", x => x >= q1 && x < q2),
				($"{q2:0.00} - {q3:0.00}", x => x >= q2 && x < q3),
				($"target >= {q3:0.00}", x => x >= q3),
			};
			foreach (var (L, P) in buckets)
			{
				var sub = f30.Where(t => P(t.Target)).ToList();
				if (sub.Count < 20) { Console.WriteLine($"{L,30} {sub.Count,7}  (too few)"); continue; }
				var r = sub.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
				Console.WriteLine($"{L,30} {sub.Count,7} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(x => x > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000}");
			}
			// The quartile table says the TOP target bucket is one of the best, yet the shipped band discards
			// everything at or above 0.50. If the target is a day-quality signal rather than a strike input, the
			// upper bound may be throwing away good sessions. Sweep the bounds with the strike pinned at 0.25 so
			// only the SELECTION changes.
			Console.WriteLine($"\n--- band bounds, strike pinned at 0.25 (selection is the only thing varying) ---");
			Console.WriteLine($"{"band",30} {"trades",7} {"impDelta%",10} {"mean/tr%",10} {"win%",7} {"IR/tr",8} " +
				$"{"Sharpe",8} {"ret/dlt",9} {"maxDD%",8} {"CAGR%",9}");
			(string L, double Lo, double Hi)[] bands =
			{
				("none (every session)", -1, 99),
				("target >= 0.05", 0.05, 99),
				("target >= 0.10", 0.10, 99),
				("target >= 0.20", 0.20, 99),
				("target >= 0.30", 0.30, 99),
				("[0.10, 0.50)  SHIPPED", 0.10, 0.50),
				("[0.10, 0.80)", 0.10, 0.80),
				("[0.10, 1.00)", 0.10, 1.00),
			};
			double savedLo = TargetLo, savedHi = TargetHi;
			foreach (var (L, lo, hi) in bands)
			{
				TargetLo = lo; TargetHi = hi;
				Row(L, Build(0.25, lo >= 0));
			}
			TargetLo = savedLo; TargetHi = savedHi;

			// -------------------------------------------------------------------------------------------------
			// SHIFTED-FLOOR RULE: netDelta = max(target - shift, floor).
			//
			// This is a hybrid of the two findings. Below target = shift + floor it is a CONSTANT (the floor), which
			// is what the ablation said the strike should be; above that it scales with the target, which is where
			// the quartile table said the signal still has something to say. So it behaves like the winning fixed
			// rule for the bulk of days and only re-introduces the engine's opinion in the strong-signal tail.
			//
			// Reported with the fraction of trades sitting ON the floor, because if that is ~100% the rule is just
			// the constant wearing a disguise and any difference is noise.
			// -------------------------------------------------------------------------------------------------
			Console.WriteLine($"\n--- shifted-floor rule vs the constant, band = target >= 0.10 (no upper cap) ---");
			Console.WriteLine($"{"rule",30} {"trades",7} {"onFloor%",9} {"impDelta%",10} {"mean/tr%",10} {"win%",7} " +
				$"{"IR/tr",8} {"Sharpe",8} {"ret/dlt",9} {"maxDD%",8} {"CAGR%",9}");
			double sLo = TargetLo, sHi = TargetHi;
			TargetLo = 0.10; TargetHi = 99;

			void RuleRow(string label, Func<double, double> rule, double floor)
			{
				var t = BuildRule(rule, true, false);
				if (t.Count < 20) { Console.WriteLine($"{label,30} {t.Count,7}  (too few)"); return; }
				double onFloor = 100.0 * t.Count(x => Math.Abs(x.NetD - floor) < 1e-6) / t.Count;
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				var (final, dd) = Curve(r);
				double cagr = final > -100 ? (Math.Pow(1 + final / 100.0, 1 / yrs) - 1) * 100 : double.NaN;
				double impD = 100 * Risk * t.Average(x => x.DeltaPerRisk);
				Console.WriteLine($"{label,30} {t.Count,7} {onFloor,9:0.0} {impD,10:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(x => x > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} " +
					$"{(sd > 0 ? m / sd * Math.Sqrt(t.Count / yrs) : 0),8:0.000} " +
					$"{(impD > 0 ? 100 * m / impD : 0),9:0.0000} {dd,8:0.00} {cagr,9:0.0}");
			}

			RuleRow("fixed 0.25  [the incumbent]", _ => 0.25, 0.25);
			RuleRow("max(target-0.30, 0.25)", t => Math.Max(t - 0.30, 0.25), 0.25);
			// neighbourhood, so the headline pair is not read as a fitted point
			RuleRow("max(target-0.20, 0.25)", t => Math.Max(t - 0.20, 0.25), 0.25);
			RuleRow("max(target-0.40, 0.25)", t => Math.Max(t - 0.40, 0.25), 0.25);
			RuleRow("max(target-0.60, 0.25)", t => Math.Max(t - 0.60, 0.25), 0.25);
			RuleRow("max(target-0.30, 0.20)", t => Math.Max(t - 0.30, 0.20), 0.20);
			RuleRow("max(target-0.30, 0.30)", t => Math.Max(t - 0.30, 0.30), 0.30);
			RuleRow("engine target (passthrough)", t => t, -1);
			TargetLo = sLo; TargetHi = sHi;
			// PAIRED TEST. Every rule above trades the SAME 1837 sessions and differs only in the delta carried, so
			// an unpaired standard error (~1/sqrt(N) = 0.023 on IR) badly overstates the uncertainty. Differencing
			// per trade removes all the shared market noise. Reported twice: over every trade, and over only the
			// subset where the rule actually departs from the floor -- that subset is where the entire effect lives,
			// and diluting it across untouched trades hides the magnitude.
			Console.WriteLine($"\n--- paired vs fixed 0.25, same sessions, difference per trade ---");
			Console.WriteLine($"{"rule",30} {"n diff",8} {"mean d (all)%",14} {"t (all)",9} {"mean d (diff only)%",21} {"t (diff)",9}");
			TargetLo = 0.10; TargetHi = 99;
			var baseArm = BuildRule(_ => 0.25, true, false);
			var baseByDate = baseArm.ToDictionary(x => x.D, x => Risk * x.R);
			void Paired(string label, Func<double, double> rule)
			{
				var t = BuildRule(rule, true, false);
				var all = new List<double>(); var only = new List<double>();
				foreach (var x in t)
				{
					if (!baseByDate.TryGetValue(x.D, out double b)) continue;
					double d = Risk * x.R - b;
					all.Add(d);
					if (Math.Abs(x.NetD - 0.25) > 1e-6) only.Add(d);
				}
				string Fmt(List<double> v)
				{
					if (v.Count < 2) return "n/a";
					double m = v.Average();
					double sd = Math.Sqrt(v.Sum(z => (z - m) * (z - m)) / (v.Count - 1));
					return $"{100 * m:+0.0000;-0.0000}|{(sd > 0 ? m / (sd / Math.Sqrt(v.Count)) : 0):+0.00;-0.00}";
				}
				var a = Fmt(all).Split('|'); var o = Fmt(only).Split('|');
				Console.WriteLine($"{label,30} {only.Count,8} {a[0],14} {(a.Length > 1 ? a[1] : ""),9} " +
					$"{(o.Length > 0 ? o[0] : "n/a"),21} {(o.Length > 1 ? o[1] : ""),9}");
			}
			Paired("max(target-0.30, 0.25)", t => Math.Max(t - 0.30, 0.25));
			Paired("max(target-0.20, 0.25)", t => Math.Max(t - 0.20, 0.25));
			Paired("max(target-0.40, 0.25)", t => Math.Max(t - 0.40, 0.25));
			Paired("engine target (passthrough)", t => t);
			TargetLo = sLo; TargetHi = sHi;
			Console.WriteLine("t is a paired t-stat on the per-trade difference; negative means the rule LOSES to flat 0.25.");

			Console.WriteLine("onFloor% is the share of trades where the rule collapsed to the constant. The higher it is,");
			Console.WriteLine("the less the scaling tail can possibly be contributing either way.");

			// -------------------------------------------------------------------------------------------------
			// WING SWEEP. Net delta is pinned, so every row carries the SAME directional exposure and the wing
			// only decides WHERE the spread sits and how wide it is. Raising the wing with net delta fixed drags
			// both legs toward the money: at wing 0.25 with net 0.25 the short leg is a 0.50-delta ATM put.
			// So this trades a narrower, richer spread against a much higher chance of the short being breached.
			// Swept at the shipped net delta and at the lower one IWM prefers, since the two may not agree.
			// -------------------------------------------------------------------------------------------------
			double savedWing = WingDelta;
			double bLo = TargetLo, bHi = TargetHi;
			TargetLo = 0.10; TargetHi = 99;
			foreach (double net in new[] { 0.25, 0.15 })
			{
				Console.WriteLine($"\n--- WING sweep at FIXED net delta {net:0.00}, exposure >= 0.10 ---");
				Console.WriteLine($"{"wing",8} {"short",8} {"trades",7} {"width%S",9} {"cr%maxLoss",11} {"impDelta%",10} " +
					$"{"mean/tr%",10} {"win%",7} {"IR/tr",8} {"maxDD%",8} {"CAGR%",9}");
				foreach (double w in new[] { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30 })
				{
					WingDelta = w;
					var t = BuildRule(_ => net, true, false);
					if (t.Count < 20) { Console.WriteLine($"{w,8:0.00} {net + w,8:0.00} {t.Count,7}  (too few)"); continue; }
					var r = t.Select(x => Risk * x.R).ToList();
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
					var (final, dd) = Curve(r);
					double cagr = final > -100 ? (Math.Pow(1 + final / 100.0, 1 / yrs) - 1) * 100 : double.NaN;
					Console.WriteLine($"{w,8:0.00} {net + w,8:0.00} {t.Count,7} {t.Average(x => x.WidthPctS),9:0.00} " +
						$"{t.Average(x => x.CrPctMl),11:0.0} {100 * Risk * t.Average(x => x.DeltaPerRisk),10:0.0} " +
						$"{100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
						$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {cagr,9:0.0}");
				}
			}
			WingDelta = savedWing; TargetLo = bLo; TargetHi = bHi;
			Console.WriteLine("Delta is identical down each block, so this ranks WHERE the spread sits, not how much it carries.");

			// -------------------------------------------------------------------------------------------------
			// WIN RATE BY EXPOSURE BUCKET. Structure is the SHIPPED one and identical in every row (0.15 wing,
			// 0.40 short, net 0.25), so nothing about the trade changes across buckets -- only which sessions
			// land in them. Any spread in win rate is therefore the exposure signal, not a different position.
			//
			// No band here: every session with a real expiry is included so the sub-0.10 region the shipped rule
			// DISCARDS is visible alongside the region it keeps. Underlying columns are carried because a short
			// put wins when the market does not fall far, so the win rate should track the session's drift.
			// -------------------------------------------------------------------------------------------------
			double wSaved = WingDelta;
			WingDelta = 0.15;
			var all = BuildRule(_ => 0.25, false, false);
			Console.WriteLine($"\n--- WIN RATE BY EXPOSURE BUCKET (shipped structure, every real-expiry session) ---");
			Console.WriteLine($"{"exposure",16} {"trades",7} {"% of days",10} {"win%",8} {"mean/tr%",10} {"IR/tr",8} " +
				$"{"undMean%",10} {"und up%",9} {"worst%",8}");
			(string L, double A, double B)[] bins =
			{
				("0.00 (flat)", -1e-9, 1e-6), ("0.00 - 0.05", 1e-6, 0.05), ("0.05 - 0.10", 0.05, 0.10),
				("0.10 - 0.15", 0.10, 0.15), ("0.15 - 0.20", 0.15, 0.20), ("0.20 - 0.30", 0.20, 0.30),
				("0.30 - 0.40", 0.30, 0.40), ("0.40 - 0.50", 0.40, 0.50), ("0.50 - 0.75", 0.50, 0.75),
				("0.75 - 1.00", 0.75, 1.00), ("1.00 - 1.50", 1.00, 1.50), (">= 1.50", 1.50, 99),
			};
			foreach (var (L, a, b) in bins)
			{
				var sub = all.Where(x => x.Target >= a && x.Target < b).ToList();
				if (sub.Count < 15) { Console.WriteLine($"{L,16} {sub.Count,7}  (too few)"); continue; }
				var r = sub.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / Math.Max(1, r.Count - 1));
				Console.WriteLine($"{L,16} {sub.Count,7} {100.0 * sub.Count / all.Count,10:0.0} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,8:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {100 * sub.Average(x => x.Under),10:+0.000;-0.000} " +
					$"{100.0 * sub.Count(x => x.Under > 0) / sub.Count,9:0.0} {100 * r.Min(),8:0.00}");
			}
			var kept = all.Where(x => x.Target >= 0.10).ToList();
			var cut  = all.Where(x => x.Target <  0.10).ToList();
			Console.WriteLine($"{"KEPT (>= 0.10)",16} {kept.Count,7} {100.0 * kept.Count / all.Count,10:0.0} " +
				$"{100.0 * kept.Count(x => x.R > 0) / kept.Count,8:0.0}");
			Console.WriteLine($"{"CUT (< 0.10)",16} {cut.Count,7} {100.0 * cut.Count / all.Count,10:0.0} " +
				$"{100.0 * cut.Count(x => x.R > 0) / cut.Count,8:0.0}");
			WingDelta = wSaved;

			// -------------------------------------------------------------------------------------------------
			// TIERED SIZING: risk 5% when exposure <= threshold, 10% above it. A THIRD job for the signal --
			// not which days to trade, not where to strike, but how much to stake.
			//
			// THE CONTROL THAT MATTERS: tiering upward takes more total risk than flat 5%, so beating flat 5% is
			// meaningless -- any rule that stakes more will. The honest benchmark is a FLAT rule at the same
			// AVERAGE risk (rbar), which holds total capital-at-risk constant and asks only whether putting it in
			// the right places helps. An inverted tier (10% low / 5% high) is carried as a sign control: if the
			// signal is real the inversion must LOSE by roughly as much as the tier wins.
			//
			// The paired difference against matched-flat is exactly cov(risk_i, R_i), so its t-stat is a direct
			// test of whether the stake lands on the better trades.
			// -------------------------------------------------------------------------------------------------
			double tw = WingDelta; WingDelta = 0.15;
			double tLo = TargetLo, tHi = TargetHi; TargetLo = 0.10; TargetHi = 99;
			var tier = BuildRule(_ => 0.25, true, false);
			Console.WriteLine($"\n--- TIERED SIZING by exposure (shipped structure, exposure >= 0.10) ---");
			Console.WriteLine($"{"rule",34} {"trades",7} {"%hi tier",9} {"avgRisk%",9} {"mean/tr%",10} " +
				$"{"Sharpe",8} {"maxDD%",8} {"CAGR%",9} {"vs matched t",13}");

			void Sized(string label, Func<double, double> riskOf, Func<double, double>? baseline = null)
			{
				var r = tier.Select(x => riskOf(x.Target) * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double yrs = Math.Max(1.0, (tier.Last().D - tier.First().D).TotalDays / 365.25);
				var (final, dd) = Curve(r);
				double cagr = final > -100 ? (Math.Pow(1 + final / 100.0, 1 / yrs) - 1) * 100 : double.NaN;
				string tstat = "";
				if (baseline != null)
				{
					var d = tier.Select(x => (riskOf(x.Target) - baseline(x.Target)) * x.R).ToList();
					double dm = d.Average();
					double dsd = Math.Sqrt(d.Sum(z => (z - dm) * (z - dm)) / (d.Count - 1));
					tstat = dsd > 0 ? $"{dm / (dsd / Math.Sqrt(d.Count)):+0.00;-0.00}" : "";
				}
				Console.WriteLine($"{label,34} {tier.Count,7} " +
					$"{100.0 * tier.Count(x => riskOf(x.Target) > 0.05) / tier.Count,9:0.0} " +
					$"{100 * tier.Average(x => riskOf(x.Target)),9:0.00} {100 * m,10:+0.0000;-0.0000} " +
					$"{(sd > 0 ? m / sd * Math.Sqrt(tier.Count / yrs) : 0),8:0.000} {dd,8:0.00} {cagr,9:0.0} {tstat,13}");
			}

			foreach (double thr in new[] { 0.50, 0.40, 0.60, 0.75 })
			{
				Func<double, double> tiered = t => t > thr ? 0.10 : 0.05;
				double rbar = tier.Average(x => tiered(x.Target));
				Console.WriteLine($"  threshold {thr:0.00}  (matched-flat control = {100 * rbar:0.00}% on every trade)");
				Sized($"    flat {100 * rbar:0.00}% [MATCHED CONTROL]", _ => rbar);
				Sized($"    TIERED 5/10 at {thr:0.00}", tiered, _ => rbar);
				// A valid sign control must hold AVERAGE RISK constant, so it cannot simply swap the comparison.
				// Instead give the 10% stake to the SAME NUMBER of trades, chosen from the LOWEST exposures --
				// same stake multiset, opposite allocation. If the tier's gain is real this must lose by a
				// comparable margin; if both win, the gain was concentration rather than the signal.
				int nHi = tier.Count(x => x.Target > thr);
				var asc = tier.Select(x => x.Target).OrderBy(v => v).ToList();
				double loThr = nHi > 0 && nHi <= asc.Count ? asc[nHi - 1] : -1;
				Sized($"    INVERTED (lowest {nHi} get 10%)", t => t <= loThr ? 0.10 : 0.05, _ => rbar);
			}
			Console.WriteLine("  reference points:");
			Sized("    flat 5%", _ => 0.05);
			Sized("    flat 10%", _ => 0.10);
			WingDelta = tw; TargetLo = tLo; TargetHi = tHi;
			Console.WriteLine("The t-column is paired against that block's matched-flat control -- it is cov(stake, outcome),");
			Console.WriteLine("so it isolates whether the extra size lands on better trades rather than just being more size.");

			// -------------------------------------------------------------------------------------------------
			// SKIP ST BEAR. A second SELECTION filter, orthogonal to the exposure floor: the ST state machine is
			// a different read on the same bar, so it may cut sessions the floor keeps.
			//
			// Two things to hold in mind. Dropping trades ALWAYS costs CAGR through lost compounding, so a filter
			// that raises per-trade quality can still lower terminal wealth -- IR/tr and drawdown are what say
			// whether the filter is any good, CAGR says what it costs. And the four ST states are shown separately
			// rather than just Bear/not-Bear, because "skip Bear" is only the right rule if Bear is actually the
			// bad state; if BearNeutral is worse the filter is aimed at the wrong thing.
			// -------------------------------------------------------------------------------------------------
			double sw = WingDelta; WingDelta = 0.15;
			double sLo2 = TargetLo, sHi2 = TargetHi; TargetLo = 0.10; TargetHi = 99;
			var stAll = BuildRule(_ => 0.25, true, false);
			Console.WriteLine($"\n--- BY ST STATE (shipped structure, exposure >= 0.10) ---");
			Console.WriteLine($"{"ST state",22} {"trades",7} {"% of days",10} {"win%",8} {"mean/tr%",10} {"IR/tr",8} " +
				$"{"undMean%",10} {"worst%",8}");
			foreach (var st in new[] { ShortTermState.Bull, ShortTermState.BullNeutral,
			                           ShortTermState.BearNeutral, ShortTermState.Bear })
			{
				var sub = stAll.Where(x => x.St == st).ToList();
				if (sub.Count < 15) { Console.WriteLine($"{st,22} {sub.Count,7}  (too few)"); continue; }
				var r = sub.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / Math.Max(1, r.Count - 1));
				Console.WriteLine($"{st,22} {sub.Count,7} {100.0 * sub.Count / stAll.Count,10:0.0} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,8:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {100 * sub.Average(x => x.Under),10:+0.000;-0.000} {100 * r.Min(),8:0.00}");
			}

			Console.WriteLine($"\n--- FILTERED VARIANTS ---");
			Console.WriteLine($"{"filter",30} {"trades",7} {"kept%",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8} " +
				$"{"Sharpe",8} {"maxDD%",8} {"CAGR%",9}");
			void StRow(string label, Func<Tr, bool> keep)
			{
				var t = stAll.Where(keep).ToList();
				if (t.Count < 20) { Console.WriteLine($"{label,30} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				var (final, dd) = Curve(r);
				double cagr = final > -100 ? (Math.Pow(1 + final / 100.0, 1 / yrs) - 1) * 100 : double.NaN;
				Console.WriteLine($"{label,30} {t.Count,7} {100.0 * t.Count / stAll.Count,7:0.0} " +
					$"{100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {(sd > 0 ? m / sd * Math.Sqrt(t.Count / yrs) : 0),8:0.000} " +
					$"{dd,8:0.00} {cagr,9:0.0}");
			}
			StRow("no ST filter [SHIPPED]", _ => true);
			StRow("skip ST Bear", x => x.St != ShortTermState.Bear);
			StRow("skip ST Bear + BearNeutral", x => x.St != ShortTermState.Bear && x.St != ShortTermState.BearNeutral);
			StRow("skip ST BearNeutral only", x => x.St != ShortTermState.BearNeutral);
			StRow("ONLY ST Bear (inverse)", x => x.St == ShortTermState.Bear);
			WingDelta = sw; TargetLo = sLo2; TargetHi = sHi2;
			Console.WriteLine("Dropping trades costs CAGR by construction, so judge the filter on IR/tr and drawdown.");

			// -------------------------------------------------------------------------------------------------
			// RE-VALIDATION AFTER THE ST-BEAR FILTER.
			//
			// The exposure floor and the ST-Bear filter are NOT independent -- ST Bear days skew low-exposure, so
			// the two rules overlap and each was tuned in the other's absence. Once Bear is removed the floor may
			// be cutting sessions that are no longer bad, and the delta optimum may have moved because the
			// surviving population is different. Both are therefore re-swept CONDITIONAL on the filter rather
			// than assumed to carry over.
			// -------------------------------------------------------------------------------------------------
			double rw = WingDelta; WingDelta = 0.15;
			double rLo = TargetLo, rHi = TargetHi; TargetLo = 0.10; TargetHi = 99;
			var uni = BuildRule(_ => 0.25, false, false);   // every real-expiry session, shipped structure

			void Reval(string label, List<Tr> t, int denom)
			{
				if (t.Count < 20) { Console.WriteLine($"{label,32} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				var (final, dd) = Curve(r);
				double cagr = final > -100 ? (Math.Pow(1 + final / 100.0, 1 / yrs) - 1) * 100 : double.NaN;
				Console.WriteLine($"{label,32} {t.Count,7} {100.0 * t.Count / denom,7:0.0} " +
					$"{100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {(sd > 0 ? m / sd * Math.Sqrt(t.Count / yrs) : 0),8:0.000} " +
					$"{dd,8:0.00} {cagr,9:0.0}");
			}

			Console.WriteLine("");
			Console.WriteLine("--- FLOOR re-swept WITH ST Bear skipped (delta 0.25) ---");
			Console.WriteLine($"{"floor",32} {"trades",7} {"kept%",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8} " +
				$"{"Sharpe",8} {"maxDD%",8} {"CAGR%",9}");
			var noBear = uni.Where(x => x.St != ShortTermState.Bear).ToList();
			foreach (double f in new[] { -1.0, 0.0, 0.05, 0.10, 0.15, 0.20, 0.30 })
				Reval(f < 0 ? "  no floor (all non-Bear)" : $"  exposure >= {f:0.00}",
					noBear.Where(x => f < 0 || x.Target >= f).ToList(), uni.Count);
			Console.WriteLine("  for contrast, floor WITHOUT the ST filter:");
			foreach (double f in new[] { -1.0, 0.10, 0.20 })
				Reval(f < 0 ? "  no floor, no ST filter" : $"  exposure >= {f:0.00}, no ST filter",
					uni.Where(x => f < 0 || x.Target >= f).ToList(), uni.Count);

			Console.WriteLine("");
			Console.WriteLine("--- NET DELTA re-swept WITH ST Bear skipped (floor 0.10) ---");
			Console.WriteLine($"{"net delta",32} {"trades",7} {"kept%",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8} " +
				$"{"Sharpe",8} {"maxDD%",8} {"CAGR%",9}");
			foreach (double d in new[] { 0.15, 0.20, 0.25, 0.30, 0.35, 0.40 })
			{
				var t = BuildRule(_ => d, false, false)
					.Where(x => x.St != ShortTermState.Bear && x.Target >= 0.10).ToList();
				Reval($"  net delta {d:0.00}", t, t.Count);
			}
			WingDelta = rw; TargetLo = rLo; TargetHi = rHi;

			Console.WriteLine("Strike is identical in every bucket here, so any spread across rows is the TARGET carrying");
			Console.WriteLine("information about the session ahead -- not an artifact of trading a different structure.");
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
