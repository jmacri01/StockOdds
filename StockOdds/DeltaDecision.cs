using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Should the shipped net delta move from 0.20 to 0.15?
	//
	// Three independent routes have now pointed at 0.15: the unconditional delta sweep, the wing sweep (which
	// found what matters is where the SHORT leg lands), and every per-bucket conditional optimum. This decides it
	// on the terms that have caught the last four false positives in this line:
	//
	//   PAIRED       the two deltas trade the SAME sessions and differ only in strike, so differencing per trade
	//                removes the shared market noise an unpaired SE would drown the effect in.
	//   THREE NAMES  SPY, IWM and GLD. GLD matters most -- it is a different asset class with its own vol market,
	//                and it is the one instrument whose expiry calendar was VERIFIED rather than assumed.
	//   SPLIT-HALF   a parameter that only wins in one half of the sample is fitted to a regime, not to the trade.
	//   OBSERVED IV  ^VIX1D re-pricing. The 1.10 assumption is measurably wrong (~0.99 at this tenor) and a delta
	//                optimum found under the wrong IV need not survive the right one. If the ranking flips here,
	//                the whole comparison was an artifact of the assumption.
	public static class DeltaDecision
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool   SkipStBear = true;
		public static double[] Deltas = { 0.10, 0.15, 0.20, 0.25 };

		private sealed record Tr(DateTime D, double R);

		public static async Task Run(params string[] symbols)
		{
			var v1d = await VixSeries("^VIX1D");
			foreach (var sym in symbols) await One(sym, v1d);
		}

		private static async Task<Dictionary<DateTime, double>> VixSeries(string s)
		{
			try
			{
				var v = await YahooClient.GetBarsAsync(s, "1d", 25);
				return v.Where(b => b.Close > 0).GroupBy(b => b.Date.Date)
				        .ToDictionary(g => g.Key, g => g.Last().Close / 100.0);
			}
			catch { return new(); }
		}

		private static async Task One(string symbol, Dictionary<DateTime, double> v1d)
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

			// ivMode 0 = assumed HV*VRP, 1 = observed VIX1D (session skipped when absent)
			List<Tr> Build(double netD, int ivMode)
			{
				double T = 1.0 / 252.0;
				var tr = new List<Tr>();
				for (int i = 1; i + 1 < bars.Count; i++)
				{
					var d = bars[i].Date.Date;
					if (!hv.TryGetValue(d, out double h)) continue;
					if (!pos.TryGetValue(d, out double target) || target < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(bars[i + 1].Date)) continue;
					if (SkipStBear && stm.TryGetValue(d, out var st) && st == ShortTermState.Bear) continue;
					double iv;
					if (ivMode == 1) { if (!v1d.TryGetValue(d, out iv)) continue; }
					else iv = h * VolRiskPremium;
					double S = bars[i + 1].Open, ST = bars[i + 1].Close;
					if (S <= 0 || ST <= 0 || iv <= 0) continue;
					double kS = StrikeForPutDelta(S, iv, T, netD + WingDelta);
					double kL = StrikeForPutDelta(S, iv, T, WingDelta);
					double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
					double maxLoss = (kS - kL) - cr;
					if (cr <= 1e-9 || maxLoss <= 1e-9) continue;
					double payoff = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
					tr.Add(new Tr(bars[i + 1].Date, (cr + payoff) / maxLoss));
				}
				return tr;
			}

			(double m, double sd, double ir, double dd, double cagr, double win) Stats(List<Tr> t)
			{
				var r = t.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / Math.Max(1, r.Count - 1));
				double e = 1, p = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e > p) p = e; double q = (p - e) / p * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				double cagr = e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100;
				return (m, sd, sd > 0 ? m / sd : 0, dd, cagr, 100.0 * r.Count(z => z > 0) / r.Count);
			}

			Console.WriteLine($"\n===== {symbol}: IS 0.15 BETTER THAN THE SHIPPED 0.20? =====");
			Console.WriteLine($"{"delta",8} {"trades",7} {"mean/tr%",10} {"win%",7} {"IR/tr",8} {"maxDD%",8} {"CAGR%",9}");
			foreach (double dd in Deltas)
			{
				var t = Build(dd, 0);
				if (t.Count < 50) { Console.WriteLine($"{dd,8:0.00} {t.Count,7}  (too few)"); continue; }
				var s = Stats(t);
				Console.WriteLine($"{dd,8:0.00} {t.Count,7} {100 * s.m,10:+0.0000;-0.0000} {s.win,7:0.0} " +
					$"{s.ir,8:0.000} {s.dd,8:0.00} {s.cagr,9:0.0}");
			}

			// paired: identical sessions, only the strike differs
			var a15 = Build(0.15, 0).ToDictionary(x => x.D, x => Risk * x.R);
			var a20 = Build(0.20, 0);
			var diff = a20.Where(x => a15.ContainsKey(x.D)).Select(x => a15[x.D] - Risk * x.R).ToList();
			if (diff.Count > 30)
			{
				double dm = diff.Average();
				double dsd = Math.Sqrt(diff.Sum(z => (z - dm) * (z - dm)) / (diff.Count - 1));
				Console.WriteLine($"paired 0.15 - 0.20 over {diff.Count} shared sessions: {100 * dm:+0.0000;-0.0000}%/trade, " +
					$"t = {(dsd > 0 ? dm / (dsd / Math.Sqrt(diff.Count)) : 0):+0.00;-0.00}");
			}

			// split-half: a parameter that wins in only one half is fitted to a regime
			var full = Build(0.15, 0);
			if (full.Count > 200)
			{
				DateTime mid = full[full.Count / 2].D;
				foreach (var (lbl, keep) in new (string, Func<Tr, bool>)[]
					{ ("first half", x => x.D < mid), ("second half", x => x.D >= mid) })
				{
					var s15 = Stats(Build(0.15, 0).Where(keep).ToList());
					var s20 = Stats(Build(0.20, 0).Where(keep).ToList());
					Console.WriteLine($"  {lbl,-12} IR 0.15 = {s15.ir:0.000}   IR 0.20 = {s20.ir:0.000}   " +
						$"{(s15.ir > s20.ir ? "0.15 wins" : "0.20 wins")}");
				}
			}

			// observed IV: does the ranking survive the correct vol level?
			var o15 = Build(0.15, 1); var o20 = Build(0.20, 1);
			if (o15.Count > 100 && o20.Count > 100)
			{
				var s15 = Stats(o15); var s20 = Stats(o20);
				Console.WriteLine($"  at OBSERVED ^VIX1D ({o15.Count} sessions from {o15.First().D:yyyy-MM}): " +
					$"IR 0.15 = {s15.ir:0.000}   IR 0.20 = {s20.ir:0.000}   " +
					$"{(s15.ir > s20.ir ? "0.15 wins" : "0.20 wins")}");
			}
			else Console.WriteLine("  at OBSERVED ^VIX1D: too few sessions (VIX1D starts 2023-04)");
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
