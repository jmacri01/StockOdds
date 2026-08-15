using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// ALWAYS-PAIRED INSTRUMENTS: run the 0DTE put credit spread on SPX and GLD on the SAME sessions.
	//
	// The question is whether two legs with LOW correlation beat either alone. Two traps to avoid:
	//
	//  1. STAKE INFLATION. Taking 10% on each leg stakes 20% of the bankroll and beats a single 10%
	//     trade for reasons that have nothing to do with diversification. Every arm here is reported
	//     at a MATCHED TOTAL STAKE (10%): solo legs risk 10%, the pair risks 5% + 5%. A 2x-stake row
	//     is printed separately and labelled, never mixed into the comparison.
	//
	//  2. SESSION-SET DRIFT. "Always taken together" means the INTERSECTION -- sessions where both
	//     names have a real same-day expiry AND both pass the shipped filters. GLD has no daily
	//     expiries (Mon/Wed/Fri only), so this is far smaller than either name's solo set. Comparing
	//     a pair on the intersection against a solo leg on its FULL set would confound the pairing
	//     with the session filter, so the solo arms are re-scored on the intersection too.
	// ================================================================================================
	internal static class PairedInstruments
	{
		public static double BaseRisk = 0.10;      // total stake per session, split across legs
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static DateTime From = new DateTime(2022, 3, 30);

		private sealed record Tr(DateTime D, double R, double Cp);

		private static Dictionary<DateTime, double> LoadCallPut(string dataSym)
		{
			// Resolve columns BY HEADER NAME -- the per-symbol CSVs do not share a column order (spx carries
			// delta/charm/vanna fields that gld does not), so fixed indices silently read the wrong field.
			var map = new Dictionary<DateTime, double>();
			string path = Path.Combine(Path.GetFullPath(Universe.DataDir), $"gex_uw_{dataSym}.csv");
			if (!File.Exists(path)) return map;
			var lines = File.ReadAllLines(path);
			var h = lines[0].Split(',');
			int di = Array.IndexOf(h, "date"), ci = Array.IndexOf(h, "call_gex"), pi = Array.IndexOf(h, "put_gex");
			if (di < 0 || ci < 0 || pi < 0) return map;
			for (int i = 1; i < lines.Length; i++)
			{
				var p = lines[i].Split(',');
				if (p.Length <= Math.Max(ci, pi)) continue;
				if (DateTime.TryParse(p[di], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
					&& double.TryParse(p[ci], NumberStyles.Any, CultureInfo.InvariantCulture, out double call)
					&& double.TryParse(p[pi], NumberStyles.Any, CultureInfo.InvariantCulture, out double put)
					&& Math.Abs(put) > 1e-9)
					map[d.Date] = call / Math.Abs(put);
			}
			return map;
		}

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

		// Build the shipped 0DTE trade list for one instrument. R is the return per unit of risk
		// staked (a full loss = -1.0), so a leg's contribution is simply risk_leg * R.
		private static async Task<List<Tr>> Build(string symbol, string dataSym)
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(bars, 10_000.0);
			var cp = LoadCallPut(dataSym);
			var outp = new List<Tr>();
			if (cp.Count == 0) return outp;

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
			for (int i = 1; i + 1 < bars.Count; i++)
			{
				var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
				if (dTr < From) continue;
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
				if (!cp.TryGetValue(dSig, out double c)) continue;
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				double iv = h * VolRiskPremium;
				double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) continue;
				double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
				outp.Add(new Tr(dTr, (cr + po) / ml, c));
			}
			return outp;
		}

		public static async Task Run()
		{
			var spy = await Build("SPY", "spx");
			var gld = await Build("GLD", "gld");
			if (spy.Count == 0 || gld.Count == 0) { Console.WriteLine("missing data"); return; }

			var sMap = spy.ToDictionary(x => x.D);
			var gMap = gld.ToDictionary(x => x.D);
			var both = sMap.Keys.Intersect(gMap.Keys).OrderBy(d => d).ToList();

			Console.WriteLine($"\n===== ALWAYS-PAIRED SPX + GLD ({From:yyyy-MM}+) =====");
			Console.WriteLine($"SPY solo eligible: {spy.Count}   GLD solo eligible: {gld.Count}   BOTH: {both.Count}");
			Console.WriteLine($"pairing costs SPY {100.0 * (spy.Count - both.Count) / spy.Count:0.0}% of its sessions, " +
				$"GLD {100.0 * (gld.Count - both.Count) / gld.Count:0.0}%");
			if (both.Count < 40) { Console.WriteLine("too few paired sessions"); return; }

			var rs = both.Select(d => sMap[d].R).ToList();
			var rg = both.Select(d => gMap[d].R).ToList();
			double ms = rs.Average(), mg = rg.Average();
			double cov = both.Select((d, i) => (rs[i] - ms) * (rg[i] - mg)).Sum() / (both.Count - 1);
			double sds = Math.Sqrt(rs.Sum(x => (x - ms) * (x - ms)) / (both.Count - 1));
			double sdg = Math.Sqrt(rg.Sum(x => (x - mg) * (x - mg)) / (both.Count - 1));
			double corr = (sds > 0 && sdg > 0) ? cov / (sds * sdg) : 0;
			int bothWin = both.Count(d => sMap[d].R > 0 && gMap[d].R > 0);
			int bothLose = both.Count(d => sMap[d].R <= 0 && gMap[d].R <= 0);
			Console.WriteLine($"leg correlation on paired sessions: {corr:+0.000;-0.000}   " +
				$"both win {100.0 * bothWin / both.Count:0.0}%, both lose {100.0 * bothLose / both.Count:0.0}%");

			double years = (both[^1] - both[0]).TotalDays / 365.25;
			// Every arm stakes the SAME total fraction of bankroll per session, so IR, drawdown and CAGR
			// are all directly comparable and none of the difference is leverage.
			void Show(string lbl, Func<DateTime, double> pnl)
			{
				var r = both.Select(pnl).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double cagr = (e > 0 && years > 0) ? (Math.Pow(e, 1.0 / years) - 1) * 100 : -100;
				Console.WriteLine($"{lbl,-34} {100 * m,9:+0.0000;-0.0000} {100.0 * r.Count(x => x > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {cagr,10:0.0}");
			}

			Console.WriteLine($"\n-- matched TOTAL stake of {100 * BaseRisk:0.#}% per session, all on the {both.Count} paired sessions --");
			Console.WriteLine($"{"arm",-34} {"mean/sess%",9} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10}");
			Show("SPX only (10%)", d => BaseRisk * sMap[d].R);
			Show("GLD only (10%)", d => BaseRisk * gMap[d].R);
			Show("PAIR 50/50 (5% + 5%)", d => 0.5 * BaseRisk * sMap[d].R + 0.5 * BaseRisk * gMap[d].R);
			Show("PAIR 70/30 SPX-heavy", d => 0.7 * BaseRisk * sMap[d].R + 0.3 * BaseRisk * gMap[d].R);
			Show("PAIR 30/70 GLD-heavy", d => 0.3 * BaseRisk * sMap[d].R + 0.7 * BaseRisk * gMap[d].R);
			// The shipped ratio gate applies to the SPX leg only -- it is inert-to-inverted on GLD, so the
			// GLD leg is always taken. On gated-out sessions the SPX stake goes to zero rather than being
			// reallocated, which is what the shipped rule would actually do.
			Show("PAIR 50/50, SPX leg gated", d => (sMap[d].Cp >= 1.0 ? 0.5 * BaseRisk * sMap[d].R : 0) + 0.5 * BaseRisk * gMap[d].R);

			Console.WriteLine($"\n-- reference: both legs at FULL {100 * BaseRisk:0.#}% = {200 * BaseRisk:0.#}% total stake (NOT matched) --");
			Show("PAIR 10% + 10%", d => BaseRisk * sMap[d].R + BaseRisk * gMap[d].R);

			// Solo arms on their OWN full session sets, to show what the intersection itself costs.
			Console.WriteLine("\n-- what the intersection costs: each leg on its own FULL set --");
			void ShowFull(string lbl, List<Tr> t)
			{
				var r = t.Select(x => BaseRisk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yy = (t[^1].D - t[0].D).TotalDays / 365.25;
				double cagr = (e > 0 && yy > 0) ? (Math.Pow(e, 1.0 / yy) - 1) * 100 : -100;
				Console.WriteLine($"{lbl,-34} {100 * m,9:+0.0000;-0.0000} {100.0 * r.Count(x => x > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {cagr,10:0.0}");
			}
			ShowFull($"SPX full set (n={spy.Count})", spy.OrderBy(x => x.D).ToList());
			ShowFull($"GLD full set (n={gld.Count})", gld.OrderBy(x => x.D).ToList());

			// ---- IS THE "ALWAYS TOGETHER" CONSTRAINT WORTH ITS COST? -----------------------------------
			// Forcing simultaneity throws away every session where only one name has an expiry, and GLD has
			// no daily expiries at all. The alternative is to run both legs on their OWN schedules -- still
			// two separate trades, just not synchronised. Scored on the UNION at a matched AVERAGE stake, so
			// the comparison is not just "the union trades more often".
			var union = sMap.Keys.Union(gMap.Keys).OrderBy(d => d).ToList();
			double wLeg = 0.5 * BaseRisk;
			var uRaw = union.Select(d => (sMap.ContainsKey(d) ? wLeg * sMap[d].R : 0) + (gMap.ContainsKey(d) ? wLeg * gMap[d].R : 0)).ToList();
			double uStake = union.Average(d => (sMap.ContainsKey(d) ? wLeg : 0) + (gMap.ContainsKey(d) ? wLeg : 0));
			double kScale = BaseRisk / uStake;                 // analytic haircut, never bisect
			double uYears = (union[^1] - union[0]).TotalDays / 365.25;
			void ShowSeries(string lbl, List<double> r, double yy)
			{
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double cagr = (e > 0 && yy > 0) ? (Math.Pow(e, 1.0 / yy) - 1) * 100 : -100;
				Console.WriteLine($"{lbl,-34} {100 * m,9:+0.0000;-0.0000} {100.0 * r.Count(x => x > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {cagr,10:0.0}");
			}
			Console.WriteLine($"\n-- UNSYNCHRONISED: each leg on its own schedule, {union.Count} union sessions, " +
				$"scaled x{kScale:0.00} to a matched {100 * BaseRisk:0.#}% average stake --");
			Console.WriteLine($"{"arm",-34} {"mean/sess%",9} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10}");
			ShowSeries("UNION both legs, own schedules", uRaw.Select(x => kScale * x).ToList(), uYears);
			ShowSeries("  same, unscaled (5% per leg)", uRaw, uYears);

			// Paired t on the SAME sessions: does adding the GLD leg beat spending the whole stake on SPX?
			var diff = both.Select(d => (0.5 * BaseRisk * sMap[d].R + 0.5 * BaseRisk * gMap[d].R) - BaseRisk * sMap[d].R).ToList();
			double md = diff.Average();
			double sdd = Math.Sqrt(diff.Sum(x => (x - md) * (x - md)) / (diff.Count - 1));
			Console.WriteLine($"\npaired t, PAIR 50/50 minus SPX-only at the same total stake: " +
				$"{md / (sdd / Math.Sqrt(diff.Count)),0:+0.00;-0.00}  (mean diff {100 * md:+0.0000;-0.0000}%/session)");
			var diffG = both.Select(d => (0.5 * BaseRisk * sMap[d].R + 0.5 * BaseRisk * gMap[d].R) - BaseRisk * gMap[d].R).ToList();
			double mdg = diffG.Average();
			double sddg = Math.Sqrt(diffG.Sum(x => (x - mdg) * (x - mdg)) / (diffG.Count - 1));
			Console.WriteLine($"paired t, PAIR 50/50 minus GLD-only at the same total stake: " +
				$"{mdg / (sddg / Math.Sqrt(diffG.Count)),0:+0.00;-0.00}  (mean diff {100 * mdg:+0.0000;-0.0000}%/session)");

			// ---- ANNUALISED SHARPE: the only metric comparable ACROSS different trade frequencies -------
			// IR-per-trade is invariant to how often you trade, so it flatters the synchronised arm (250
			// trades) against the unsynchronised one (770). Annualising with sqrt(trades per year) is what
			// actually decides which portfolio a year of capital prefers.
			Console.WriteLine($"\n-- annualised Sharpe = IR x sqrt(trades/yr); the frequency-fair comparison --");
			Console.WriteLine($"{"arm",-34} {"n",6} {"tr/yr",8} {"IR/trade",10} {"annSharpe",11}");
			void Ann(string lbl, List<double> r, double yy)
			{
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
				double ir = sd > 0 ? m / sd : 0, perYr = r.Count / yy;
				Console.WriteLine($"{lbl,-34} {r.Count,6} {perYr,8:0.0} {ir,10:0.000} {ir * Math.Sqrt(perYr),11:0.00}");
			}
			double spyYrs = (spy.Max(x => x.D) - spy.Min(x => x.D)).TotalDays / 365.25;
			double gldYrs = (gld.Max(x => x.D) - gld.Min(x => x.D)).TotalDays / 365.25;
			Ann("SPX alone, own full set", spy.Select(x => BaseRisk * x.R).ToList(), spyYrs);
			Ann("GLD alone, own full set", gld.Select(x => BaseRisk * x.R).ToList(), gldYrs);
			Ann("PAIR 50/50, synchronised", both.Select(d => 0.5 * BaseRisk * (sMap[d].R + gMap[d].R)).ToList(), years);
			Ann("PAIR 50/50, SPX leg gated", both.Select(d => (sMap[d].Cp >= 1.0 ? 0.5 * BaseRisk * sMap[d].R : 0) + 0.5 * BaseRisk * gMap[d].R).ToList(), years);
			Ann("UNION, own schedules", uRaw.Select(x => kScale * x).ToList(), uYears);

			// ---- VRP SENSITIVITY: the GLD leg's edge rests on an ASSUMED premium ------------------------
			// SPX's 1-day premium was MEASURED at ~0.99 via ^VIX1D. Gold's has never been measured here, yet
			// both legs are priced at 1.10 above. Since the whole options ranking is a monotone function of
			// VolRiskPremium and inverts below ~0.93, the GLD leg has to be shown across the range before any
			// of its advantage is believed.
			Console.WriteLine("\n-- VRP sensitivity, each leg SOLO on its own full set (premium is ASSUMED, not measured, for GLD) --");
			Console.WriteLine($"{"VRP",8} {"SPX IR",10} {"SPX mean%",11} {"GLD IR",10} {"GLD mean%",11}");
			double vrpSave = VolRiskPremium;
			foreach (double v in new[] { 0.93, 0.99, 1.05, 1.10, 1.15 })
			{
				VolRiskPremium = v;
				var s2 = await Build("SPY", "spx");
				var g2 = await Build("GLD", "gld");
				(double ir, double mean) Stat(List<Tr> t)
				{
					if (t.Count < 20) return (0, 0);
					var r = t.Select(x => BaseRisk * x.R).ToList();
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
					return (sd > 0 ? m / sd : 0, 100 * m);
				}
				var a = Stat(s2); var b = Stat(g2);
				Console.WriteLine($"{v,8:0.00} {a.ir,10:0.000} {a.mean,11:+0.0000;-0.0000} {b.ir,10:0.000} {b.mean,11:+0.0000;-0.0000}");
			}
			VolRiskPremium = vrpSave;
		}
	}
}
