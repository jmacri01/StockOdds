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
		// Per-LEG sizing on the exposure at that leg's entry. 0 = flat. Multipliers are raw here and
		// normalised to mean 1.0 across all legs afterwards, so average risk matches flat exactly and no
		// arm can win by quietly deploying more capital.
		public static int SizeMode = 0;
		// Gex sizing: leg risk x min(callPut, 2). The ratio is a DAILY value, so every leg in a session
		// carries the same multiplier. Scoped to SPY/QQQ -- it is inverted on IWM and pure leverage on GLD.
		// Consecutive candles that must sit under Gate before arming. 1 = the level rule tested so far.
		// Persistence is a different lever from the threshold: it trades entry PROMPTNESS for confirmation.
		public static int ArmBars = 1;
		public static bool GexSizing = false;
		public static double GexCap = 2.0;

		private sealed record Sess(string Sym, DateTime D, double Ret, int Legs, double PriorExp, double SumMult);

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
			double priorExp, int mode, double cp = double.NaN)
		{
			int n = Math.Min(tb.Count, exp.Count);
			if (n < 20) return null;
			double ST = tb[n - 1].Close;
			if (ST <= 0) return null;
			double total = 0; int legs = 0; double sumMult = 0;
			int k = 0;
			bool first = true;

			while (k < n - MinBarsLeft)
			{
				// ---- find the entry bar for this leg
				int j;
				if (mode == 0 && first) j = 0;
				// HYBRID: the prior session already closed under the gate, so the condition is satisfied
				// before the bell -- enter at the open rather than waiting for a fresh candle to confirm
				// something already true. Only meaningful at ArmBars == 1; with a persistence requirement
				// there is no prior-session run to inspect, so it falls through to the scan.
				else if (mode == 4 && first && ArmBars == 1 && priorExp < Gate) j = 0;
				else if (mode == 3 && !first) j = k;
				else
				{
					j = -1;
					for (int q = k; q < n - MinBarsLeft; q++)
						{
							// ArmBars consecutive candles must sit under the gate, the run ending at q
							if (q + 1 < ArmBars) continue;
							bool ok = true;
							for (int b = 0; b < ArmBars; b++) if (exp[q - b] >= Gate) { ok = false; break; }
							if (ok) { j = q; break; }
						}
					if (j < 0) break;
				}
				bool atOpen = (mode == 0 || mode == 4) && first && j == 0 && (mode == 0 || priorExp < Gate);
				double S = atOpen ? tb[0].Open : tb[j].Close;
				if (S <= 0) break;
				double frac = atOpen ? 1.0 : (double)(n - 1 - j) / n;
				double T = Math.Max(1e-9, frac) / 252.0;
				double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) break;
				double entryCost = cr * CostPct / 100.0;
				// deeper dip -> larger leg. Mode 3 inverts it as a sign control.
				double eAt = Math.Max(0.0, exp[Math.Min(j, exp.Count - 1)]);
				double mult = SizeMode switch
				{
					1 => Math.Max(0.05, (Gate - Math.Min(eAt, Gate)) / Math.Max(1e-9, Gate)) + 0.5,
					2 => 1.0 / (eAt + 0.05),
					3 => Math.Min(eAt, Gate) / Math.Max(1e-9, Gate) + 0.5,
					_ => 1.0
				};
				if (GexSizing && !double.IsNaN(cp)) mult *= Math.Min(GexCap, cp);
				double riskLeg = Risk * mult;
				legs++; first = false; sumMult += mult;

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
					total += riskLeg * (cr - val - entryCost - val * CostPct / 100.0) / ml;
					banked = true;
					break;
				}
				if (!banked)
				{
					double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					total += riskLeg * (cr + po - entryCost) / ml;   // rides to expiry, no exit crossing
					break;
				}
				if (mode == 0 || mode == 1) break;                    // single-entry modes stop after one leg
				k = e + 1;
			}
			return legs == 0 ? null : new Sess(sym, d, total, legs, priorExp, sumMult);
		}

		// The configuration as specified: enter at the OPEN when the prior session's closing 5m exposure
		// was already under the gate, otherwise wait for the first candle under it; bank at delta < 0.10
		// qualified on spot > short strike; re-arm only on another candle under the gate; size on gex.
		public static async Task RunFinal()
		{
			var inputs = await Collect();
			if (inputs.Count == 0) { Console.WriteLine("no data"); return; }
			double gSave = Gate; bool xSave = GexSizing;
			Gate = 0.05;

			List<Sess> Arm(int mode, bool gex, IEnumerable<Input>? univ = null)
			{
				GexSizing = gex;
				var outp = new List<Sess>();
				foreach (var x in (univ ?? inputs))
				{
					var s = Simulate(x.Sym, x.D, x.Bars, x.Exp, x.Iv, x.PriorExp, mode, x.Cp);
					if (s != null) outp.Add(s);
				}
				return outp;
			}
			(double mean, double ir, double dd, double wst, double sd) Stat(List<double> r)
			{
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				return (100 * m, sd > 1e-12 ? m / sd : 0, dd, 100 * r.Min(), sd);
			}

			var holdAll = Arm(0, false);
			var worst = holdAll.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.Ret)).First().Key;
			// gex is scoped to SPY/QQQ: inverted on IWM, pure leverage on GLD
			var sq = inputs.Where(x => x.Sym is "SPY" or "QQQ").ToList();

			Console.WriteLine($"\n===== THE SPECIFIED CONFIGURATION =====");
			Console.WriteLine($"gate {Gate:0.00} on prior close OR any candle in session; bank at delta < {FloorDelta:0.00} " +
				$"qualified; re-arm on another sub-gate candle; cost {CostPct:0.0}%/fill");
			foreach (var (tag, filt) in new[] { ("W23 REMOVED", (Func<Sess, bool>)(x => Wk(x.D) != worst)), ("FULL SAMPLE", _ => true) })
			{
				Console.WriteLine($"\n-- {tag} --");
				Console.WriteLine($"{"arm",-46} {"sess",5} {"legs/s",7} {"mean/s%",9} {"win%",7} {"IR",8} {"maxDD%",8} {"worst%",8} {"ret/DD",8}");
				void Row(string lbl, List<Sess> s, double norm = 1.0)
				{
					var f = s.Where(filt).ToList();
					if (f.Count < 20) { Console.WriteLine($"{lbl,-46} {f.Count,5}  (too few)"); return; }
					var r = f.Select(x => x.Ret / norm).ToList();
					var st = Stat(r);
					Console.WriteLine($"{lbl,-46} {f.Count,5} {f.Average(x => x.Legs),7:0.00} {st.mean,9:+0.0000;-0.0000} " +
						$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {st.ir,8:0.000} {st.dd,8:0.00} {st.wst,8:0.00} " +
						$"{st.mean / Math.Max(0.01, st.dd),8:0.000}");
				}
				Row("hold to expiry, flat [SHIPPED]", holdAll);
				Row("loop, first entry at dip", Arm(2, false));
				Row("loop, HYBRID entry (open-or-dip)", Arm(4, false));
				Console.WriteLine($"   -- SPY+QQQ only, where gex sizing is valid --");
				Row("  hold, flat", Arm(0, false, sq));
				Row("  HYBRID loop, flat", Arm(4, false, sq));
				var gx = Arm(4, true, sq);
				Row("  HYBRID loop + gex sizing (as shipped)", gx);
				double mm = gx.Sum(x => x.SumMult) / Math.Max(1, gx.Sum(x => x.Legs));
				Row($"  HYBRID loop + gex, MEAN-1 (x{mm:0.00} removed)", gx, mm);
				Console.WriteLine($"   as-shipped runs {100 * Risk * mm:0.0}% average risk vs {100 * Risk:0.0}% flat, so the mean-1 row is");
				Console.WriteLine($"   the one that is not partly leverage.");
			}
			// ---- PERSISTENCE vs LEVEL, inside the loop -------------------------------------------------
			// "5 candles under 0.20" against "1 candle under 0.05". Both wait for a quiet tape; one asks for
			// a DEEPER reading, the other for a more SUSTAINED one. In the single-entry framework persistence
			// lost outright, but the gate already changed meaning once between session-filter and re-entry
			// timer, so it is re-tested here rather than assumed.
			Console.WriteLine($"{Environment.NewLine}===== PERSISTENCE vs LEVEL inside the loop =====");
			Console.WriteLine($"{"arm",-34} {"sess",5} {"legs/s",7} {"mean/s%",9} {"win%",7} {"IR",8} {"maxDD%",8} {"worst%",8} {"ret/DD",8}");
			int aSave = ArmBars;
			void PRow(string lbl, int bars, double gate, Func<Sess, bool> filt)
			{
				ArmBars = bars; Gate = gate; GexSizing = false;
				var s2 = Arm(4, false).Where(filt).ToList();
				if (s2.Count < 20) { Console.WriteLine($"{lbl,-34} {s2.Count,5}  (too few)"); return; }
				var r = s2.Select(x => x.Ret).ToList();
				var st = Stat(r);
				Console.WriteLine($"{lbl,-34} {s2.Count,5} {s2.Average(x => x.Legs),7:0.00} {st.mean,9:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {st.ir,8:0.000} {st.dd,8:0.00} {st.wst,8:0.00} " +
					$"{st.mean / Math.Max(0.01, st.dd),8:0.000}");
			}
			foreach (var (tag, filt) in new[] { ("W23 REMOVED", (Func<Sess, bool>)(x => Wk(x.D) != worst)), ("FULL SAMPLE", _ => true) })
			{
				Console.WriteLine($"{Environment.NewLine}-- {tag} --");
				foreach (int bars in new[] { 1, 3, 5, 8 })
					foreach (double gate in new[] { 0.05, 0.10, 0.20 })
						PRow($"{bars} candle{(bars == 1 ? "" : "s")} under {gate:0.00}", bars, gate, filt);
			}
			ArmBars = aSave;
			Gate = gSave; GexSizing = xSave;
		}

		// Cached per-session inputs, so the gate can be swept without refetching or re-deriving anything.
		private sealed record Input(string Sym, DateTime D, List<OhlcBar> Bars, List<double> Exp, double Iv, double PriorExp, double Cp);

		private static Dictionary<DateTime, double> LoadCallPut(string symbol)
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
				if (DateTime.TryParse(f[di], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
					&& double.TryParse(f[ci], NumberStyles.Any, CultureInfo.InvariantCulture, out var cg)
					&& double.TryParse(f[pi], NumberStyles.Any, CultureInfo.InvariantCulture, out var pg)
					&& Math.Abs(pg) > 0)
					m[d.Date] = cg / Math.Abs(pg);
			}
			return m;
		}

		private static async Task<List<Input>> Collect()
		{
			var outp = new List<Input>();
			foreach (var symbol in Symbols)
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
				var cpMap = LoadCallPut(symbol);
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
					if (Math.Min(pToday.Count, tb.Count) < 20) continue;
					outp.Add(new Input(symbol, dTr, tb, pToday, h * VolRiskPremium, pPrev[^1],
						cpMap.TryGetValue(dSig, out double cpv) ? cpv : double.NaN));
				}
			}
			return outp;
		}

		// Sweep the entry/re-entry threshold inside the LOOP, which is a different question from the old
		// session-filter sweep: here the gate decides WHEN to arm, not WHETHER to trade the day at all.
		public static async Task RunGateSweep()
		{
			var inputs = await Collect();
			if (inputs.Count == 0) { Console.WriteLine("no data"); return; }
			double gSave = Gate;

			List<Sess> Arm(int mode, double gate)
			{
				Gate = gate;
				var outp = new List<Sess>();
				foreach (var x in inputs)
				{
					var s = Simulate(x.Sym, x.D, x.Bars, x.Exp, x.Iv, x.PriorExp, mode);
					if (s != null) outp.Add(s);
				}
				return outp;
			}
			var hold = Arm(0, 0.10);
			var worst = hold.GroupBy(x => Wk(x.D)).OrderBy(g => g.Average(x => x.Ret)).First().Key;

			(double mean, double ir, double dd, double wst, double sd) Stat(List<double> r)
			{
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				return (100 * m, sd > 1e-12 ? m / sd : 0, dd, 100 * r.Min(), sd);
			}

			Console.WriteLine($"\n===== GATE SWEEP INSIDE THE LOOP (entry AND re-entry threshold) =====");
			Console.WriteLine($"{inputs.Count} sessions; floor-qualified bank at delta < {FloorDelta:0.00}; cost {CostPct:0.0}%/fill");
			foreach (var (tag, filt) in new[] { ("W23 REMOVED", (Func<Sess, bool>)(x => Wk(x.D) != worst)), ("FULL SAMPLE", _ => true) })
			{
				var hr = hold.Where(filt).Select(x => x.Ret).ToList();
				var hs = Stat(hr);
				Console.WriteLine($"\n-- {tag} --");
				Console.WriteLine($"{"gate",7} {"sess",5} {"legs/s",7} {"mean/s%",9} {"win%",7} {"IR",8} {"maxDD%",8} {"worst%",8} {"ret/DD",8} {"vs matched",11}");
				Console.WriteLine($"{"hold",7} {hr.Count,5} {1.00,7:0.00} {hs.mean,9:+0.0000;-0.0000} " +
					$"{100.0 * hr.Count(z => z > 0) / hr.Count,7:0.0} {hs.ir,8:0.000} {hs.dd,8:0.00} {hs.wst,8:0.00} " +
					$"{hs.mean / Math.Max(0.01, hs.dd),8:0.000} {"--",11}");
				foreach (double g in new[] { 0.01, 0.02, 0.03, 0.05, 0.10, 0.15, 0.20, 0.30, 0.50 })
				{
					var s = Arm(2, g).Where(filt).ToList();
					if (s.Count < 20) { Console.WriteLine($"{g,7:0.00} {s.Count,5}  (too few)"); continue; }
					var r = s.Select(x => x.Ret).ToList();
					var st = Stat(r);
					// vol-matched haircut: scale HOLD to this arm's volatility, then compare ret/DD. A rule
					// that merely deploys more capital cannot beat its own matched haircut on this column.
					double mult = hs.sd > 1e-12 ? st.sd / hs.sd : 1.0;
					var hm = Stat(hr.Select(z => z * mult).ToList());
					Console.WriteLine($"{g,7:0.00} {s.Count,5} {s.Average(x => x.Legs),7:0.00} {st.mean,9:+0.0000;-0.0000} " +
						$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {st.ir,8:0.000} {st.dd,8:0.00} {st.wst,8:0.00} " +
						$"{st.mean / Math.Max(0.01, st.dd),8:0.000} {hm.mean / Math.Max(0.01, hm.dd),11:0.000}");
				}
				Console.WriteLine($"   last column = ret/DD of hold scaled to the SAME volatility; the loop only earns");
				Console.WriteLine($"   credit where its ret/DD exceeds it.");
			}
			// ---- PER-LEG SIZING ON ENTRY EXPOSURE, at a fixed gate ------------------------------------
			// Sizing failed on the single-entry framework, but the loop is a different setting: entries now
			// happen at varied depths below the gate, so there is a real spread of entry exposures to size
			// on. Multipliers are normalised to mean 1.0 ACROSS ALL LEGS, so average risk equals flat and
			// nothing can win by deploying more. Mode 3 inverts the tilt as a sign control.
			int sSave = SizeMode;
			string[] sizeNames = { "flat (no sizing)", "deeper dip -> bigger leg", "1/(exp+0.05)", "INVERTED (sign control)" };
			foreach (double g in new[] { 0.05, 0.10 })
			{
				Console.WriteLine($"\n-- per-leg sizing on entry exposure, gate {g:0.00}, W23 removed, mean-1 normalised --");
				Console.WriteLine($"{"sizing",-28} {"legs/s",7} {"mean/s%",9} {"IR",8} {"maxDD%",8} {"worst%",8} {"ret/DD",8}");
				for (int sm = 0; sm < 4; sm++)
				{
					SizeMode = sm;
					var s = Arm(2, g).Where(x => Wk(x.D) != worst).ToList();
					if (s.Count < 20) { Console.WriteLine($"{sizeNames[sm],-28}  (too few)"); continue; }
					double meanMult = s.Sum(x => x.SumMult) / Math.Max(1, s.Sum(x => x.Legs));
					var r = s.Select(x => x.Ret / meanMult).ToList();     // normalise to mean multiplier 1.0
					var st = Stat(r);
					Console.WriteLine($"{sizeNames[sm],-28} {s.Average(x => x.Legs),7:0.00} {st.mean,9:+0.0000;-0.0000} " +
						$"{st.ir,8:0.000} {st.dd,8:0.00} {st.wst,8:0.00} {st.mean / Math.Max(0.01, st.dd),8:0.000}");
				}
			}
			SizeMode = sSave;
			Gate = gSave;
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
				var cpMap = LoadCallPut(symbol);
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
