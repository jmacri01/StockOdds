using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Size the 0-DTE spread on the CALL/PUT gamma ratio, keeping every shipped filter and trading every session.
	//
	// THE RULE, in full:
	//
	//     cp_i        = call_gex / |put_gex|   on the PRIOR day   (higher = call gamma dominates = better)
	//     multiplier  = clamp(Slope * cp_i, MinMult, MaxMult)
	//     risk_i      = BaseRisk * multiplier                     ( = fraction of bankroll lost on a full loss)
	//
	// With the shipped defaults (Base 10%, Slope 0.90, clamp [0.5, 2.0]) that maps:
	//     cp <= 0.56  ->  5.0% risk   (floor: puts dominate, stake half)
	//     cp  = 1.11  -> 10.0% risk   (neutral point, the shipped stake)
	//     cp >= 2.22  -> 20.0% risk   (cap: calls dominate, stake double)
	// and it is linear in between. The clamp is what stops one extreme day from dominating the book -- without it
	// a single lopsided session would size arbitrarily large.
	//
	// This is a REPARAMETERISATION of the put/call version, not a new rule: clamp(0.90/putCall) is identical to
	// clamp(0.90*callPut) since the two ratios are reciprocals. The numbers below should reproduce exactly, which
	// is the check that the flip was done correctly.
	//
	// THE CONTROL, unchanged and non-negotiable: any scheme that stakes more in total beats flat sizing for
	// trivial reasons, so each is scored against a FLAT rule at its own AVERAGE risk. The paired difference is
	// cov(stake, outcome). An inverted scheme is carried as a sign control and must lose by a comparable margin.
	public static class RatioSizing
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double BaseRisk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;
		public static DateTime From = new DateTime(2022, 3, 30);
		public static double Slope = 0.90, MinMult = 0.5, MaxMult = 2.0;
		// Which UW gamma file to read. The ratio's LEVEL is instrument-specific -- median call/put is 1.19 on SPY,
		// 1.70 on GLD and 0.60 on IWM -- so a rule with ABSOLUTE thresholds cannot transfer. Both the shipped
		// absolute form and a percentile-normalised form are therefore scored.
		public static string DataSym = "spx";

		private sealed record Tr(DateTime D, double R, double Cp);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(bars, 10_000.0);
			var cp = LoadCallPut();
			if (cp.Count == 0) { Console.WriteLine("no UW gamma data"); return; }

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
			var tr = new List<Tr>();
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
				tr.Add(new Tr(dTr, (cr + po) / ml, c));
			}

			double Mult(double c) => Math.Min(MaxMult, Math.Max(MinMult, Slope * c));
			var cps = tr.Select(x => x.Cp).OrderBy(v => v).ToList();
			Console.WriteLine($"\n===== {symbol}: SIZE ON THE CALL/PUT GAMMA RATIO ({From:yyyy-MM}+) =====");
			Console.WriteLine($"{tr.Count} sessions, shipped filters on, every session traded (no gate)");
			Console.WriteLine($"rule:  risk = {100 * BaseRisk:0.#}% x clamp({Slope:0.00} x callPut, {MinMult:0.0}, {MaxMult:0.0})");
			Console.WriteLine($"call/put distribution: p10 {cps[(int)(cps.Count * 0.10)]:0.00}  p25 {cps[(int)(cps.Count * 0.25)]:0.00}  " +
				$"median {cps[cps.Count / 2]:0.00}  p75 {cps[(int)(cps.Count * 0.75)]:0.00}  p90 {cps[(int)(cps.Count * 0.90)]:0.00}");
			Console.WriteLine($"\nthe mapping, and how often each region occurs:");
			Console.WriteLine($"{"call/put",12} {"multiplier",12} {"risk%",8} {"% of days at/below",20}");
			foreach (double c in new[] { 0.40, 0.56, 0.80, 1.00, 1.11, 1.40, 1.80, 2.22, 3.00 })
				Console.WriteLine($"{c,12:0.00} {Mult(c),12:0.00} {100 * BaseRisk * Mult(c),8:0.0} " +
					$"{100.0 * cps.Count(v => v <= c) / cps.Count,20:0.0}");
			Console.WriteLine($"   clamped LOW on {100.0 * tr.Count(x => Slope * x.Cp < MinMult) / tr.Count:0.0}% of days, " +
				$"HIGH on {100.0 * tr.Count(x => Slope * x.Cp > MaxMult) / tr.Count:0.0}%");

			Console.WriteLine($"\n{"scheme",34} {"avgRisk%",9} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} " +
				$"{"CAGR%",10} {"paired t",9}");
			void Show(string lbl, Func<double, double> riskOf, Func<double, double>? baseline = null)
			{
				var r = tr.Select(x => riskOf(x.Cp) * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (tr.Last().D - tr.First().D).TotalDays / 365.25);
				string ts = "";
				if (baseline != null)
				{
					var d = tr.Select(x => (riskOf(x.Cp) - baseline(x.Cp)) * x.R).ToList();
					double dm = d.Average();
					double dsd = Math.Sqrt(d.Sum(z => (z - dm) * (z - dm)) / (d.Count - 1));
					ts = dsd > 0 ? $"{dm / (dsd / Math.Sqrt(d.Count)):+0.00;-0.00}" : "";
				}
				Console.WriteLine($"{lbl,34} {100 * tr.Average(x => riskOf(x.Cp)),9:0.00} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {ts,9}");
			}
			double bar = tr.Average(x => BaseRisk * Mult(x.Cp));
			double ibar = tr.Average(x => BaseRisk * Math.Min(MaxMult, Math.Max(MinMult, 1.0 / Math.Max(0.05, Slope * x.Cp))));
			Show("flat 10% [SHIPPED]", _ => BaseRisk);
			Show("flat, matched to the scheme [CONTROL]", _ => bar);
			Show("CONTINUOUS on call/put", c => BaseRisk * Mult(c), _ => bar);
			Show("flat, matched to the inverse", _ => ibar);
			Show("INVERTED (sign control)",
				c => BaseRisk * Math.Min(MaxMult, Math.Max(MinMult, 1.0 / Math.Max(0.05, Slope * c))), _ => ibar);

			// ---- SCALE THE STRIKE INSTEAD OF THE STAKE ---------------------------------------------------
			// Sizing on the ratio works. Does moving the SHORT PUT DELTA with it work too? Bucketed tests on the
			// SqueezeMetrics scalar said no, and said so BACKWARDS: the highest-gamma bucket preferred the LOWEST
			// delta (0.10), stable across both halves. This runs it continuously on the call/put ratio.
			//
			// Unlike sizing, changing delta changes the TRADE rather than its scale, so every rule needs its own
			// re-priced trade set and the control is a flat delta matched on the AVERAGE delta carried.
			Console.WriteLine("");
			Console.WriteLine("--- SCALING THE SHORT PUT DELTA WITH call/put (instead of the stake) ---");
			double[] dGrid = { 0.10, 0.125, 0.15, 0.175, 0.20, 0.225, 0.25, 0.30, 0.35, 0.40 };
			var byDelta = new Dictionary<double, Dictionary<DateTime, double>>();
			foreach (double nd in dGrid)
			{
				var map = new Dictionary<DateTime, double>();
				for (int i = 1; i + 1 < bars.Count; i++)
				{
					var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
					if (dTr < From) continue;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st2) && st2 == ShortTermState.Bear) continue;
					if (!cp.ContainsKey(dSig)) continue;
					double S = bars[i + 1].Open, ST = bars[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;
					double kS = StrikeForPutDelta(S, iv, T, nd + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr2 = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double ml2 = (kS - kL) - cr2;
					if (cr2 <= 1e-9 || ml2 <= 1e-9) continue;
					map[dTr] = (cr2 + (-Math.Max(0, kS - ST) + Math.Max(0, kL - ST))) / ml2;
				}
				byDelta[nd] = map;
			}
			double Snap(double d) => dGrid.OrderBy(g => Math.Abs(g - d)).First();
			Console.WriteLine($"{"delta rule",34} {"avgDelta",9} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} " +
				$"{"CAGR%",10} {"paired t",9}");
			void DShow(string lbl, Func<double, double> deltaOf, Func<double, double> baseline = null)
			{
				var rows = tr.Where(x => byDelta[Snap(deltaOf(x.Cp))].ContainsKey(x.D)).ToList();
				var r = rows.Select(x => BaseRisk * byDelta[Snap(deltaOf(x.Cp))][x.D]).ToList();
				if (r.Count < 50) { Console.WriteLine($"{lbl,34} (too few)"); return; }
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (rows.Last().D - rows.First().D).TotalDays / 365.25);
				string ts = "";
				if (baseline != null)
				{
					var d = rows.Where(x => byDelta[Snap(baseline(x.Cp))].ContainsKey(x.D))
						.Select(x => BaseRisk * (byDelta[Snap(deltaOf(x.Cp))][x.D] - byDelta[Snap(baseline(x.Cp))][x.D])).ToList();
					double dm = d.Average();
					double dsd = Math.Sqrt(d.Sum(z => (z - dm) * (z - dm)) / (d.Count - 1));
					ts = dsd > 0 ? $"{dm / (dsd / Math.Sqrt(d.Count)):+0.00;-0.00}" : "";
				}
				Console.WriteLine($"{lbl,34} {rows.Average(x => Snap(deltaOf(x.Cp))),9:0.000} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {ts,9}");
			}
			Func<double, double> upD = c => Math.Min(0.40, Math.Max(0.10, NetDelta * 0.90 * c));
			Func<double, double> dnD = c => Math.Min(0.40, Math.Max(0.10, NetDelta / Math.Max(0.30, 0.90 * c)));
			double upBar = Snap(tr.Average(x => Snap(upD(x.Cp))));
            double dnBar = Snap(tr.Average(x => Snap(dnD(x.Cp))));
			DShow("flat 0.20 [SHIPPED]", _ => 0.20);
			DShow($"flat {upBar:0.000} [control for UP]", _ => upBar);
			DShow("delta UP with call/put", upD, _ => upBar);
			DShow($"flat {dnBar:0.000} [control for DOWN]", _ => dnBar);
			DShow("delta DOWN with call/put", dnD, _ => dnBar);
			Console.WriteLine("  (deltas snapped to the priced grid; paired t is against the matched-average-delta flat rule)");

			// ---- THE PLAIN RULE: risk = BaseRisk * callPut ------------------------------------------------
			// Slope 1.0, no clamp -- the simplest possible version. Two things to watch: average risk drifts
			// ABOVE base because the median call/put sits above 1.0, and without a clamp one extreme session
			// sizes without limit. The realised min/max risk is printed before any performance number.
			Console.WriteLine("");
			Console.WriteLine("--- risk = 10% x callPut, with and without the clamp ---");
			var rawRisk = tr.Select(x => BaseRisk * x.Cp).OrderBy(v => v).ToList();
			Console.WriteLine($"  unclamped risk runs {100 * rawRisk.First():0.0}% to {100 * rawRisk.Last():0.0}% " +
				$"(p1 {100 * rawRisk[(int)(rawRisk.Count * 0.01)]:0.0}%, p99 {100 * rawRisk[(int)(rawRisk.Count * 0.99)]:0.0}%), " +
				$"mean {100 * rawRisk.Average():0.00}%");
			Func<double, double> plain = c => BaseRisk * c;
			Func<double, double> plainCl = c => BaseRisk * Math.Min(2.0, Math.Max(0.5, c));
			Func<double, double> plainInv = c => BaseRisk * Math.Max(0.05, 1.0 / Math.Max(0.05, c));
			double pBar = tr.Average(x => plain(x.Cp));
			double pcBar = tr.Average(x => plainCl(x.Cp));
			double piBar = tr.Average(x => plainInv(x.Cp));
			Show("flat 10% [SHIPPED]", _ => BaseRisk);
			Show($"flat {100 * pBar:0.00}% [control]", _ => pBar);
			Show("risk = 10% x callPut  (no clamp)", plain, _ => pBar);
			Show($"flat {100 * pcBar:0.00}% [control]", _ => pcBar);
			Show("risk = 10% x callPut  clamp[0.5,2]", plainCl, _ => pcBar);
			Show($"flat {100 * piBar:0.00}% [control]", _ => piBar);
			Show("INVERTED 10% / callPut (sign ctrl)", plainInv, _ => piBar);
			// ASYMMETRIC CLAMPS. A floor at 1.0 means never sizing BELOW base -- full stake on poor days, extra on
			// good ones. That deliberately throws away half the mechanism: the edge is cov(stake, outcome), and
			// refusing to under-stake the bad days removes the negative-covariance contribution. Worth measuring
			// rather than assuming, since "never risk less than normal" is a reasonable thing to want.
			Console.WriteLine("  asymmetric clamps (floor at 1.0 = never size below base):");
			foreach (var (lo, hi) in new[] { (1.0, 1.5), (1.0, 2.0), (1.0, 3.0), (0.75, 2.0), (0.5, 2.0) })
			{
				Func<double, double> f = c => BaseRisk * Math.Min(hi, Math.Max(lo, c));
				double bb = tr.Average(x => f(x.Cp));
				Show($"  flat {100 * bb:0.00}% [control]", _ => bb);
				Show($"  clamp[{lo:0.00},{hi:0.00}] x callPut", f, _ => bb);
			}
			// ---- THE COMPARISON THAT MATTERS: GATE FIRST, THEN SIZE WITHIN IT ----------------------------
			// Everything above trades EVERY session, so it is not the shipped config -- shipped gates on gamma.
			// Once sessions with callPut < 1 are excluded, the multiplier can never fall below 1, so the choice
			// of lower clamp is moot: the gate has already made the rule one-sided. The open question is whether
			// varying the stake INSIDE the surviving set still earns anything, or whether the gate has already
			// taken all the signal has to give.
			Console.WriteLine("");
			Console.WriteLine("--- GATE FIRST, THEN SIZE WITHIN (the actual shipped comparison) ---");
			foreach (double gate in new[] { 1.00, 1.10, 1.25 })
			{
				var g = tr.Where(x => x.Cp > gate).ToList();
				if (g.Count < 60) { Console.WriteLine($"  gate cp>{gate:0.00}: {g.Count} sessions (too few)"); continue; }
				Console.WriteLine($"  -- gate callPut > {gate:0.00}: {g.Count} of {tr.Count} sessions " +
					$"({100.0 * g.Count / tr.Count:0.0}%), callPut range {g.Min(x => x.Cp):0.00}-{g.Max(x => x.Cp):0.00} --");
				void GShow(string lbl, Func<double, double> riskOf, Func<double, double> baseline = null)
				{
					var r = g.Select(x => riskOf(x.Cp) * x.R).ToList();
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					double e = 1, pk = 1, dd = 0;
					foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
					double yrs = Math.Max(1.0, (g.Last().D - g.First().D).TotalDays / 365.25);
					string ts = "";
					if (baseline != null)
					{
						var d = g.Select(x => (riskOf(x.Cp) - baseline(x.Cp)) * x.R).ToList();
						double dm = d.Average();
						double dsd = Math.Sqrt(d.Sum(z => (z - dm) * (z - dm)) / (d.Count - 1));
						ts = dsd > 0 ? $"{dm / (dsd / Math.Sqrt(d.Count)):+0.00;-0.00}" : "";
					}
					Console.WriteLine($"{lbl,34} {100 * g.Average(x => riskOf(x.Cp)),9:0.00} {100 * m,10:+0.0000;-0.0000} " +
						$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
						$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {ts,9}");
				}
				Func<double, double> szd = c => BaseRisk * Math.Min(2.0, c);
				double gb = g.Average(x => szd(x.Cp));
				GShow("    flat 10% (gated) [SHIPPED]", _ => BaseRisk);
				GShow($"    flat {100 * gb:0.00}% [matched control]", _ => gb);
				GShow("    sized 10% x callPut within gate", szd, _ => gb);
				GShow("    INVERTED within gate (sign ctrl)",
					c => BaseRisk * Math.Min(2.0, Math.Max(0.5, 2.2 - c)), _ => g.Average(x => BaseRisk * Math.Min(2.0, Math.Max(0.5, 2.2 - x.Cp))));
			}

			// ---- SAME-DAY vs PRIOR-DAY, ON THE ROWS WHERE PROVENANCE IS KNOWN ----------------------------
			// Same-day gamma IS tradeable: open interest is fixed at the prior settlement, so today's gamma can be
			// computed live from standing OI and current spot. The only real question is whether the HISTORICAL
			// series supports the test. UW rows are live-captured (stamped 9:33 ET) from 2024-08-23; earlier rows
			// were bulk-backfilled on one date from an unknown input.
			//
			// The ratio is persistent but NOT redundant -- lag-1 autocorrelation 0.798, and a median overnight
			// move of 40% of one cross-sectional sd -- so if contemporaneous gamma matters, same-day should beat
			// prior-day in BOTH blocks. It is tested separately in each, paired on identical sessions.
			//
			// NOTE the 3-minute caveat: a 9:33 stamp cannot size a 9:30 entry. Treat the same-day arm as requiring
			// entry at ~9:35, which the earlier 1h work suggests costs nothing and may help.
			var cpSame = LoadCallPutSameDay();
			DateTime liveFrom = new DateTime(2024, 8, 23);
			Console.WriteLine("");
			Console.WriteLine("--- SAME-DAY vs PRIOR-DAY ratio, split on the backfill boundary ---");
			Console.WriteLine($"{"block / scheme",38} {"n",6} {"avgRisk%",9} {"mean/tr%",10} {"IR",8} {"maxDD%",8} {"paired t",9}");
			void Blk(string lbl, Func<Tr, bool> inBlock)
			{
				var g = tr.Where(x => inBlock(x) && cpSame.ContainsKey(x.D)).ToList();
				if (g.Count < 40) { Console.WriteLine($"{lbl,38} {g.Count,6}  (too few)"); return; }
				double RiskOf(double c) => BaseRisk * Math.Min(2.0, c);
				var prior = g.Select(x => RiskOf(x.Cp) * x.R).ToList();
				var same  = g.Select(x => RiskOf(cpSame[x.D]) * x.R).ToList();
				void One(string tag, List<double> r, List<double> vs)
				{
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					double e = 1, pk = 1, dd = 0;
					foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
					string ts = "";
					if (vs != null)
					{
						var d = r.Zip(vs, (a2, b2) => a2 - b2).ToList();
						double dm = d.Average();
						double dsd = Math.Sqrt(d.Sum(z => (z - dm) * (z - dm)) / (d.Count - 1));
						ts = dsd > 0 ? $"{dm / (dsd / Math.Sqrt(d.Count)):+0.00;-0.00}" : "";
					}
					Console.WriteLine($"{tag,38} {r.Count,6} " +
						$"{100 * (tag.Contains("same") ? g.Average(x => RiskOf(cpSame[x.D])) : g.Average(x => RiskOf(x.Cp))),9:0.00} " +
						$"{100 * m,10:+0.0000;-0.0000} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {ts,9}");
				}
				One($"{lbl} prior-day", prior, null);
				One($"{lbl} same-day", same, prior);
			}
			Blk("[BACKFILLED]", x => x.D < liveFrom);
			Blk("[LIVE 9:33]", x => x.D >= liveFrom);
			Blk("[ALL]", _ => true);
			Console.WriteLine("  paired t is same-day minus prior-day on identical sessions. If same-day only wins in");
			Console.WriteLine("  the backfilled block, that block's inputs are the explanation, not contemporaneous gamma.");

			// ---- SWAP THE STRUCTURE ON HIGH-RATIO DAYS ---------------------------------------------------
			// Use the ratio to choose the INSTRUMENT rather than the stake: put spread normally, long call when
			// call gamma strongly dominates. Both sized to the SAME implied delta so the comparison is structural
			// rather than a leverage difference -- a 0-DTE call bought at "10% risk" carries ~13x delta and rules
			// itself out immediately, which is not the question here.
			//
			// The mechanism argues against it before the numbers do: high call gamma means dealers are LONG gamma,
			// and dealer long gamma suppresses realised vol. A long call needs realised vol. So the ratio's good
			// regime is precisely the wrong regime for buying premium, and the two effects should fight.
			Console.WriteLine("");
			Console.WriteLine("--- PUT SPREAD vs LONG CALL, by call/put bucket (delta-matched) ---");
			var qs = tr.Select(x => x.Cp).OrderBy(v => v).ToList();
			double b25 = qs[(int)(qs.Count * 0.25)], b50 = qs[(int)(qs.Count * 0.50)], b75 = qs[(int)(qs.Count * 0.75)];
			Console.WriteLine($"{"bucket",22} {"n",5} {"PUT mean%",10} {"PUT IR",8} {"CALL .50 mean%",15} {"IR",8} " +
				$"{"CALL .70 mean%",15} {"IR",8}");
			double Tt = 1.0 / 252.0;
			// per-session call P&L per unit of PREMIUM, at a given call delta
			var callPnl = new Dictionary<double, Dictionary<DateTime, (double R, double Prem)>>();
			foreach (double cd in new[] { 0.50, 0.70 })
			{
				var map = new Dictionary<DateTime, (double, double)>();
				for (int i = 1; i + 1 < bars.Count; i++)
				{
					var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
					if (dTr < From) continue;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st3) && st3 == ShortTermState.Bear) continue;
					double S = bars[i + 1].Open, ST = bars[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;
					double kC = StrikeForCallDelta(S, iv, Tt, cd);
					double prem = CallPx(S, kC, iv, Tt);
					if (prem <= 1e-9) continue;
					map[dTr] = ((Math.Max(0, ST - kC) - prem) / prem, prem / S);
				}
				callPnl[cd] = map;
			}
			string Cell(List<Tr> g, double cd)
			{
				var rows = g.Where(x => callPnl[cd].ContainsKey(x.D)).ToList();
				if (rows.Count < 20) return $"{"(few)",15} {"",8}";
				// delta-match: outlay = targetDelta * prem/(cd*S); targetDelta uses the spread's implied delta
				var r = rows.Select(x => BaseRisk * NetDelta / callPnl[cd][x.D].Prem / cd * callPnl[cd][x.D].Prem
					* callPnl[cd][x.D].R * cd / cd).ToList();
				// simplify: delta-matched outlay = BaseRisk * (spread impDelta) * prem/(cd*S) -- prem/S stored
				r = rows.Select(x => BaseRisk * (NetDelta / 0.0) * 0.0).ToList();   // placeholder, replaced below
				r = rows.Select(x => (BaseRisk * NetDelta / cd) * callPnl[cd][x.D].R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				return $"{100 * m,15:+0.0000;-0.0000} {(sd > 0 ? m / sd : 0),8:0.000}";
			}
			(string L, Func<Tr, bool> P)[] cbk =
			{
				($"cp < {b25:0.00}", x => x.Cp < b25),
				($"{b25:0.00} - {b50:0.00}", x => x.Cp >= b25 && x.Cp < b50),
				($"{b50:0.00} - {b75:0.00}", x => x.Cp >= b50 && x.Cp < b75),
				($"cp > {b75:0.00} (best)", x => x.Cp >= b75),
			};
			foreach (var (L, P) in cbk)
			{
				var g = tr.Where(P).ToList();
				var pr = g.Select(x => BaseRisk * x.R).ToList();
				double pm = pr.Average();
				double psd = Math.Sqrt(pr.Sum(z => (z - pm) * (z - pm)) / (pr.Count - 1));
				Console.WriteLine($"{L,22} {g.Count,5} {100 * pm,10:+0.0000;-0.0000} {(psd > 0 ? pm / psd : 0),8:0.000} " +
					$"{Cell(g, 0.50)} {Cell(g, 0.70)}");
			}
			Console.WriteLine("  calls are delta-matched to the spread, so this compares STRUCTURES, not leverage.");

			// THE DEEP PUT-HEAVY TAIL. The buckets above lump mild and extreme together. Heavy put gamma means
			// dealers are SHORT gamma, and short-gamma hedging is pro-cyclical -- it AMPLIFIES realised vol. That
			// is the one regime where buying premium has a mechanism working for it, and it is also the regime the
			// shipped gate throws away entirely. Sample counts are printed first because this is a distribution
			// tail and the cells will be thin.
			Console.WriteLine("");
			Console.WriteLine("--- THE DEEP PUT-HEAVY TAIL (days the gate discards) ---");
			Console.WriteLine($"  call/put range in sample: {tr.Min(x => x.Cp):0.00} to {tr.Max(x => x.Cp):0.00}");
			Console.WriteLine($"{"bucket",22} {"n",5} {"undMove%",10} {"PUT mean%",10} {"PUT IR",8} " +
				$"{"CALL .50 mean%",15} {"IR",8} {"CALL .70 mean%",15} {"IR",8}");
			foreach (var (L, P) in new (string, Func<Tr, bool>)[]
			{
				("cp < 0.50", x => x.Cp < 0.50),
				("cp < 0.60", x => x.Cp < 0.60),
				("cp < 0.75", x => x.Cp < 0.75),
				("cp < 0.85", x => x.Cp < 0.85),
				("0.50 - 0.75", x => x.Cp >= 0.50 && x.Cp < 0.75),
				("0.75 - 1.00", x => x.Cp >= 0.75 && x.Cp < 1.00),
			})
			{
				var g = tr.Where(P).ToList();
				if (g.Count < 12) { Console.WriteLine($"{L,22} {g.Count,5}  (too few)"); continue; }
				var pr = g.Select(x => BaseRisk * x.R).ToList();
				double pm = pr.Average();
				double psd = Math.Sqrt(pr.Sum(z => (z - pm) * (z - pm)) / Math.Max(1, pr.Count - 1));
				string flag = g.Count < 40 ? "  << thin" : "";
				Console.WriteLine($"{L,22} {g.Count,5} {"",10} {100 * pm,10:+0.0000;-0.0000} " +
					$"{(psd > 0 ? pm / psd : 0),8:0.000} {Cell(g, 0.50)} {Cell(g, 0.70)}{flag}");
			}
			Console.WriteLine("  a long call needs realised vol; if the short-gamma mechanism is real it should show HERE");
			Console.WriteLine("  or nowhere. Watch the counts -- the deepest cells are a distribution tail.");

			// ---- ABSOLUTE vs PERCENTILE-NORMALISED, the cross-instrument question ------------------------
			// The shipped rule multiplies by the RAW ratio, which bakes in SPY's distribution. A percentile form
			// asks only "is today call-heavy RELATIVE TO THIS NAME'S OWN HISTORY", using an expanding window so
			// nothing is fitted. If the signal is real the percentile version should work on every instrument
			// while the absolute version only works where its scale happens to fit.
			Console.WriteLine($"\n--- ABSOLUTE (shipped) vs PERCENTILE-NORMALISED sizing ---");
			var ordCp = tr.OrderBy(x => x.D).ToList();
			var hist = new List<double>();
			var pctOf = new Dictionary<DateTime, double>();
			foreach (var x in ordCp)
			{
				if (hist.Count >= 120) pctOf[x.D] = (double)hist.Count(z => z < x.Cp) / hist.Count;
				hist.Add(x.Cp);
			}
			var elig = ordCp.Where(x => pctOf.ContainsKey(x.D)).ToList();
			Console.WriteLine($"  {elig.Count} sessions after a 120-day warm-up | median call/put {ordCp.OrderBy(x => x.Cp).ElementAt(ordCp.Count / 2).Cp:0.00}");
			Console.WriteLine($"{"scheme",38} {"avgRisk%",9} {"mean/tr%",10} {"IR",8} {"maxDD%",8} {"CAGR%",10} {"paired t",9}");
			void P2(string lbl, Func<Tr, double> riskOf, Func<Tr, double> baseline = null)
			{
				var r = elig.Select(x => riskOf(x) * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (elig.Last().D - elig.First().D).TotalDays / 365.25);
				string ts = "";
				if (baseline != null)
				{
					var d = elig.Select(x => (riskOf(x) - baseline(x)) * x.R).ToList();
					double dm = d.Average();
					double dsd = Math.Sqrt(d.Sum(z => (z - dm) * (z - dm)) / (d.Count - 1));
					ts = dsd > 0 ? $"{dm / (dsd / Math.Sqrt(d.Count)):+0.00;-0.00}" : "";
				}
				Console.WriteLine($"{lbl,38} {100 * elig.Average(riskOf),9:0.00} {100 * m,10:+0.0000;-0.0000} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {ts,9}");
			}
			// percentile form: multiplier 0.5 at the bottom of the name's own range, 1.5 at the top
			Func<Tr, double> pctRisk = x => BaseRisk * (0.5 + pctOf[x.D]);
			Func<Tr, double> pctInv = x => BaseRisk * (1.5 - pctOf[x.D]);
			Func<Tr, double> absRisk = x => BaseRisk * Math.Min(MaxMult, Math.Max(MinMult, x.Cp));
			double aBar = elig.Average(absRisk), pBar2 = elig.Average(pctRisk), iBar2 = elig.Average(pctInv);
			P2("flat 10%", _ => BaseRisk);
			P2($"flat {100 * aBar:0.00}% [ctrl for absolute]", _ => aBar);
			P2("ABSOLUTE: 10% x callPut (shipped)", absRisk, _ => aBar);
			P2($"flat {100 * pBar2:0.00}% [ctrl for pctile]", _ => pBar2);
			P2("PERCENTILE: 10% x (0.5 + pctile)", pctRisk, _ => pBar2);
			P2($"flat {100 * iBar2:0.00}% [ctrl for inverse]", _ => iBar2);
			P2("PERCENTILE INVERTED (sign control)", pctInv, _ => iBar2);

			// ---- THE GATE, CROSS-INSTRUMENT --------------------------------------------------------------
			// Gating is a different question from sizing and must be judged separately. An ABSOLUTE 1.00 cut keeps
			// ~62% of SPY sessions but 88% of GLD and only 12% of IWM, so comparing names on it confounds "does
			// the signal work" with "how much did the gate remove". Percentile gates fix the kept-fraction so the
			// selectivity is identical across instruments and only the signal differs.
			Console.WriteLine($"\n--- GATE: absolute vs percentile, {symbol} ---");
			Console.WriteLine($"{"gate",30} {"n",6} {"%kept",7} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8}");
			void G2(string lbl, IEnumerable<Tr> src, int denom)
			{
				var t = src.ToList();
				if (t.Count < 40) { Console.WriteLine($"{lbl,30} {t.Count,6}  (too few)"); return; }
				var r = t.Select(x => BaseRisk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				Console.WriteLine($"{lbl,30} {t.Count,6} {100.0 * t.Count / denom,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00}");
			}
			G2("no gate", elig, elig.Count);
			foreach (double th in new[] { 1.00, 1.25 })
				G2($"absolute callPut > {th:0.00}", elig.Where(x => x.Cp > th), elig.Count);
			foreach (double q in new[] { 0.25, 0.50, 0.70 })
				G2($"top {100 * (1 - q):0}% of own history", elig.Where(x => pctOf[x.D] >= q), elig.Count);
			G2("bottom 30% (inverse control)", elig.Where(x => pctOf[x.D] < 0.30), elig.Count);

			Console.WriteLine("  for comparison, the 0.90-slope version already tested:");
			double s9 = tr.Average(x => BaseRisk * Mult(x.Cp));
			Show($"flat {100 * s9:0.00}% [control]", _ => s9);
			Show("risk = 10% x clamp(0.90 x callPut)", c => BaseRisk * Mult(c), _ => s9);

			Console.WriteLine($"\n--- clamp sensitivity (slope held at {Slope:0.00}) ---");
			double sMin = MinMult, sMax = MaxMult;
			foreach (var (lo, hi) in new[] { (0.75, 1.5), (0.5, 2.0), (0.34, 3.0), (0.25, 4.0) })
			{
				MinMult = lo; MaxMult = hi;
				double b2 = tr.Average(x => BaseRisk * Mult(x.Cp));
				Show($"  clamp [{lo:0.00}, {hi:0.00}] vs its control", c => BaseRisk * Mult(c), _ => b2);
			}
			MinMult = sMin; MaxMult = sMax;
			Console.WriteLine("Wider clamps let the signal act harder; if the paired t rises with the clamp the effect is");
			Console.WriteLine("in the tails, if it flattens the middle of the distribution is doing the work.");
		}

		// Same-day map: keyed by the TRADE date rather than the signal date, so a lookup returns the ratio
		// published for the session being traded.
		private static Dictionary<DateTime, double> LoadCallPutSameDay() => LoadCallPut();

		private static Dictionary<DateTime, double> LoadCallPut()
		{
			var m = new Dictionary<DateTime, double>();
			string p = Path.Combine(Path.GetFullPath(Universe.DataDir), $"gex_uw_{DataSym}.csv");
			if (!File.Exists(p)) return m;
			var lines = File.ReadAllLines(p);
			var h = lines[0].Split(',');
			int di = Array.IndexOf(h, "date"), ci = Array.IndexOf(h, "call_gex"), pi = Array.IndexOf(h, "put_gex");
			for (int i = 1; i < lines.Length; i++)
			{
				var f = lines[i].Split(',');
				if (f.Length <= Math.Max(ci, pi)) continue;
				if (DateTime.TryParse(f[di], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
					&& double.TryParse(f[ci], NumberStyles.Any, CultureInfo.InvariantCulture, out var cg)
					&& double.TryParse(f[pi], NumberStyles.Any, CultureInfo.InvariantCulture, out var pg)
					&& Math.Abs(pg) > 0)
					m[d.Date] = cg / Math.Abs(pg);          // CALL / PUT -- higher is better
			}
			return m;
		}

		private static double CallPx(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return Math.Max(0, S - K);
			double v = iv * Math.Sqrt(T);
			double d1 = (Math.Log(S / K) + 0.5 * iv * iv * T) / v;
			return S * Nd(d1) - K * Nd(d1 - v);
		}
		private static double CallDeltaOf(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S > K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd((Math.Log(S / K) + 0.5 * iv * iv * T) / v);
		}
		private static double StrikeForCallDelta(double S, double iv, double T, double d)
		{
			double lo = S * 0.05, hi = S * 3.0;
			for (int i = 0; i < 80; i++) { double mid = 0.5 * (lo + hi); if (CallDeltaOf(S, mid, iv, T) > d) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
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
