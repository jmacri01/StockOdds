using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ================================================================================================
	// REAL CHAINS HAVE DISCRETE STRIKES. Every result in this project prices the EXACT 0.35/0.15 delta
	// strikes, which do not exist on a real board. In practice the trader picks the listed strike
	// nearest the target and PREFERS THE ONE UNDER -- for puts, delta falls as the strike falls, so
	// "prefer under" means both legs land FURTHER OTM than the model assumes.
	//
	// This is not a rounding detail for 0DTE. At a $1 strike grid the 0.35d and 0.15d puts sit only a
	// few dollars apart, so shifting each leg by up to a full increment is a LARGE relative change to
	// the width -- and width is the denominator of the whole return-per-unit-risk calculation. It can
	// also collapse the spread outright when both legs snap to the same or adjacent strikes, which is
	// counted here rather than silently dropped.
	//
	// Conventions compared, all priced on the SAME sessions with the SAME IV so the difference is
	// purely the strike grid:
	//   EXACT   - the continuous-delta ideal every earlier result used
	//   FLOOR   - snap both legs DOWN (the convention actually being traded)
	//   NEAREST - snap both legs to the closest listed strike
	//   MIXED   - short down / long up, which WIDENS the spread and is the conservative alternative
	// ================================================================================================
	internal static class DiscreteStrikes
	{
		public static double VolRiskPremium = 1.10;
		public static int HvWindow = 20;
		public static double ShortDelta = 0.35, LongDelta = 0.15;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static bool SkipStBear = true;
		// Standard listed increments. SPY/QQQ/IWM/GLD all carry $1 strikes on 0DTE-eligible expiries.
		public static Dictionary<string, double> Increment = new()
			{ ["SPY"] = 1.0, ["QQQ"] = 1.0, ["IWM"] = 1.0, ["GLD"] = 1.0 };
		public static string[] Symbols = { "SPY", "QQQ", "IWM", "GLD" };
		// DAILY-EXPIRY ERA ONLY. Before 2022-11 the calendar admits monthly third-Fridays, and back in
		// 2005 SPY traded near $70 where a $1 grid is a ~10x coarser RELATIVE step than at $650 today.
		// Mixing those in would badly misstate how much discretisation costs the spread now.
		public static DateTime From = new DateTime(2022, 11, 1);

		private sealed record Sess(string Sym, DateTime D, double S, double ST, double Iv, double Inc);

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

		private static double PutDelta(double s, double k, double v, double t)
		{
			if (t <= 0 || v <= 0) return s < k ? 1.0 : 0.0;
			double d1 = (Math.Log(s / k) + 0.5 * v * v * t) / (v * Math.Sqrt(t));
			return NormCdf(-d1);       // magnitude
		}

		private static double StrikeForPutDelta(double s, double v, double t, double delta)
		{
			double lo = s * 0.5, hi = s * 1.5;
			for (int i = 0; i < 80; i++)
			{
				double mid = 0.5 * (lo + hi);
				if (PutDelta(s, mid, v, t) < delta) lo = mid; else hi = mid;
			}
			return 0.5 * (lo + hi);
		}

		private sealed record Fill(double R, double DS, double DL, double Width, bool Collapsed);

		// Price one convention. Returns null only if the spread cannot exist at all.
		private static Fill? Price(Sess x, string mode)
		{
			double T = 1.0 / 252.0;
			double kSe = StrikeForPutDelta(x.S, x.Iv, T, ShortDelta);
			double kLe = StrikeForPutDelta(x.S, x.Iv, T, LongDelta);
			double kS, kL;
			switch (mode)
			{
				case "EXACT": kS = kSe; kL = kLe; break;
				// puts: lower strike = lower delta, so "prefer under" on delta = floor the strike
				case "FLOOR": kS = Math.Floor(kSe / x.Inc) * x.Inc; kL = Math.Floor(kLe / x.Inc) * x.Inc; break;
				case "NEAREST": kS = Math.Round(kSe / x.Inc) * x.Inc; kL = Math.Round(kLe / x.Inc) * x.Inc; break;
				// The short leg is the HIGHER strike, so widening means rounding the short UP and the long
				// DOWN. That maximises width (never collapses) but puts the short delta slightly ABOVE
				// target -- the opposite of "prefer the one under", included as the other extreme.
				case "WIDE": kS = Math.Ceiling(kSe / x.Inc) * x.Inc; kL = Math.Floor(kLe / x.Inc) * x.Inc; break;
				default: return null;
			}
			// Only the DISCRETE conventions can collapse. EXACT is continuous, so a narrow width is a
			// legitimate spread there -- treating it as collapsed would silently compare different samples.
			bool collapsed = mode != "EXACT" && kS - kL < x.Inc - 1e-9;
			if (collapsed) return new Fill(0, 0, 0, Math.Max(0, kS - kL), true);
			double cr = Put(x.S, kS, x.Iv, T) - Put(x.S, kL, x.Iv, T);
			double ml = (kS - kL) - cr;
			if (cr <= 1e-9 || ml <= 1e-9) return new Fill(0, 0, 0, kS - kL, true);
			double po = -Math.Max(0, kS - x.ST) + Math.Max(0, kL - x.ST);
			return new Fill((cr + po) / ml, PutDelta(x.S, kS, x.Iv, T), PutDelta(x.S, kL, x.Iv, T), kS - kL, false);
		}

		public static async Task Run()
		{
			var sess = new List<Sess>();
			foreach (var symbol in Symbols)
			{
				FiveperecentBandTest.UseCalendar(symbol);
				var daily = await YahooClient.GetBarsAsync(symbol, "1d", 21);
				var eng = BankrollSimulator.Run(daily, 10_000.0);
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
				for (int i = 1; i + 1 < daily.Count; i++)
				{
					var dSig = daily[i].Date.Date; var dTr = daily[i + 1].Date.Date;
					if (!hv.TryGetValue(dSig, out double h)) continue;
					if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
					if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
					if (SkipStBear && stm.TryGetValue(dSig, out var st) && st == ShortTermState.Bear) continue;
					if (dTr < From) continue;
					double S = daily[i + 1].Open, ST = daily[i + 1].Close;
					if (S <= 0 || ST <= 0) continue;
					sess.Add(new Sess(symbol, dTr, S, ST, h * VolRiskPremium, Increment.GetValueOrDefault(symbol, 1.0)));
				}
			}
			if (sess.Count == 0) { Console.WriteLine("no data"); return; }

			Console.WriteLine($"\n===== DISCRETE STRIKES: what the real board does to the shipped spread =====");
			Console.WriteLine($"{sess.Count} shipped-qualifying sessions across {sess.Select(x => x.Sym).Distinct().Count()} instruments, " +
				$"{sess.Min(x => x.D):yyyy-MM-dd} -> {sess.Max(x => x.D):yyyy-MM-dd}");
			Console.WriteLine($"target {ShortDelta:0.00}/{LongDelta:0.00}; $1 strike grid; " +
				$"FLOOR = 'prefer the one under', the convention being traded");

			var modes = new[] { "EXACT", "FLOOR", "NEAREST", "WIDE" };
			var fills = modes.ToDictionary(m => m, m => sess.Select(x => Price(x, m)).ToList());

			Console.WriteLine($"\n{"convention",-12} {"n live",8} {"collapsed",10} {"avg shortD",11} {"avg longD",10} " +
				$"{"avg width$",11} {"mean%",10} {"win%",7} {"IR",8}");
			foreach (var m in modes)
			{
				var f = fills[m];
				var live = Enumerable.Range(0, sess.Count).Where(i => f[i] != null && !f[i]!.Collapsed).ToList();
				if (live.Count < 10) { Console.WriteLine($"{m,-12} {live.Count,8}   (too few)"); continue; }
				var r = live.Select(i => Risk * f[i]!.R).ToList();
				double mn = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - mn) * (z - mn)) / (r.Count - 1));
				Console.WriteLine($"{m,-12} {live.Count,8} {sess.Count - live.Count,10} " +
					$"{live.Average(i => f[i]!.DS),11:0.000} {live.Average(i => f[i]!.DL),10:0.000} " +
					$"{live.Average(i => f[i]!.Width),11:0.00} {100 * mn,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 1e-12 ? mn / sd : 0),8:0.000}");
			}

			// PAIRED against EXACT on the sessions where BOTH conventions produce a live spread, so the
			// difference is the strike grid and not a change of sample.
			Console.WriteLine($"\n-- paired vs EXACT, on sessions where both are live --");
			foreach (var m in modes.Where(m => m != "EXACT"))
			{
				var d = new List<double>();
				for (int i = 0; i < sess.Count; i++)
				{
					var a = fills[m][i]; var b = fills["EXACT"][i];
					if (a == null || b == null || a.Collapsed || b.Collapsed) continue;
					d.Add(Risk * (a.R - b.R));
				}
				if (d.Count < 10) { Console.WriteLine($"  {m,-10} too few"); continue; }
				double mn = d.Average();
				double sd = Math.Sqrt(d.Sum(z => (z - mn) * (z - mn)) / (d.Count - 1));
				Console.WriteLine($"  {m,-10} n={d.Count,5}  diff {100 * mn,9:+0.0000;-0.0000}pp  " +
					$"t {mn / (sd / Math.Sqrt(d.Count)),7:+0.00;-0.00}  " +
					$"worse on {100.0 * d.Count(z => z < 0) / d.Count,5:0.0}% of sessions");
			}

			Console.WriteLine($"\n-- per-instrument, FLOOR vs EXACT (mean% and realised deltas) --");
			foreach (var sym in Symbols)
			{
				var idx = Enumerable.Range(0, sess.Count).Where(i => sess[i].Sym == sym
					&& fills["FLOOR"][i] is { Collapsed: false } && fills["EXACT"][i] is { Collapsed: false }).ToList();
				if (idx.Count < 20) { Console.WriteLine($"   {sym,-5} too few"); continue; }
				double me = idx.Average(i => Risk * fills["EXACT"][i]!.R);
				double mf = idx.Average(i => Risk * fills["FLOOR"][i]!.R);
				Console.WriteLine($"   {sym,-5} n={idx.Count,4}  EXACT {100 * me,8:+0.0000;-0.0000}  FLOOR {100 * mf,8:+0.0000;-0.0000}  " +
					$"delta {idx.Average(i => fills["FLOOR"][i]!.DS):0.000}/{idx.Average(i => fills["FLOOR"][i]!.DL):0.000}  " +
					$"width ${idx.Average(i => fills["FLOOR"][i]!.Width):0.00} vs ${idx.Average(i => fills["EXACT"][i]!.Width):0.00}");
			}

			// Collapse risk is the practical hazard: a narrow 0DTE spread can snap to a single increment.
			Console.WriteLine($"\n-- how often does FLOOR collapse the spread (legs < 1 strike apart)? --");
			foreach (var sym in Symbols)
			{
				var idx = Enumerable.Range(0, sess.Count).Where(i => sess[i].Sym == sym).ToList();
				if (idx.Count == 0) continue;
				int col = idx.Count(i => fills["FLOOR"][i]?.Collapsed ?? true);
				var widths = idx.Where(i => fills["EXACT"][i] is { Collapsed: false })
					.Select(i => fills["EXACT"][i]!.Width).OrderBy(v => v).ToList();
				if (widths.Count == 0) continue;
				Console.WriteLine($"   {sym,-5} collapsed {100.0 * col / idx.Count,5:0.0}% of {idx.Count,4} sessions;  " +
					$"exact width p10 ${widths[widths.Count / 10]:0.00}, median ${widths[widths.Count / 2]:0.00}, p90 ${widths[9 * widths.Count / 10]:0.00}");
			}
		}
	}
}
