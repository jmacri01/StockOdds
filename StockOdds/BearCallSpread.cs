using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// A CALL credit spread on the days the shipped strategy throws away: put/call gamma ratio > 1 (dealers short
	// gamma), engine exposure below the 0.10 floor, and not ST Bull. Short the 0.35-delta call, long the 0.15-delta
	// call -- net delta -0.20, the exact mirror of the shipped put spread.
	//
	// This is still SHORT PREMIUM, not a short position: it wins on decay and on the market failing to rally, and
	// it has the same defined risk. What it gives up is the drift. Equities rise on average, so a call spread pays
	// that away every session, and at a measured 1-day VRP of ~0.99 there is no vol premium left to cover it. The
	// bar is therefore higher than for the put spread, and the honest question is not "is it positive" but "is it
	// positive ENOUGH to beat leaving the capital idle -- or to beat simply running the put spread on those days".
	//
	// The put spread on the SAME days is carried for exactly that reason: if these sessions are fine for premium
	// selling generally, the bearish tilt is unnecessary, and the conditions are just finding quiet days.
	//
	// Prior finding worth stating up front: every short formulation tested on the stock engine lost to cash --
	// out-of-region was worth zero exposure, never negative. This is a different instrument, but the same tape.
	public static class BearCallSpread
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double ExposureCeil = 0.10;
		public static double RatioFloor = 1.0;
		public static DateTime From = new DateTime(2022, 3, 30);

		private sealed record Tr(DateTime D, double RCall, double RPut, double Under, ShortTermState St, double Exp, double Ratio);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(bars, 10_000.0);
			var ratio = LoadRatio();
			if (ratio.Count == 0) { Console.WriteLine("no UW ratio data"); return; }

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
				var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
				if (dTr < From) continue;
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double tgt)) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				if (!ratio.TryGetValue(dSig, out double rt)) continue;
				stm.TryGetValue(dSig, out var st);
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				double iv = h * VolRiskPremium;

				// CALL credit spread: short 0.35d call, long 0.15d call -> net delta -0.20
				double cS = StrikeForCallDelta(S, iv, T, NetDelta + WingDelta);
				double cL = StrikeForCallDelta(S, iv, T, WingDelta);
				double crC = Call(S, cS, iv, T) - Call(S, cL, iv, T);
				double mlC = (cL - cS) - crC;                     // long strike is ABOVE the short one
				double poC = -Math.Max(0, ST - cS) + Math.Max(0, ST - cL);
				// mirrored PUT spread on the same day, for the "is the tilt even needed" comparison
				double pS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double pL = StrikeForPutDelta(S, iv, T, WingDelta);
				double crP = Put(S, pS, iv, T) - Put(S, pL, iv, T);
				double mlP = (pS - pL) - crP;
				double poP = -Math.Max(0, pS - ST) + Math.Max(0, pL - ST);
				if (crC <= 1e-9 || mlC <= 1e-9 || crP <= 1e-9 || mlP <= 1e-9) continue;

				all.Add(new Tr(dTr, (crC + poC) / mlC, (crP + poP) / mlP, (ST - S) / S, st, tgt, rt));
			}

			bool Qual(Tr x) => x.Ratio > RatioFloor && x.Exp < ExposureCeil && x.St != ShortTermState.Bull;
			var q = all.Where(Qual).ToList();

			Console.WriteLine($"\n===== {symbol}: BEAR CALL SPREAD ON THE DISCARDED DAYS ({From:yyyy-MM}+) =====");
			Console.WriteLine($"conditions: put/call ratio > {RatioFloor:0.00}, engine exposure < {ExposureCeil:0.00}, not ST Bull");
			Console.WriteLine($"{q.Count} qualifying sessions of {all.Count} ({100.0 * q.Count / all.Count:0.0}%) | " +
				$"underlying mean move on those days {100 * (q.Count > 0 ? q.Average(x => x.Under) : 0):+0.000;-0.000}% " +
				$"(all days {100 * all.Average(x => x.Under):+0.000;-0.000}%)");

			void Show(string lbl, List<Tr> t, Func<Tr, double> pick, double rk)
			{
				if (t.Count < 20) { Console.WriteLine($"{lbl,38} {100 * rk,5:0.#}% {t.Count,6}  (too few)"); return; }
				var r = t.Select(x => rk * pick(x)).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double qd = (pk - e) / pk * 100; if (qd > dd) dd = qd; }
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,38} {100 * rk,5:0.#}% {t.Count,6} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {100 * r.Min(),8:0.00}");
			}
			Console.WriteLine($"\n{"arm",38} {"risk",6} {"n",6} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10} {"worst%",8}");
			foreach (double rk in new[] { 0.05, 0.10 })
			{
				Show("CALL spread on qualifying days", q, x => x.RCall, rk);
				Show("  PUT spread, same days", q, x => x.RPut, rk);
			}

			// ---- FULL COMBINATION MATRIX -----------------------------------------------------------------
			// Every subset of the three conditions, so no variant needs asking about twice. net gex < 0 is
			// arithmetically identical to ratio > 1 (net = call + put, put stored negative) -- one condition, not
			// two. The put spread and the underlying's own drift ride on the same rows: a bearish structure needs
			// the tape to FALL, so any cell with a positive mean move cannot work however the conditions combine.
			Console.WriteLine($"\n--- ALL CONDITION COMBINATIONS (10% risk) ---");
			Console.WriteLine("  gex<0 = net gamma negative = put/call ratio > 1 (the same condition)");
			Console.WriteLine($"{"conditions",34} {"n",6} {"CALL mean%",11} {"CALL IR",8} {"PUT mean%",10} {"PUT IR",8} " +
				$"{"undMean%",9} {"down%",7}");
			void Combo(string lbl, Func<Tr, bool> f)
			{
				var t = all.Where(f).ToList();
				if (t.Count < 20) { Console.WriteLine($"{lbl,34} {t.Count,6}  (too few)"); return; }
				double MeanOf(Func<Tr, double> pick) => 0.10 * t.Average(pick);
				double IrOf(Func<Tr, double> pick)
				{
					var r = t.Select(x => 0.10 * pick(x)).ToList();
					double m = r.Average();
					double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
					return sd > 0 ? m / sd : 0;
				}
				Console.WriteLine($"{lbl,34} {t.Count,6} {100 * MeanOf(x => x.RCall),11:+0.0000;-0.0000} " +
					$"{IrOf(x => x.RCall),8:+0.000;-0.000} {100 * MeanOf(x => x.RPut),10:+0.0000;-0.0000} " +
					$"{IrOf(x => x.RPut),8:+0.000;-0.000} {100 * t.Average(x => x.Under),9:+0.000;-0.000} " +
					$"{100.0 * t.Count(x => x.Under < 0) / t.Count,7:0.0}");
			}
			bool G(Tr x) => x.Ratio > RatioFloor;                     // net gex < 0
			bool E(Tr x) => x.Exp < ExposureCeil;                     // engine exposure below the floor
			bool B(Tr x) => x.St != ShortTermState.Bull;              // not ST Bull
			Combo("(none) every session", _ => true);
			Combo("gex<0", G);
			Combo("exposure<0.10", E);
			Combo("not ST Bull", B);
			Combo("gex<0 + exposure<0.10  <-- ASKED", x => G(x) && E(x));
			Combo("gex<0 + not ST Bull", x => G(x) && B(x));
			Combo("exposure<0.10 + not ST Bull", x => E(x) && B(x));
			Combo("all three", x => G(x) && E(x) && B(x));
			Combo("gex<0 + exp<0.10 + ST Bear only", x => G(x) && E(x) && x.St == ShortTermState.Bear);

			Console.WriteLine($"\n--- which condition is doing the work? (call spread, 10% risk) ---");
			Show("all days", all, x => x.RCall, 0.10);
			Show("ratio > 1 only", all.Where(x => x.Ratio > RatioFloor).ToList(), x => x.RCall, 0.10);
			Show("exposure < 0.10 only", all.Where(x => x.Exp < ExposureCeil).ToList(), x => x.RCall, 0.10);
			Show("not ST Bull only", all.Where(x => x.St != ShortTermState.Bull).ToList(), x => x.RCall, 0.10);
			Show("all three [the proposal]", q, x => x.RCall, 0.10);

			// ---- ST BEAR ONLY, exposure ignored ----------------------------------------------------------
			// ST Bear is discarded outright by the shipped config, so these sessions are free either way. The
			// exposure floor is dropped: the claim under test is that the ST state alone, plus put-heavy gamma,
			// finds tape worth fading. Both structures run on the identical days, and the underlying's own mean
			// move is printed -- a bearish structure can only work if the tape actually falls, and every bucket
			// examined so far has had a POSITIVE mean move, which is what has killed the short side before.
			Console.WriteLine($"\n--- ST BEAR ONLY, any exposure (10% risk) ---");
			Console.WriteLine($"{"arm",38} {"risk",6} {"n",6} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10} {"worst%",8}");
			var bearAll = all.Where(x => x.St == ShortTermState.Bear).ToList();
			var bearHi = bearAll.Where(x => x.Ratio > RatioFloor).ToList();
			var bearLo = bearAll.Where(x => x.Ratio <= RatioFloor).ToList();
			void Und(string lbl, List<Tr> t) =>
				Console.WriteLine($"    {lbl}: n={t.Count}, underlying mean move " +
					$"{100 * (t.Count > 0 ? t.Average(x => x.Under) : 0):+0.000;-0.000}%, " +
					$"down-days {100.0 * (t.Count > 0 ? t.Count(x => x.Under < 0) : 0) / Math.Max(1, t.Count):0.0}%");
			Show("CALL spread, ST Bear + ratio > 1", bearHi, x => x.RCall, 0.10);
			Show("  PUT spread, same days", bearHi, x => x.RPut, 0.10);
			Show("CALL spread, ST Bear + ratio <= 1", bearLo, x => x.RCall, 0.10);
			Show("  PUT spread, same days", bearLo, x => x.RPut, 0.10);
			Show("CALL spread, ST Bear (no ratio gate)", bearAll, x => x.RCall, 0.10);
			Show("  PUT spread, same days", bearAll, x => x.RPut, 0.10);
			Console.WriteLine("  underlying behaviour on those same sessions:");
			Und("ST Bear + ratio > 1", bearHi);
			Und("ST Bear + ratio <= 1", bearLo);
			Und("ST Bear, all", bearAll);
			Und("every session", all);

			Console.WriteLine($"\n--- qualifying days by ST state (call spread, 10% risk) ---");
			foreach (var st in new[] { ShortTermState.BullNeutral, ShortTermState.BearNeutral, ShortTermState.Bear })
				Show($"{st}", q.Where(x => x.St == st).ToList(), x => x.RCall, 0.10);
		}

		private static Dictionary<DateTime, double> LoadRatio()
		{
			var m = new Dictionary<DateTime, double>();
			string p = Path.Combine(Path.GetFullPath(Universe.DataDir), "gex_uw_spx.csv");
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
		private static double Call(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return Math.Max(0, S - K);
			double v = iv * Math.Sqrt(T);
			double d1 = (Math.Log(S / K) + 0.5 * iv * iv * T) / v;
			return S * Nd(d1) - K * Nd(d1 - v);
		}
		private static double PutDeltaMag(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S < K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd(-((Math.Log(S / K) + 0.5 * iv * iv * T) / v));
		}
		private static double CallDeltaOf(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S > K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd((Math.Log(S / K) + 0.5 * iv * iv * T) / v);
		}
		private static double StrikeForPutDelta(double S, double iv, double T, double mag)
		{
			double lo = S * 0.05, hi = S * 3.0;
			for (int i = 0; i < 80; i++) { double mid = 0.5 * (lo + hi); if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
		}
		private static double StrikeForCallDelta(double S, double iv, double T, double d)
		{
			double lo = S * 0.05, hi = S * 3.0;
			for (int i = 0; i < 80; i++) { double mid = 0.5 * (lo + hi); if (CallDeltaOf(S, mid, iv, T) > d) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
		}
	}
}
