using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// BEAR CALL SPREADS ON THE SESSIONS THE PUT SPREAD REJECTS.
	//
	// Bearish structures were closed after 9 daily combos, 26 intraday buckets, 23 strike pairs and 9
	// call deltas all failed, with the standing rule "do not re-open without a NEW feature". The 5m
	// exposure signal is a new feature, so this is a legitimate re-open. The proposed rule:
	//
	//     same-day expiry exists
	//     AND the daily candle does NOT qualify for a put spread (exposure < 0.10, or ST Bear)
	//     AND call/put gamma ratio < 1.00      (negative net gamma)
	//     AND 5m exposure > 0.50               (fast tape maxed long -> fade it)
	//
	// THE SAMPLE IS THE WHOLE PROBLEM and is reported as a FUNNEL before any performance number. 5m
	// exposure exceeded 0.50 on roughly 5% of sessions and 5m history is ~60 days, so the intersection
	// may be empty. A performance table on 1-3 trades would be noise dressed as a finding, so anything
	// under MinN is refused rather than printed.
	//
	// The same rule MINUS the 5m gate runs on the full 2022-03+ gamma history, where there is enough
	// data to conclude something. That arm is the real test; the 5m arm is a feasibility check.
	//
	// NOTE ON THE PREMIUM SUBSIDY: IV is set to HV x 1.10 here, the same assumption that flatters every
	// short-premium structure. A bear call spread is SHORT premium too, so it collects that subsidy as
	// well. If it still loses at 1.10 the verdict is strong, because the thumb is on its side.
	// ================================================================================================
	internal static class BearCallRevisit
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double WingDelta = 0.15;     // long call, further OTM
		public static double NetDelta = 0.20;      // short call sits at Wing + Net
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static double RatioGate = 1.00;
		public static double FiveMinGate = 0.50;
		public static int MinN = 15;
		public static DateTime From = new DateTime(2022, 3, 30);
		public static (string sym, string dat)[] Symbols =
			{ ("SPY", "spx"), ("QQQ", "qqq"), ("IWM", "iwm"), ("GLD", "gld") };

		private sealed record Tr(string Sym, DateTime D, double R, double RPut, double Cp, double Exp5, bool StBear, double DailyExp);

		private static double NormCdf(double x)
		{
			double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
			double p = 1.0 - 0.3989422804014327 * Math.Exp(-x * x / 2.0) *
				(0.319381530 * t - 0.356563782 * t * t + 1.781477937 * t * t * t
				 - 1.821255978 * t * t * t * t + 1.330274429 * t * t * t * t * t);
			return x >= 0 ? p : 1.0 - p;
		}

		private static double Call(double s, double k, double v, double t)
		{
			if (t <= 0 || v <= 0) return Math.Max(0, s - k);
			double d1 = (Math.Log(s / k) + 0.5 * v * v * t) / (v * Math.Sqrt(t));
			return s * NormCdf(d1) - k * NormCdf(d1 - v * Math.Sqrt(t));
		}

		// Strike whose CALL delta equals the target. Call delta = N(d1), falling as the strike rises.
		private static double StrikeForCallDelta(double s, double v, double t, double delta)
		{
			double lo = s * 0.5, hi = s * 2.0;
			for (int i = 0; i < 80; i++)
			{
				double mid = 0.5 * (lo + hi);
				double d1 = (Math.Log(s / mid) + 0.5 * v * v * t) / (v * Math.Sqrt(t));
				if (NormCdf(d1) > delta) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
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

		private static Dictionary<DateTime, double> LoadCallPut(string dataSym)
		{
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
					&& double.TryParse(p[ci], NumberStyles.Any, CultureInfo.InvariantCulture, out double c)
					&& double.TryParse(p[pi], NumberStyles.Any, CultureInfo.InvariantCulture, out double pg)
					&& Math.Abs(pg) > 1e-9)
					map[d.Date] = c / Math.Abs(pg);
			}
			return map;
		}

		private sealed record Funnel(int Expiry, int NotQualify, int PlusRatio, int PlusFiveMin, int FiveMinCovered);

		public static async Task Run()
		{
			var all = new List<Tr>();
			var funnels = new Dictionary<string, Funnel>();

			foreach (var (symbol, dataSym) in Symbols)
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
				var cp = LoadCallPut(dataSym);
				if (cp.Count == 0) { Console.WriteLine($"  {symbol}: no gamma data"); continue; }

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
				var exp5 = new Dictionary<DateTime, double>();
				try
				{
					var intra = await IntradayClient.GetAsync(symbol, "5m", "60d");
					if (intra.Count >= 100)
					{
						var iEng = BankrollSimulator.Run(intra, 10_000.0);
						for (int k = 0; k < iEng.Positions.Count && k < iEng.ReturnDates.Count; k++)
							exp5[iEng.ReturnDates[k].Date] = iEng.Positions[k];
					}
				}
				catch (Exception ex) { Console.WriteLine($"  {symbol}: 5m unavailable ({ex.Message})"); }

				int nExp = 0, nNot = 0, nRatio = 0, nFive = 0, nCovered = 0;
				double T = 1.0 / 252.0;
				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (dTr < From) continue;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					nExp++;
					// does the daily candle qualify for a PUT spread? if it does, this session is not ours
					bool stBear = stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear;
					double dExp = pos.TryGetValue(dSig, out double tg) ? tg : double.NaN;
					if (double.IsNaN(dExp)) continue;
					bool qualifies = dExp >= TargetLo && !stBear;
					if (qualifies) continue;
					nNot++;
					if (!cp.TryGetValue(dSig, out double c)) continue;
					if (c >= RatioGate) continue;
					nRatio++;
					bool have5 = exp5.TryGetValue(dSig, out double e5);
					if (have5) nCovered++;
					if (have5 && e5 > FiveMinGate) nFive++;

					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;
					// BEAR CALL SPREAD: short the nearer call, long the further-OTM one. Credit, defined risk.
					double kS = StrikeForCallDelta(S, iv, T, WingDelta + NetDelta);
					double kL = StrikeForCallDelta(S, iv, T, WingDelta);
					double cr = Call(S, kS, iv, T) - Call(S, kL, iv, T);
					double ml = (kL - kS) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, ST - kS) + Math.Max(0, ST - kL);
					// DECISIVE CONTROL: the PUT spread on this same rejected session. If it earns as much or
					// more, there is no bearish edge here -- only short premium, which either structure
					// collects. A bearish claim requires the call spread to BEAT the put spread.
					double pS = StrikeForPutDelta(S, iv, T, WingDelta + NetDelta);
					double pL = StrikeForPutDelta(S, iv, T, WingDelta);
					double pcr = Put(S, pS, iv, T) - Put(S, pL, iv, T);
					double pml = (pS - pL) - pcr;
					double rput = pml > 1e-9 ? (pcr - Math.Max(0, pS - ST) + Math.Max(0, pL - ST)) / pml : double.NaN;
					all.Add(new Tr(symbol, dTr, (cr + po) / ml, rput, c, have5 ? e5 : double.NaN, stBear, dExp));
				}
				funnels[symbol] = new Funnel(nExp, nNot, nRatio, nFive, nCovered);
			}

			Console.WriteLine($"\n===== BEAR CALL SPREAD ON PUT-SPREAD REJECTS ({From:yyyy-MM}+) =====");
			Console.WriteLine($"structure: SHORT {WingDelta + NetDelta:0.00}d call / LONG {WingDelta:0.00}d call, " +
				$"risk {100 * Risk:0.#}% per session, IV = HV x {VolRiskPremium:0.00}");
			Console.WriteLine($"\n-- FUNNEL: how many sessions survive each condition --");
			Console.WriteLine($"{"sym",6} {"has expiry",11} {"+ not qualify",14} {"+ callPut<1",12} " +
				$"{"5m covered",11} {"+ 5m exp>0.5",13}");
			foreach (var kv in funnels)
				Console.WriteLine($"{kv.Key,6} {kv.Value.Expiry,11} {kv.Value.NotQualify,14} {kv.Value.PlusRatio,12} " +
					$"{kv.Value.FiveMinCovered,11} {kv.Value.PlusFiveMin,13}");
			Console.WriteLine($"{"TOTAL",6} {funnels.Values.Sum(f => f.Expiry),11} {funnels.Values.Sum(f => f.NotQualify),14} " +
				$"{funnels.Values.Sum(f => f.PlusRatio),12} {funnels.Values.Sum(f => f.FiveMinCovered),11} " +
				$"{funnels.Values.Sum(f => f.PlusFiveMin),13}");

			void Table(string lbl, List<Tr> t)
			{
				if (t.Count < MinN) { Console.WriteLine($"{lbl,-40} {t.Count,5}   REFUSED (n < {MinN})"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double se = sd / Math.Sqrt(r.Count);
				double e = 1, pk = 1, dd = 0;
				foreach (var z in r) { e *= 1 + z; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				Console.WriteLine($"{lbl,-40} {t.Count,5} {100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 1e-12 ? m / sd : 0),8:0.000} {(se > 0 ? m / se : 0),7:+0.00;-0.00} {dd,8:0.00}");
			}

			Console.WriteLine($"\n{"arm",-40} {"n",5} {"mean%",10} {"win%",7} {"IR",8} {"t",7} {"maxDD%",8}");
			Console.WriteLine("-- THE REQUESTED RULE (needs 5m, so limited to the ~60d window) --");
			Table("  5m-covered sessions, no 5m gate", all.Where(x => !double.IsNaN(x.Exp5)).ToList());
			// Sweep the 5m threshold rather than testing one value: 0.50 collapsed the sample to 4 sessions,
			// so the question is whether a LOOSER cut keeps enough sessions to say anything at all. Both the
			// gate and its complement are shown -- if exposure carries direction here, they must differ.
			foreach (double g in new[] { 0.10, 0.20, 0.30, 0.50 })
			{
				var above = all.Where(x => !double.IsNaN(x.Exp5) && x.Exp5 > g).ToList();
				var below = all.Where(x => !double.IsNaN(x.Exp5) && x.Exp5 <= g).ToList();
				Table($"  5m exp >  {g:0.00}", above);
				Table($"  5m exp <= {g:0.00} (complement)", below);
			}

			Console.WriteLine("\n-- THE SAME RULE WITHOUT THE 5m GATE, on the full gamma history --");
			Table("not qualify & callPut < 1.00", all);
			Table("  ... & reason = ST Bear", all.Where(x => x.StBear).ToList());
			Table("  ... & reason = exposure < 0.10", all.Where(x => !x.StBear).ToList());
			Console.WriteLine("-- sign / dose controls --");
			Table("  ... & callPut < 0.75 (tighter)", all.Where(x => x.Cp < 0.75).ToList());
			Table("  ... & callPut 0.75-1.00", all.Where(x => x.Cp >= 0.75).ToList());
			foreach (var kv in all.GroupBy(x => x.Sym).OrderBy(g => g.Key))
				Table($"  per-instrument: {kv.Key}", kv.ToList());

			// ---- CONTROL 1: is this a BEARISH edge, or just short premium? -----------------------------
			var paired = all.Where(x => !double.IsNaN(x.RPut)).ToList();
			Console.WriteLine($"{Environment.NewLine}-- CONTROL: the PUT spread on these SAME {paired.Count} rejected sessions --");
			Console.WriteLine($"{"arm",-40} {"n",5} {"mean%",10} {"win%",7} {"IR",8} {"t",7} {"maxDD%",8}");
			Table("  BEAR CALL spread (the candidate)", paired);
			var flip = paired.Select(x => x with { R = x.RPut }).ToList();
			Table("  PUT spread, same sessions", flip);
			void Pair(string lbl, List<Tr> t)
			{
				if (t.Count < MinN) { Console.WriteLine($"  {lbl,-42} n={t.Count,4}   REFUSED (n < {MinN})"); return; }
				var d = t.Select(x => Risk * (x.R - x.RPut)).ToList();
				double m = d.Average();
				double sd = Math.Sqrt(d.Sum(z => (z - m) * (z - m)) / (d.Count - 1));
				Console.WriteLine($"  {lbl,-42} n={t.Count,4}  diff {100 * m,9:+0.0000;-0.0000}pp  " +
					$"t {m / (sd / Math.Sqrt(d.Count)),6:+0.00;-0.00}  call wins {100.0 * d.Count(z => z > 0) / d.Count,5:0.0}%");
			}
			Console.WriteLine("  a bearish edge requires the difference to be POSITIVE; negative means the tape still rose");
			Pair("all rejected sessions", paired);
			Pair("5m-covered subset", paired.Where(x => !double.IsNaN(x.Exp5)).ToList());
			foreach (double g in new[] { 0.10, 0.20 })
				Pair($"5m-covered & exp > {g:0.00}", paired.Where(x => !double.IsNaN(x.Exp5) && x.Exp5 > g).ToList());

			// ---- CONTROL 2: why the paired test is the subsidy-free one --------------------------------
			// Both legs of the comparison are SHORT premium at the same assumed IV = HV x 1.10, so the
			// inflated premium lands on both sides and largely cancels in the DIFFERENCE. That makes the
			// paired call-minus-put statistic the one number here that is not an artifact of the premium
			// assumption -- unlike either structure's standalone mean, which is mostly subsidy.
			Console.WriteLine($"{Environment.NewLine}-- note: both arms are short premium at IV = HV x {VolRiskPremium:0.00}, " +
				"so the subsidy cancels in the paired difference above --");
			Console.WriteLine("   each STANDALONE mean is inflated by that assumption (measured 1-day VRP is ~0.99);");
			Console.WriteLine("   the paired difference is the subsidy-free read, so judge the verdict on it.");
		}
	}
}
