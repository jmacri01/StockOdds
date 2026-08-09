using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Can ANY long call match the put credit spread? Sweeps call delta from deep OTM to deep ITM.
	//
	// SIZING IS THE WHOLE ARGUMENT, so both conventions are shown side by side:
	//   RISK-MATCHED   premium outlay = 10% of bankroll, mirroring "10% risk" on the spread. But a long call
	//                  loses its ENTIRE outlay whenever it expires OTM, whereas the spread realises max loss on
	//                  ~7% of trades -- so the same words mean a far heavier risk, and the implied delta explodes
	//                  because a 0-DTE call costs so little per unit of delta.
	//   DELTA-MATCHED  size the call to carry the SAME implied delta as the spread. This is the structural
	//                  question -- same directional exposure, which instrument delivers it better -- and it makes
	//                  the outlay an OUTPUT. Outlay is printed because deep-ITM calls can exceed the bankroll,
	//                  in which case the row is arithmetic rather than a trade.
	//
	// A long call needs the market to finish above the strike BY MORE THAN THE PREMIUM, so its win rate sits well
	// below its delta. Delta is P(finish ITM), not P(profit) -- the gap is what the sweep is really measuring.
	public static class CallDeltaSweep
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;
		public static double[] Deltas = { 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90 };

		private sealed record Tr(DateTime D, double CallPnlPerPrem, double PremPctS, double CallD,
			double SpreadR, double SpreadImpDelta);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
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
			List<Tr> Build(double cd)
			{
				var outp = new List<Tr>();
				for (int i = 1; i + 1 < bars.Count; i++)
				{
					var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					double S = bars[i + 1].Open, ST = bars[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;

					double kC = StrikeForCallDelta(S, iv, T, cd);
					double prem = Call(S, kC, iv, T);
					if (prem <= 1e-9) continue;
					double callPnl = (Math.Max(0, ST - kC) - prem) / prem;      // per unit of premium paid

					double pS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
					double pL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, pS, iv, T) - Put(S, pL, iv, T);
					double ml = (pS - pL) - cr;
					if (cr <= 1e-9 || ml <= 1e-9) continue;
					double po = -Math.Max(0, pS - ST) + Math.Max(0, pL - ST);
					outp.Add(new Tr(dTr, callPnl, prem / S, cd, (cr + po) / ml, NetDelta * S / ml));
				}
				return outp;
			}

			var refSet = Build(0.50);
			Console.WriteLine($"\n===== {symbol}: CAN ANY LONG CALL MATCH THE PUT SPREAD? =====");
			Console.WriteLine($"shipped filters (exposure >= {TargetLo:0.00}, ST Bear skipped) | {refSet.Count} sessions " +
				$"{refSet.First().D:yyyy-MM} -> {refSet.Last().D:yyyy-MM}");

			void Stat(string lbl, List<double> r, string extra)
			{
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				Console.WriteLine($"{lbl,26} {extra} {100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} " +
					$"{(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} {100 * r.Min(),8:0.00}");
			}

			var pr = refSet.Select(x => Risk * x.SpreadR).ToList();
			Console.WriteLine($"\n--- RISK-MATCHED: premium outlay = {100 * Risk:0.#}% of bankroll ---");
			Console.WriteLine($"{"structure",26} {"prem%S",8} {"impDelta%",10} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"worst%",8}");
			Stat("PUT SPREAD [shipped]", pr, $"{"--",8} {100 * Risk * refSet.Average(x => x.SpreadImpDelta),10:0.0}");
			foreach (double cd in Deltas)
			{
				var t = Build(cd);
				var r = t.Select(x => Risk * x.CallPnlPerPrem).ToList();
				double impD = 100 * Risk * t.Average(x => cd / x.PremPctS);
				Stat($"long call {cd:0.00}d", r, $"{100 * t.Average(x => x.PremPctS),8:0.000} {impD,10:0.0}");
			}

			Console.WriteLine($"\n--- DELTA-MATCHED: call sized to the spread's implied delta ---");
			Console.WriteLine($"{"structure",26} {"outlay%",8} {"impDelta%",10} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"worst%",8}");
			Stat("PUT SPREAD [shipped]", pr, $"{100 * Risk,8:0.0} {100 * Risk * refSet.Average(x => x.SpreadImpDelta),10:0.0}");
			foreach (double cd in Deltas)
			{
				var t = Build(cd);
				// qty*cd*S = targetDelta*bankroll  =>  outlay = qty*prem = targetDelta*prem/(cd*S)
				var outlay = t.Select(x => Risk * x.SpreadImpDelta * x.PremPctS / cd).ToList();
				var r = t.Select((x, i) => outlay[i] * x.CallPnlPerPrem).ToList();
				Stat($"long call {cd:0.00}d", r,
					$"{100 * outlay.Average(),8:0.0} {100 * Risk * refSet.Average(x => x.SpreadImpDelta),10:0.0}");
			}
			Console.WriteLine("outlay% is capital actually committed; above 100 the row cannot be funded and is arithmetic only.");
			Console.WriteLine("Note win% for a call sits far below its delta: delta is P(finish ITM), not P(beat the premium).");
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
