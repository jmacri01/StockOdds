using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Replace the shipped selection (exposure >= 0.10 AND not ST Bear) with the single coarsest signal the engine
	// has: is the LONG-TERM state Bull? Trade every candle in LT Bull, ignore exposure and ST state entirely.
	//
	// WHY IT MIGHT WIN. The exposure floor and the ST-Bear skip are two conditions fitted on this data; the LT
	// state is a slow, structural read that changes a handful of times a year and has far less capacity to be
	// fitted. If it captures most of the same sessions, the extra machinery is over-engineering.
	//
	// WHY IT MIGHT LOSE. The exposure target is a FUNCTION of the LT state -- LT Bull maps to the higher rows of
	// the bucket table -- so the two overlap heavily by construction. Dropping the floor also re-admits the
	// sub-0.10 sessions, which measured IR 0.011 on their own: genuinely dead, not merely filtered.
	//
	// Every arm runs the shipped structure (0.15 wing / 0.35 short, net 0.20) at 10% risk on real expiry dates,
	// so only SELECTION varies. Full 21-year history.
	public static class LtBullFilter
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;

		private sealed record Tr(DateTime D, double R, bool LtBull, bool StBear, double Exp);

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
			var ltm = new Dictionary<DateTime, LongTermState>();
			for (int k = 0; k < eng.LtState.Count && k < eng.ReturnDates.Count; k++)
				ltm[eng.ReturnDates[k].Date] = eng.LtState[k];
			if (ltm.Count == 0) { Console.WriteLine("LtState not populated"); return; }

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
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double tg)) continue;
				if (!ltm.TryGetValue(dSig, out var lt)) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				double iv = h * VolRiskPremium;
				double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
				double kL = StrikeForPutDelta(S, iv, T, WingDelta);
				double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) continue;
				double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
				stm.TryGetValue(dSig, out var st);
				all.Add(new Tr(dTr, (cr + po) / ml, lt == LongTermState.Bull, st == ShortTermState.Bear, tg));
			}

			Console.WriteLine($"\n===== {symbol}: TRADE EVERY CANDLE IN LT BULL? =====");
			Console.WriteLine($"{all.Count} real-expiry sessions {all.First().D:yyyy-MM} -> {all.Last().D:yyyy-MM} | " +
				$"LT Bull on {100.0 * all.Count(x => x.LtBull) / all.Count:0.0}% of them");
			Console.WriteLine($"\n{"selection",40} {"n",6} {"%kept",7} {"mean/tr%",10} {"win%",7} {"IR",8} " +
				$"{"maxDD%",8} {"CAGR%",10} {"worst%",8}");
			void Row(string lbl, Func<Tr, bool> keep)
			{
				var t = all.Where(keep).OrderBy(x => x.D).ToList();
				if (t.Count < 30) { Console.WriteLine($"{lbl,40} {t.Count,6}  (too few)"); return; }
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,40} {t.Count,6} {100.0 * t.Count / all.Count,7:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {100 * r.Min(),8:0.00}");
			}
			bool Ship(Tr x) => x.Exp >= TargetLo && !x.StBear;
			Row("no selection at all", _ => true);
			Row("SHIPPED: exp >= 0.10 AND not ST Bear", Ship);
			Row("LT Bull only (ignore exp and ST)", x => x.LtBull);
			Row("LT Bull + exp >= 0.10", x => x.LtBull && x.Exp >= TargetLo);
			Row("LT Bull + not ST Bear", x => x.LtBull && !x.StBear);
			Row("LT Bull + SHIPPED filters", x => x.LtBull && Ship(x));
			Row("LT Bear only", x => !x.LtBull);
			Row("LT Bear + SHIPPED filters", x => !x.LtBull && Ship(x));

			// How much do the two selections actually overlap? If LT Bull already contains the shipped set, the
			// extra conditions are redundant; if it admits a large disjoint block, that block is what decides.
			int both = all.Count(x => x.LtBull && Ship(x));
			int bullOnly = all.Count(x => x.LtBull && !Ship(x));
			int shipOnly = all.Count(x => !x.LtBull && Ship(x));
			Console.WriteLine($"\noverlap: {both} sessions in BOTH | {bullOnly} LT-Bull-only (shipped rejects) | " +
				$"{shipOnly} shipped-only (LT Bear)");
			Row("  the LT-Bull-only block alone", x => x.LtBull && !Ship(x));
			Row("  the shipped-only block alone", x => !x.LtBull && Ship(x));
			Console.WriteLine("The two disjoint blocks are what the choice is actually between -- everything else is shared.");
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
