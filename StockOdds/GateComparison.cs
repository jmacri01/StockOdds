using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Does the dealer-gamma gate survive a change of vendor?
	//
	// Every gated result so far keys on the SqueezeMetrics scalar. Unusual Whales publishes its own SPX gamma and
	// the two agree on the SIGN -- the thing the gate actually tests -- only 68.4% of the time (level correlation
	// +0.768; UW calls gamma positive on 56.9% of days against SqueezeMetrics' 88.4%). They are not the same
	// measurement, so this is not a data upgrade, it is a second opinion. If the gate is picking up a real gamma
	// regime it should show up in both. If it only works on one, it was a property of that vendor's construction.
	//
	// TIMING. UW rows captured live (2024-08-23 onward) are stamped 13:33/14:33 UTC = 9:33 ET, i.e. three minutes
	// after the open, computed from prior-close OI. So a SAME-DAY UW gate is reachable in a way the SqueezeMetrics
	// end-of-day print never was -- but only if entry happens after 9:33, which this daily backtest does NOT model
	// (it enters at the open). That arm is therefore labelled as carrying ~3 minutes of look-ahead: a real
	// candidate, not yet a clean result. Rows before 2024-08-23 carry a bulk-backfill stamp, so the 9:33 property
	// is assumed rather than verified over most of the window.
	//
	// Everything is restricted to 2022-03-30+, where UW history begins, and every arm runs the shipped config so
	// only the gate varies.
	public static class GateComparison
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;
		public static DateTime From = new DateTime(2022, 3, 30);

		private sealed record Tr(DateTime D, DateTime Sig, double R, ShortTermState St = ShortTermState.BearNeutral);

		public static Dictionary<DateTime, double> UwRatio = new();   // |put_gex| / call_gex, by date

		private static Dictionary<DateTime, double> LoadUw()
		{
			var m = new Dictionary<DateTime, double>();
			UwRatio = new Dictionary<DateTime, double>();
			string p = Path.Combine(Path.GetFullPath(Universe.DataDir), "gex_uw_spx.csv");
			if (!File.Exists(p)) { Console.WriteLine($"missing {p}"); return m; }
			var lines = File.ReadAllLines(p);
			var head = lines[0].Split(',');
			int di = Array.IndexOf(head, "date"), ni = Array.IndexOf(head, "net_gex");
			int ci = Array.IndexOf(head, "call_gex"), pi = Array.IndexOf(head, "put_gex");
			for (int i = 1; i < lines.Length; i++)
			{
				var f = lines[i].Split(',');
				if (f.Length <= Math.Max(di, ni)) continue;
				if (DateTime.TryParse(f[di], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
					&& double.TryParse(f[ni], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
					m[d.Date] = v;
				// The ratio is scale-free by construction, so unlike the raw level it does not drift with the
				// growth of the options market -- no trailing-percentile correction needed to compare eras.
				if (ci >= 0 && pi >= 0 && f.Length > Math.Max(ci, pi)
					&& DateTime.TryParse(f[di], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)
					&& double.TryParse(f[ci], NumberStyles.Any, CultureInfo.InvariantCulture, out var cg)
					&& double.TryParse(f[pi], NumberStyles.Any, CultureInfo.InvariantCulture, out var pg)
					&& cg > 0)
					UwRatio[d2.Date] = Math.Abs(pg) / cg;
			}
			return m;
		}

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var sm = await GexClient.ByDateAsync();
			var uw = LoadUw();
			if (uw.Count == 0) return;
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			var pos = new Dictionary<DateTime, double>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
				pos[eng.ReturnDates[k].Date] = eng.Positions[k];
			var stm = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < eng.StState.Count && k < eng.ReturnDates.Count; k++)
				stm[eng.ReturnDates[k].Date] = eng.StState[k];

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
					hv[bars[i].Date.Date] = Math.Max(0.05, Math.Sqrt(lr.Sum(x => (x - m) * (x - m)) / (lr.Count - 1)) * Math.Sqrt(252.0));
				}
			}

			double T = 1.0 / 252.0;
			var all = new List<Tr>();
			for (int i = 1; i + 1 < bars.Count; i++)
			{
				var dSig = bars[i].Date.Date;
				var dTrade = bars[i + 1].Date.Date;
				if (dTrade < From) continue;
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double target) || target < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTrade)) continue;
				if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				double iv = h * VolRiskPremium;
				double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
				double maxLoss = (kS - kL) - cr;
				if (cr <= 1e-9 || maxLoss <= 1e-9) continue;
				double payoff = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
				all.Add(new Tr(dTrade, dSig, (cr + payoff) / maxLoss));
			}

			Console.WriteLine($"\n===== {symbol}: GAMMA GATE, SqueezeMetrics vs UNUSUAL WHALES ({From:yyyy-MM} onward) =====");
			Console.WriteLine($"shipped config; {all.Count} ungated trades {all.First().D:yyyy-MM-dd} -> {all.Last().D:yyyy-MM-dd}");
			Console.WriteLine($"\n{"gate",40} {"trades",7} {"%kept",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8} " +
				$"{"maxDD%",8} {"CAGR%",9}");

			void Row(string label, Func<Tr, bool> keep)
			{
				var t = all.Where(keep).ToList();
				if (t.Count < 40) { Console.WriteLine($"{label,40} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, p = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e > p) p = e; double q = (p - e) / p * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				Console.WriteLine($"{label,40} {t.Count,7} {100.0 * t.Count / all.Count,7:0.0} " +
					$"{100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {(Math.Pow(Math.Max(1e-9, e), 1 / yrs) - 1) * 100,9:0.0}");
			}

			bool SmPrev(Tr t) => sm.TryGetValue(t.Sig, out var g) && g.Gex > 0;
			bool UwPrev(Tr t) => uw.TryGetValue(t.Sig, out var v) && v > 0;
			bool UwSame(Tr t) => uw.TryGetValue(t.D, out var v) && v > 0;

			Row("NO GATE", _ => true);
			Row("SqueezeMetrics prior-day [SHIPPED]", SmPrev);
			Row("UW prior-day (clean)", UwPrev);
			Row("UW same-day 9:33 (needs 9:35 entry)", UwSame);
			Row("BOTH vendors agree positive", t => SmPrev(t) && UwPrev(t));
			Row("UW prior-day NEGATIVE (inverse)", t => uw.TryGetValue(t.Sig, out var v) && v < 0);
			Row("SqueezeMetrics NEGATIVE (inverse)", t => sm.TryGetValue(t.Sig, out var g) && g.Gex < 0);

			// SPLIT-HALF. The UW window is one regime (2022-03+), so a gate that only works in half of it is
			// fitted to that half. Both vendors are shown in each half on the same trades.
			var ordered = all.OrderBy(x => x.D).ToList();
			DateTime mid = ordered[ordered.Count / 2].D;
			Console.WriteLine("");
			Console.WriteLine($"{"split-half",40} {"trades",7} {"%kept",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8} {"maxDD%",8}");
			void Half(string lbl, Func<Tr, bool> keep, Func<Tr, bool> half)
			{
				var t = all.Where(x => keep(x) && half(x)).ToList();
				if (t.Count < 40) { Console.WriteLine($"{lbl,40} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, p = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e > p) p = e; double q = (p - e) / p * 100; if (q > dd) dd = q; }
				int denom = all.Count(half);
				Console.WriteLine($"{lbl,40} {t.Count,7} {100.0 * t.Count / denom,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00}");
			}
			foreach (var (hl, hf) in new (string, Func<Tr, bool>)[]
				{ ("1st", x => x.D < mid), ("2nd", x => x.D >= mid) })
			{
				Half($"[{hl}] no gate", _ => true, hf);
				Half($"[{hl}] SqueezeMetrics prior-day", SmPrev, hf);
				Half($"[{hl}] UW prior-day", UwPrev, hf);
				Half($"[{hl}] UW same-day", UwSame, hf);
			}

			// BACKFILL vs LIVE. UW rows from 2024-08-23 onward were captured live and stamped 9:33 ET. Everything
			// before that carries one bulk-backfill stamp, and there is no way to know from the data whether the
			// backfill reconstructed a 9:33 snapshot or simply used END-OF-DAY figures. If it used EOD, then the
			// SAME-DAY arm is reading the future -- but ONLY over the backfilled rows. Splitting exactly on that
			// boundary rather than on the median date turns that from a suspicion into a measurement: a same-day
			// edge that exists in the backfill and vanishes in the live rows is look-ahead, not signal.
			DateTime liveFrom = new DateTime(2024, 8, 23);
			Console.WriteLine("");
			Console.WriteLine($"{"backfilled vs live-captured UW rows",40} {"trades",7} {"%kept",7} {"mean/tr%",10} " +
				$"{"win%",7} {"IR/tr",8} {"maxDD%",8}");
			foreach (var (hl, hf) in new (string, Func<Tr, bool>)[]
				{ ("BACKFILL <2024-08-23", x => x.D < liveFrom), ("LIVE >=2024-08-23", x => x.D >= liveFrom) })
			{
				Half($"[{hl}] no gate", _ => true, hf);
				Half($"[{hl}] UW prior-day", UwPrev, hf);
				Half($"[{hl}] UW same-day", UwSame, hf);
			}
			Console.WriteLine("Same-day minus prior-day should be SIMILAR in both blocks if the backfill is honest.");

			// ---- PUT/CALL GAMMA RATIO x NET DELTA -------------------------------------------------------
			// ratio = |put_gex| / call_gex on the PRIOR day. Above 1 means put gamma dominates, which is the same
			// condition as net gamma < 0 -- so the SIGN is not new information. What is new is the MAGNITUDE:
			// the ratio grades how lopsided the book is, on a scale that does not drift between eras.
			//
			// Re-pricing per delta means rebuilding the trade set, so the strike sweep is done here rather than
			// reusing `all`.
			double[] dGrid = { 0.10, 0.15, 0.20, 0.25, 0.30, 0.35 };
			var byD = new Dictionary<double, List<Tr>>();
			foreach (double nd in dGrid)
			{
				var lst = new List<Tr>();
				for (int i = 1; i + 1 < bars.Count; i++)
				{
					var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
					if (dTr < From) continue;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st2) && st2 == ShortTermState.Bear) continue;
					double S = bars[i + 1].Open, ST2 = bars[i + 1].Close;
					if (S <= 0 || ST2 <= 0) continue;
					double iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, nd + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml = (kS - kL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, kS - ST2) + Math.Max(0, kL - ST2);
					lst.Add(new Tr(dTr, dSig, (cr + po) / ml));
				}
				byD[nd] = lst;
			}
			double RIr(List<Tr> t)
			{
				if (t.Count < 30) return double.NaN;
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				return sd > 0 ? m / sd : 0;
			}
			var rvals = byD[0.20].Where(x => UwRatio.ContainsKey(x.Sig)).Select(x => UwRatio[x.Sig])
			                     .OrderBy(v => v).ToList();
			double q1 = rvals[(int)(rvals.Count * 0.25)], q2 = rvals[(int)(rvals.Count * 0.50)], q3 = rvals[(int)(rvals.Count * 0.75)];
			Console.WriteLine("");
			Console.WriteLine($"--- IR by PRIOR-DAY put/call gamma ratio x net delta (ratio quartiles: {q1:0.00} / {q2:0.00} / {q3:0.00}) ---");
			(string L, Func<Tr, bool> P)[] rb =
			{
				($"ratio < {q1:0.00} (call-heavy)", x => UwRatio.TryGetValue(x.Sig, out var v) && v < q1),
				($"{q1:0.00} - {q2:0.00}", x => UwRatio.TryGetValue(x.Sig, out var v) && v >= q1 && v < q2),
				($"{q2:0.00} - {q3:0.00}", x => UwRatio.TryGetValue(x.Sig, out var v) && v >= q2 && v < q3),
				($"ratio > {q3:0.00} (put-heavy)", x => UwRatio.TryGetValue(x.Sig, out var v) && v >= q3),
			};
			Console.Write($"{"bucket",26} {"n",5}");
			foreach (double nd in dGrid) Console.Write($" {nd,7:0.00}");
			Console.WriteLine($" {"best",6} {"1stH",6} {"2ndH",6}");
			var ordR = byD[0.20].OrderBy(x => x.D).ToList();
			DateTime rmid = ordR[ordR.Count / 2].D;
			foreach (var (L, P) in rb)
			{
				Console.Write($"{L,26} {byD[0.20].Count(P),5}");
				double best = double.NegativeInfinity, bd = double.NaN;
				foreach (double nd in dGrid)
				{
					double ir = RIr(byD[nd].Where(P).ToList());
					Console.Write(double.IsNaN(ir) ? $" {"-",7}" : $" {ir,7:0.000}");
					if (!double.IsNaN(ir) && ir > best) { best = ir; bd = nd; }
				}
				string BestH(Func<Tr, bool> hf)
				{
					double b2 = double.NegativeInfinity, d2 = double.NaN;
					foreach (double nd in dGrid)
					{
						double ir = RIr(byD[nd].Where(x => P(x) && hf(x)).ToList());
						if (!double.IsNaN(ir) && ir > b2) { b2 = ir; d2 = nd; }
					}
					return double.IsNaN(d2) ? "few" : $"{d2:0.00}";
				}
				Console.WriteLine($" {bd,6:0.00} {BestH(x => x.D < rmid),6} {BestH(x => x.D >= rmid),6}");
			}
			Console.WriteLine("The ratio needs no drift correction, so a stable optimum here would be worth more than the");
			Console.WriteLine("raw-level version -- but it must still agree across halves to be a rule rather than a fit.");

			// The ratio's LEVEL separates quality far more than the delta grid does, so test it as a GATE. Note
			// ratio > 1 is arithmetically the same condition as net gamma < 0, so "ratio < 1.00" IS the shipped
			// sign gate -- the question is whether tightening below 1 buys anything, and whether it survives the
			// split and the backfill boundary that already exposed the same-day arm.
			Console.WriteLine("");
			Console.WriteLine($"{"ratio gate (prior day), delta 0.20",40} {"trades",7} {"%kept",7} {"mean/tr%",10} " +
				$"{"win%",7} {"IR/tr",8} {"maxDD%",8}");
			var baseSet = byD[0.20];
			void RGate(string lbl, double thr, Func<Tr, bool> half = null)
			{
				var t = baseSet.Where(x => UwRatio.TryGetValue(x.Sig, out var v) && v < thr
				                           && (half == null || half(x))).ToList();
				if (t.Count < 40) { Console.WriteLine($"{lbl,40} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				int denom = half == null ? baseSet.Count : baseSet.Count(half);
				Console.WriteLine($"{lbl,40} {t.Count,7} {100.0 * t.Count / denom,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00}");
			}
			RGate("no ratio gate", 1e9);
			foreach (double thr in new[] { 1.00, 0.90, 0.84, 0.73 }) RGate($"ratio < {thr:0.00}", thr);
			Console.WriteLine("  split-half:");
			foreach (double thr in new[] { 1.00, 0.84, 0.73 })
			{
				RGate($"  [1st] ratio < {thr:0.00}", thr, x => x.D < rmid);
				RGate($"  [2nd] ratio < {thr:0.00}", thr, x => x.D >= rmid);
			}
			Console.WriteLine("  backfill vs live-captured:");
			DateTime lf = new DateTime(2024, 8, 23);
			foreach (double thr in new[] { 1.00, 0.84 })
			{
				RGate($"  [backfill] ratio < {thr:0.00}", thr, x => x.D < lf);
				RGate($"  [live]     ratio < {thr:0.00}", thr, x => x.D >= lf);
			}

			// ---- OUT-OF-SAMPLE THRESHOLD TESTS ----------------------------------------------------------
			// The 0.73/0.84 cuts above are sample quartiles, so their exact values are fitted. Two ways to remove
			// that, and they fail differently:
			//   (A) FIT-THEN-APPLY  choose the best absolute threshold on the first half ONLY, then score it on
			//       the second. Clean statistically, but yields a fixed number that could still drift out of date.
			//   (B) EXPANDING PERCENTILE  gate on "today's ratio sits in the bottom P% of every ratio observed so
			//       far". Never fitted at all, adapts to drift, and is the form that could actually be traded --
			//       each day's threshold uses only prior days. This is the honest operating rule.
			Console.WriteLine("");
			Console.WriteLine("--- (A) threshold FITTED on the first half, APPLIED to the second ---");
			var ordA = baseSet.Where(x => UwRatio.ContainsKey(x.Sig)).OrderBy(x => x.D).ToList();
			DateTime amid = ordA[ordA.Count / 2].D;
			double bestThr = double.NaN, bestIr = double.NegativeInfinity;
			foreach (double thr in new[] { 0.65, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95, 1.00 })
			{
				var t = ordA.Where(x => x.D < amid && UwRatio[x.Sig] < thr).ToList();
				double ir = RIr(t);
				if (!double.IsNaN(ir) && t.Count >= 60 && ir > bestIr) { bestIr = ir; bestThr = thr; }
			}
			Console.WriteLine($"  best first-half threshold = {bestThr:0.00} (in-sample IR {bestIr:0.000})");
			Console.WriteLine($"{"applied to SECOND half",40} {"trades",7} {"%kept",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8} {"maxDD%",8}");
			void Score(string lbl, List<Tr> t, int denom)
			{
				if (t.Count < 30) { Console.WriteLine($"{lbl,40} {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				Console.WriteLine($"{lbl,40} {t.Count,7} {100.0 * t.Count / Math.Max(1, denom),7:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00}");
			}
			var second = ordA.Where(x => x.D >= amid).ToList();
			Score("  ungated", second, second.Count);
			Score("  sign gate (ratio < 1.00)", second.Where(x => UwRatio[x.Sig] < 1.00).ToList(), second.Count);
			Score($"  FITTED threshold {bestThr:0.00}", second.Where(x => UwRatio[x.Sig] < bestThr).ToList(), second.Count);

			Console.WriteLine("");
			Console.WriteLine("--- (B) EXPANDING-window percentile gate (never fitted; each day uses only prior days) ---");
			Console.WriteLine($"{"gate",40} {"trades",7} {"%kept",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8} {"maxDD%",8}");
			var hist = new List<double>();
			var pctOf = new Dictionary<DateTime, double>();
			foreach (var x in ordA)
			{
				double v = UwRatio[x.Sig];
				if (hist.Count >= 120)
					pctOf[x.D] = (double)hist.Count(z => z < v) / hist.Count;
				hist.Add(v);
			}
			var elig = ordA.Where(x => pctOf.ContainsKey(x.D)).ToList();
			Console.WriteLine($"  (warm-up consumes {ordA.Count - elig.Count} trades; {elig.Count} remain from {elig.First().D:yyyy-MM})");
			Score("  ungated (eligible window)", elig, elig.Count);
			foreach (double pp in new[] { 0.20, 0.30, 0.40, 0.50, 0.60, 0.75 })
				Score($"  bottom {100 * pp:0}% of trailing ratios", elig.Where(x => pctOf[x.D] < pp).ToList(), elig.Count);
			DateTime bmid = elig[elig.Count / 2].D;
			Console.WriteLine("  split-half of the expanding-percentile rule:");
			foreach (double pp in new[] { 0.30, 0.50 })
			{
				Score($"  [1st] bottom {100 * pp:0}%", elig.Where(x => x.D < bmid && pctOf[x.D] < pp).ToList(), elig.Count(x => x.D < bmid));
				Score($"  [2nd] bottom {100 * pp:0}%", elig.Where(x => x.D >= bmid && pctOf[x.D] < pp).ToList(), elig.Count(x => x.D >= bmid));
			}

			// ---- SIZING SWEEP under the ratio <= 1 gate --------------------------------------------------
			// ratio <= 1 is the sign gate. Shown across risk levels against the ungated book and the tighter
			// adaptive rule, because the gate LOWERS compounded return (it removes a quarter of the trades) while
			// lowering drawdown more -- so which one "works well" depends entirely on which axis is binding.
			Console.WriteLine("");
			Console.WriteLine($"--- CAGR / drawdown by risk level, {From:yyyy-MM} onward ---");
			Console.WriteLine($"{"arm",34} {"risk",6} {"trades",7} {"mean/tr%",10} {"IR",7} {"maxDD%",8} {"CAGR%",10} {"worst%",8}");
			void Sz(string lbl, List<Tr> t, double risk)
			{
				if (t.Count < 30) { Console.WriteLine($"{lbl,38} {100 * risk,5:0.#}% {t.Count,7}  (too few)"); return; }
				var r = t.Select(x => risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				double cagr = e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100;
				Console.WriteLine($"{lbl,38} {100 * risk,5:0.#}% {t.Count,7} {100 * m,10:+0.0000;-0.0000} " +
					$"{(sd > 0 ? m / sd : 0),7:0.000} {dd,8:0.00} {cagr,10:0.0} {100 * r.Min(),8:0.00}");
			}
			// The percentile map is keyed by DATE, so it transfers to any delta without refitting -- the gate is
			// unchanged, only the structure it gates differs.
			var eligDates = new HashSet<DateTime>(elig.Select(x => x.D));
			foreach (double nd in new[] { 0.20, 0.35 })
			{
				Console.WriteLine($"  == net delta {nd:0.00} (short leg {nd + WingDelta:0.00}) ==");
				var set = byD[nd].Where(x => UwRatio.ContainsKey(x.Sig)).ToList();
				var ung = set;
				var le1 = set.Where(x => UwRatio[x.Sig] <= 1.00).ToList();
				var b30 = set.Where(x => eligDates.Contains(x.D) && pctOf[x.D] < 0.30).ToList();
				foreach (double rk in new[] { 0.05, 0.10, 0.15 })
				{
					Sz("ungated", ung, rk);
					Sz("ratio <= 1.00 (sign gate)", le1, rk);
					Sz("bottom 30% adaptive", b30, rk);
					Console.WriteLine();
				}
			}

			// ---- ST STATE BREAKDOWN INSIDE EACH GAMMA REGIME ---------------------------------------------
			// The ST-Bear skip is REMOVED here: the question is what every state does when put gamma dominates,
			// and the shipped filter would hide the state most likely to matter. Both sides of ratio = 1 are shown,
			// because "bad in put-heavy tape" only means something against the same state in call-heavy tape.
			var noFilter = new List<Tr>();
			for (int i = 1; i + 1 < bars.Count; i++)
			{
				var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
				if (dTr < From) continue;
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				if (!UwRatio.ContainsKey(dSig)) continue;
				double S = bars[i + 1].Open, STc = bars[i + 1].Close;
				if (S <= 0 || STc <= 0) continue;
				double iv = h * VolRiskPremium;
				double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) continue;
				double po = -Math.Max(0, kS - STc) + Math.Max(0, kL - STc);
				stm.TryGetValue(dSig, out var stv);
				noFilter.Add(new Tr(dTr, dSig, (cr + po) / ml, stv));
			}
			Console.WriteLine("");
			Console.WriteLine($"--- MEAN RETURN BY ST STATE, split on the prior-day put/call gamma ratio ---");
			Console.WriteLine($"(net delta {NetDelta:0.00}, risk {100 * Risk:0.#}%, ST-Bear filter REMOVED, {From:yyyy-MM} onward)");
			Console.WriteLine($"{"ST state",16} | {"ratio <= 1 (call-heavy)",34} | {"ratio > 1 (put-heavy)",34}");
			Console.WriteLine($"{"",16} | {"n",5} {"mean/tr%",10} {"win%",7} {"IR",8} | {"n",5} {"mean/tr%",10} {"win%",7} {"IR",8}");
			string Cell(List<Tr> t)
			{
				if (t.Count < 15) return $"{t.Count,5} {"(too few)",27}";
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / Math.Max(1, r.Count - 1));
				return $"{t.Count,5} {100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000}";
			}
			foreach (var st in new[] { ShortTermState.Bull, ShortTermState.BullNeutral,
			                           ShortTermState.BearNeutral, ShortTermState.Bear })
			{
				var lo = noFilter.Where(x => x.St == st && UwRatio[x.Sig] <= 1.0).ToList();
				var hi = noFilter.Where(x => x.St == st && UwRatio[x.Sig] > 1.0).ToList();
				Console.WriteLine($"{st,16} | {Cell(lo),34} | {Cell(hi),34}");
			}
			var aLo = noFilter.Where(x => UwRatio[x.Sig] <= 1.0).ToList();
			var aHi = noFilter.Where(x => UwRatio[x.Sig] > 1.0).ToList();
			Console.WriteLine($"{"ALL STATES",16} | {Cell(aLo),34} | {Cell(aHi),34}");

			// ---- EXEMPT ST BULL FROM THE GATE -------------------------------------------------------------
			// ST Bull barely notices put-heavy gamma (+1.830 vs +1.906 mean/trade, IR 0.501 vs 0.562) while the
			// middle states lose 29-61% of their edge. So gating ST Bull away may be discarding good trades for
			// nothing. All three arms keep the shipped ST-Bear skip; only the gate's SCOPE changes.
			var shipBase = noFilter.Where(x => x.St != ShortTermState.Bear).ToList();
			var armNoGate = shipBase;
			var armShipped = shipBase.Where(x => UwRatio[x.Sig] <= 1.0).ToList();
			var armBullExempt = shipBase.Where(x => UwRatio[x.Sig] <= 1.0 || x.St == ShortTermState.Bull).ToList();
			Console.WriteLine("");
			Console.WriteLine($"--- gate SCOPE x risk level (ST Bear skipped throughout, net delta {NetDelta:0.00}) ---");
			Console.WriteLine($"{"arm",38} {"risk",6} {"trades",7} {"mean/tr%",10} {"IR",7} {"maxDD%",8} {"CAGR%",10}");
			foreach (double rk in new[] { 0.05, 0.10, 0.15 })
			{
				Sz("no gate", armNoGate, rk);
				Sz("gate on ALL states [shipped scope]", armShipped, rk);
				Sz("gate, ST Bull EXEMPT", armBullExempt, rk);
				Console.WriteLine();
			}
			Console.WriteLine($"  trade counts: no gate {armNoGate.Count}, gated {armShipped.Count}, " +
				$"Bull-exempt {armBullExempt.Count} (+{armBullExempt.Count - armShipped.Count} put-heavy Bull days let back in)");

			// ---- FINE THRESHOLD SWEEP ON THE FULL BOOK ---------------------------------------------------
			// The shipped gate sits at ratio 1.00 (equivalently net gamma > 0). This asks where it SHOULD sit.
			// Each row carries its own split-half IRs, because a threshold that only works in one half is fitted
			// to that half -- and with a dozen thresholds searched, the best single number is guaranteed to
			// flatter itself. Stability across the halves is the thing to read, not the peak.
			Console.WriteLine("");
			Console.WriteLine($"--- RATIO THRESHOLD SWEEP, full book, {100 * Risk:0.#}% risk ---");
			Console.WriteLine($"{"gate",22} {"n",6} {"%kept",7} {"mean/tr%",10} {"win%",7} {"IR",8} " +
				$"{"maxDD%",8} {"CAGR%",10} {"IR 1stH",9} {"IR 2ndH",9}");
			var univ = baseSet.Where(x => UwRatio.ContainsKey(x.Sig)).OrderBy(x => x.D).ToList();
			DateTime tmid = univ[univ.Count / 2].D;
			double IrOnly(List<Tr> t)
			{
				if (t.Count < 25) return double.NaN;
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				return sd > 0 ? m / sd : 0;
			}
            void ThrRow(string lbl, Func<Tr, bool> keep)
			{
				var t = univ.Where(keep).ToList();
				if (t.Count < 25) { Console.WriteLine($"{lbl,22} {t.Count,6}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				double h1 = IrOnly(t.Where(x => x.D < tmid).ToList());
				double h2 = IrOnly(t.Where(x => x.D >= tmid).ToList());
				Console.WriteLine($"{lbl,22} {t.Count,6} {100.0 * t.Count / univ.Count,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} " +
					$"{(double.IsNaN(h1) ? "-" : h1.ToString("0.000")),9} {(double.IsNaN(h2) ? "-" : h2.ToString("0.000")),9}");
			}
			ThrRow("no gate", _ => true);
			foreach (double th in new[] { 1.30, 1.20, 1.10, 1.05, 1.00, 0.95, 0.90, 0.85, 0.80, 0.75, 0.70, 0.65, 0.60 })
				ThrRow($"ratio < {th:0.00}" + (Math.Abs(th - 1.0) < 1e-9 ? "  [SHIPPED]" : ""), x => UwRatio[x.Sig] < th);
			Console.WriteLine("Read the two split-half columns together: a threshold worth using should hold up in BOTH,");
			Console.WriteLine("and the peak IR across a dozen searched thresholds is inflated by the search itself.");

			// ---- SIZE ON THE RATIO INSTEAD OF GATING ON IT -----------------------------------------------
			// A gate throws sessions away; sizing keeps them all and varies the stake. Shipped filters stay on,
			// so this is purely about how much to risk given the ratio.
			//
			// THE CONTROL: any scheme that stakes more in total will beat flat 10% for trivial reasons, so every
			// scheme is scored against a FLAT rule at its own AVERAGE risk. The paired difference against that
			// control is exactly cov(stake, outcome) -- a direct test of whether the stake lands on better trades.
			// An INVERTED scheme is carried as a sign control, matched on average risk, and must lose by roughly
			// as much as the real one wins.
			Console.WriteLine("");
			Console.WriteLine("--- SIZING ON THE RATIO (shipped filters kept, no gate) ---");
			var sz = baseSet.Where(x => UwRatio.ContainsKey(x.Sig)).OrderBy(x => x.D).ToList();
			var rl = sz.Select(x => UwRatio[x.Sig]).OrderBy(v => v).ToList();
			double p33 = rl[(int)(rl.Count * 0.3333)], p67 = rl[(int)(rl.Count * 0.6667)];
			Console.WriteLine($"  ratio terciles: {p33:0.00} / {p67:0.00}");
			Console.WriteLine($"{"scheme",34} {"avgRisk%",9} {"mean/tr%",10} {"win%",7} {"IR",8} {"Sharpe",8} " +
				$"{"maxDD%",8} {"CAGR%",10} {"paired t",9}");

			void Scheme(string lbl, Func<double, double> riskOf, Func<double, double>? baseline = null)
			{
				var r = sz.Select(x => riskOf(UwRatio[x.Sig]) * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (sz.Last().D - sz.First().D).TotalDays / 365.25);
				string ts = "";
				if (baseline != null)
				{
					var d = sz.Select(x => (riskOf(UwRatio[x.Sig]) - baseline(UwRatio[x.Sig])) * x.R).ToList();
					double dm = d.Average();
					double dsd = Math.Sqrt(d.Sum(z => (z - dm) * (z - dm)) / (d.Count - 1));
					ts = dsd > 0 ? $"{dm / (dsd / Math.Sqrt(d.Count)):+0.00;-0.00}" : "";
				}
				Console.WriteLine($"{lbl,34} {100 * sz.Average(x => riskOf(UwRatio[x.Sig])),9:0.00} " +
					$"{100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {(sd > 0 ? m / sd * Math.Sqrt(sz.Count / yrs) : 0),8:0.000} " +
					$"{dd,8:0.00} {(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {ts,9}");
			}

			// tercile tiers: heavier when call gamma dominates (low ratio), lighter when puts do
			Func<double, double> tier = v => v < p33 ? 0.15 : v < p67 ? 0.10 : 0.05;
			Func<double, double> tierInv = v => v < p33 ? 0.05 : v < p67 ? 0.10 : 0.15;
			double tbar = sz.Average(x => tier(UwRatio[x.Sig]));
			// continuous: stake inversely proportional to the ratio, clamped so no single day dominates
			Func<double, double> cont = v => Risk * Math.Min(2.0, Math.Max(0.5, 0.90 / Math.Max(0.35, v)));
			Func<double, double> contInv = v => Risk * Math.Min(2.0, Math.Max(0.5, Math.Max(0.35, v) / 0.90));
			double cbar = sz.Average(x => cont(UwRatio[x.Sig]));
			double cibar = sz.Average(x => contInv(UwRatio[x.Sig]));

			Scheme("flat 10% [SHIPPED]", _ => 0.10);
			Console.WriteLine($"  -- tercile tiers 15/10/5, matched control {100 * tbar:0.00}% --");
			Scheme("  flat, matched [CONTROL]", _ => tbar);
			Scheme("  TIERED 15/10/5 by ratio", tier, _ => tbar);
			Scheme("  INVERTED 5/10/15 (sign control)", tierInv, _ => tbar);
			Console.WriteLine($"  -- continuous 0.90/ratio, matched control {100 * cbar:0.00}% --");
			Scheme("  flat, matched [CONTROL]", _ => cbar);
			Scheme("  CONTINUOUS 0.90/ratio", cont, _ => cbar);
			Scheme("  flat, matched to inverse", _ => cibar);
			Scheme("  INVERTED ratio/0.90 (sign control)", contInv, _ => cibar);
			Console.WriteLine("  paired t is against that block's matched-risk control, i.e. cov(stake, outcome).");

			// A gate that keeps most days cannot do much by construction; report what each one actually removes.
			Console.WriteLine($"\nkept-fraction check -- SM keeps {100.0 * all.Count(SmPrev) / all.Count:0.0}%, " +
				$"UW keeps {100.0 * all.Count(UwPrev) / all.Count:0.0}% of the same trades");
			Console.WriteLine("A gate is only as informative as it is selective; SqueezeMetrics' is nearly always-on.");
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
	}
}
