using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Can a STRICTER gamma gate rescue the ST Bear sessions the book currently skips?
	//
	// The delta grid said no strike pair helps -- every pair sat at IR 0.04-0.13 against the non-Bear book's 0.456.
	// But strikes and selection are different levers, and one earlier cell hinted at this: ST Bear with put/call
	// ratio <= 1 scored IR 0.249 against 0.11 for ST Bear overall. This sweeps the ratio threshold to find where,
	// if anywhere, ST Bear days become worth trading.
	//
	// SAMPLE IS THE BINDING CONSTRAINT AND IT BITES TWICE. UW gamma starts 2022-03, which already cuts ST Bear to
	// a couple of hundred sessions; each tightening of the ratio then removes most of what is left. Every row
	// prints its n, and rows under ~40 trades are reported but should not be believed -- with a search across
	// thresholds, the smallest surviving cell is exactly where a spurious winner appears.
	//
	// The decision is scored on the FULL BOOK, not on the ST Bear slice: re-admitting a state only pays if it
	// improves the portfolio it joins, and a slice with lower IR than the book dilutes even when positive.
	public static class StBearRatioSalvage
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double WingDelta = 0.15;
		public static double NetDelta = 0.20;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static DateTime From = new DateTime(2022, 3, 30);
		public static double[] Thresholds = { 1.20, 1.00, 0.90, 0.84, 0.73, 0.65 };

		private sealed record Tr(DateTime D, double R, bool IsBear, double Ratio);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(bars, 10_000.0);
			var ratio = LoadRatio();
			if (ratio.Count == 0) { Console.WriteLine("no UW gamma data"); return; }

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
				if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				if (!ratio.TryGetValue(dSig, out double rt)) continue;
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
				all.Add(new Tr(dTr, (cr + po) / ml, st == ShortTermState.Bear, rt));
			}

			var bear = all.Where(x => x.IsBear).ToList();
			var rest = all.Where(x => !x.IsBear).ToList();
			Console.WriteLine($"\n===== {symbol}: CAN A STRICTER GAMMA GATE SALVAGE ST BEAR? ({From:yyyy-MM}+) =====");
			Console.WriteLine($"{all.Count} sessions, {bear.Count} ST Bear ({100.0 * bear.Count / all.Count:0.0}%)");

			(double m, double ir, double dd, double t, int n) St(IEnumerable<Tr> src)
			{
				var r = src.Select(x => Risk * x.R).ToList();
				if (r.Count < 2) return (0, 0, 0, 0, r.Count);
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				return (m, sd > 0 ? m / sd : 0, dd, sd > 0 ? m / (sd / Math.Sqrt(r.Count)) : 0, r.Count);
			}
			var restAll = St(rest);
			Console.WriteLine($"non-Bear book (the bar to beat): IR {restAll.ir:0.000}, mean {100 * restAll.m:+0.0000;-0.0000}%, n {restAll.n}");

			Console.WriteLine($"\n--- ST BEAR sessions kept, by ratio threshold ---");
			Console.WriteLine($"{"gate",26} {"n",6} {"%ofBear",9} {"mean/tr%",10} {"IR",8} {"maxDD%",8} {"t",7}");
			var b0 = St(bear);
			Console.WriteLine($"{"no ratio gate",26} {b0.n,6} {100.0,9:0.0} {100 * b0.m,10:+0.0000;-0.0000} " +
				$"{b0.ir,8:0.000} {b0.dd,8:0.0} {b0.t,7:+0.00;-0.00}");
			foreach (double th in Thresholds)
			{
				var sub = bear.Where(x => x.Ratio < th).ToList();
				var s = St(sub);
				string flag = sub.Count < 40 ? "  << thin" : "";
				Console.WriteLine($"{$"ratio < {th:0.00}",26} {s.n,6} {100.0 * sub.Count / bear.Count,9:0.0} " +
					$"{100 * s.m,10:+0.0000;-0.0000} {s.ir,8:0.000} {s.dd,8:0.0} {s.t,7:+0.00;-0.00}{flag}");
			}

			Console.WriteLine($"\n--- FULL BOOK: does re-admitting the surviving ST Bear days help? ---");
			Console.WriteLine($"{"book",42} {"trades",7} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10}");
			void Book(string lbl, List<Tr> t)
			{
				var l = t.OrderBy(x => x.D).ToList();
				var r = l.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (l.Last().D - l.First().D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,42} {r.Count,7} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0}");
			}
			Book("skip ST Bear entirely [SHIPPED]", rest);
			Book("re-admit ALL ST Bear", all);
			foreach (double th in new[] { 1.00, 0.84, 0.73 })
				Book($"re-admit ST Bear where ratio < {th:0.00}",
					rest.Concat(bear.Where(x => x.Ratio < th)).ToList());
			Console.WriteLine("A gate that leaves only a handful of ST Bear days cannot move the book either way --");
			Console.WriteLine("check the trade counts before reading anything into the IR column.");
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
