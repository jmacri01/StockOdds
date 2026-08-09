using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// In ST Bull, buy a 0.50-delta 0-DTE CALL instead of selling the put credit spread.
	//
	// This inverts the trade. The spread is short premium and wins ~85% of the time on small credits; a long ATM
	// call is LONG premium, wins under half the time, and needs the tail. Three consequences the tables must make
	// visible rather than bury:
	//
	//   RISK IS NOT COMPARABLE AT THE SAME "RISK %". The spread realises its max loss on ~7% of trades; a long call
	//   loses its ENTIRE debit whenever it expires out of the money -- around half the time. Sizing both to "10% of
	//   bankroll at risk" therefore means something completely different, and the call's implied delta is roughly
	//   an order of magnitude larger because a 0-DTE ATM call costs so little per unit of delta.
	//
	//   IT WANTS THE OPPOSITE VOL REGIME. The credit spread profits when realised vol comes in under implied; the
	//   call needs the reverse. A prior study found the long 0-DTE call spread's WORST bucket was the highest-gamma
	//   one, since dealer long gamma suppresses realised vol -- so the shipped gamma gate may select AGAINST this
	//   structure. Gated and ungated arms are both carried for that reason.
	//
	//   THE VRP HEADWIND IS SMALLER THAN IT LOOKS. Measured 1-day VRP is ~0.99, not the 1.10 the harness assumes,
	//   so buying premium is not systematically overpaying at this tenor the way it would be further out. That is
	//   the one thing arguing for the idea, and it also means these numbers -- priced at 1.10 -- UNDERSTATE the
	//   call arm and OVERSTATE the spread.
	public static class BullCallSwitch
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double CallDelta = 0.50;
		public static double TargetLo = 0.10;
		public static DateTime From = new DateTime(2022, 3, 30);

		private sealed record Tr(DateTime D, double R, double ImpDelta, ShortTermState St, double Ratio, bool IsCall);

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
			// callInBull: replace the spread with a long call whenever ST Bull
			List<Tr> Build(bool callInBull)
			{
				var outp = new List<Tr>();
				for (int i = 1; i + 1 < bars.Count; i++)
				{
					var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
					if (dTr < From) continue;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (!ratio.TryGetValue(dSig, out double rt)) continue;
					stm.TryGetValue(dSig, out var st);
					if (st == ShortTermState.Bear) continue;                       // shipped skip
					double S = bars[i + 1].Open, ST = bars[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					double iv = h * VolRiskPremium;

					if (callInBull && st == ShortTermState.Bull)
					{
						double kC = StrikeForCallDelta(S, iv, T, CallDelta);
						double prem = Call(S, kC, iv, T);
						if (prem <= 1e-9) continue;
						// max loss IS the debit, so R is measured against the premium paid
						double payoff = Math.Max(0, ST - kC);
						outp.Add(new Tr(dTr, (payoff - prem) / prem, CallDelta * S / prem, st, rt, true));
					}
					else
					{
						double kS = StrikeForPutDelta(S, iv, T, NetDelta + WingDelta);
						double kL = StrikeForPutDelta(S, iv, T, WingDelta);
						double cr = Put(S, kS, iv, T) - Put(S, kL, iv, T);
						double ml = (kS - kL) - cr;
						if (cr <= 1e-9 || ml <= 1e-9) continue;
						double po = -Math.Max(0, kS - ST) + Math.Max(0, kL - ST);
						outp.Add(new Tr(dTr, (cr + po) / ml, NetDelta * S / ml, st, rt, false));
					}
				}
				return outp;
			}

			var spreadAll = Build(false);
			var withCall = Build(true);

			Console.WriteLine($"\n===== {symbol}: 0.50-DELTA LONG CALL IN ST BULL vs THE PUT SPREAD ({From:yyyy-MM}+) =====");
			Console.WriteLine("ST Bear skipped throughout. 'risk %' = the fraction of bankroll lost on a FULL loss --");
			Console.WriteLine("~7% of spread trades, but every call that expires OTM. Read impDelta for the real exposure.");

			void Table(string title, Func<Tr, bool> keep)
			{
				Console.WriteLine($"\n{title}");
				Console.WriteLine($"{"arm",34} {"risk",6} {"n",6} {"impDelta%",10} {"mean/tr%",10} {"win%",7} " +
					$"{"IR",7} {"maxDD%",8} {"CAGR%",10}");
				foreach (double rk in new[] { 0.05, 0.10 })
				{
					Show("put spread [shipped]", spreadAll.Where(keep).ToList(), rk);
					Show("call in ST Bull", withCall.Where(keep).ToList(), rk);
				}
			}
			void Show(string lbl, List<Tr> t, double rk)
			{
				if (t.Count < 25) { Console.WriteLine($"{lbl,34} {100 * rk,5:0.#}% {t.Count,6}  (too few)"); return; }
				var r = t.Select(x => rk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (t.Last().D - t.First().D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,34} {100 * rk,5:0.#}% {t.Count,6} {100 * rk * t.Average(x => x.ImpDelta),10:0.0} " +
					$"{100 * m,10:+0.0000;-0.0000} {100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),7:0.000} " +
					$"{dd,8:0.00} {(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0}");
			}

			Table("--- WHOLE BOOK (gate: ratio <= 1) ---", x => x.Ratio <= 1.0);
			Table("--- WHOLE BOOK (no gate) ---", _ => true);
			Table("--- ST BULL DAYS ONLY, head to head (no gate) ---", x => x.St == ShortTermState.Bull);
			Table("--- ST BULL, put-heavy tape only (ratio > 1) ---",
				x => x.St == ShortTermState.Bull && x.Ratio > 1.0);
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
		private static double StrikeForPutDelta(double S, double iv, double T, double mag)
		{
			double lo = S * 0.05, hi = S * 3.0;
			for (int i = 0; i < 80; i++) { double mid = 0.5 * (lo + hi); if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
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
	}
}
