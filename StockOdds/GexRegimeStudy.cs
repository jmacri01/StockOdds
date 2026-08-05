using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Does the shipped engine behave differently by DEALER GAMMA REGIME?
	//
	// GEX is a market-wide daily scalar, so it conditions every name at once. The right unit of observation is
	// therefore a DATE, not a name-bar: pooling 2,500 name-bars per day would be pseudo-replication (they all
	// share the same GEX reading and most of the same market move). So this builds an EQUAL-WEIGHT daily series
	// -- mean strategy return across names for each date, and mean buy-&-hold return -- and then buckets DATES
	// by that day's GEX.
	//
	// Sign convention note: negative GEX in SqueezeMetrics' model is the "dealers short gamma" state, usually
	// associated with trend-amplifying flow and higher realised vol. The question is whether the engine's edge
	// (which is a de-risking overlay) concentrates there.
	public static class GexRegimeStudy
	{
		public static async Task Run(string interval)
		{
			var gex = await GexClient.ByDateAsync();
			Console.WriteLine($"GEX series: {gex.Count} days, {gex.Keys.Min():yyyy-MM-dd} -> {gex.Keys.Max():yyyy-MM-dd}");

			var uni = await Universe.BuildAsync();
			var bars = BarCache.LoadAll(uni.Select(u => u.Symbol));
			var syms = uni.Where(u => bars.ContainsKey(u.Symbol) && bars[u.Symbol].Count > 0
					&& u.Shares * bars[u.Symbol][^1].Close >= Universe.MinMarketCap)
				.Select(u => u.Symbol).ToList();
			Console.WriteLine($"Universe: {syms.Count} names");

			// per-date accumulators for an equal-weight book
			var stratSum = new Dictionary<DateTime, double>();
			var bhSum = new Dictionary<DateTime, double>();
			var expSum = new Dictionary<DateTime, double>();
			var cnt = new Dictionary<DateTime, int>();

			var results = new System.Collections.Concurrent.ConcurrentBag<(DateTime D, double S, double B, double E)>();
			Parallel.ForEach(syms, sym =>
			{
				try
				{
					var b = bars[sym];
					var r = BankrollSimulator.Run(b, 10_000.0);
					var closeBy = new Dictionary<DateTime, double>();
					for (int i = 0; i < b.Count; i++) closeBy[b[i].Date] = b[i].Close;
					var under = new Dictionary<DateTime, double>();
					for (int i = 1; i < b.Count; i++) if (b[i - 1].Close > 0) under[b[i].Date] = (b[i].Close - b[i - 1].Close) / b[i - 1].Close;

					for (int k = 0; k < r.StratReturns.Count && k < r.ReturnDates.Count; k++)
					{
						var d = r.ReturnDates[k].Date;
						if (!under.TryGetValue(r.ReturnDates[k], out double u)) continue;
						results.Add((d, r.StratReturns[k], u, Math.Abs(r.Positions[k])));
					}
				}
				catch { }
			});

			foreach (var (d, s, bb, e) in results)
			{
				stratSum[d] = stratSum.GetValueOrDefault(d) + s;
				bhSum[d] = bhSum.GetValueOrDefault(d) + bb;
				expSum[d] = expSum.GetValueOrDefault(d) + e;
				cnt[d] = cnt.GetValueOrDefault(d) + 1;
			}

			// keep dates with a real cross-section AND a GEX reading
			var days = cnt.Where(kv => kv.Value >= 100 && gex.ContainsKey(kv.Key))
				.Select(kv => kv.Key).OrderBy(d => d).ToList();
			Console.WriteLine($"Dates with >=100 names and a GEX reading: {days.Count} " +
				$"({days.First():yyyy-MM-dd} -> {days.Last():yyyy-MM-dd})\n");

			var rows = days.Select(d => (
				Date: d,
				Strat: stratSum[d] / cnt[d],
				Bh: bhSum[d] / cnt[d],
				Exp: expSum[d] / cnt[d],
				Gex: gex[d].Gex,
				Dix: gex[d].Dix)).ToList();

			// buckets: negative gamma on its own, then quartiles of the positive days
			var pos = rows.Where(r => r.Gex >= 0).Select(r => r.Gex).OrderBy(x => x).ToList();
			double q1 = pos[(int)(pos.Count * 0.25)], q2 = pos[(int)(pos.Count * 0.50)], q3 = pos[(int)(pos.Count * 0.75)];

			(string Label, Func<double, bool> P)[] buckets =
			{
				("GEX < 0",        g => g < 0),
				($"0..{q1/1e9:0.0}B",   g => g >= 0 && g < q1),
				($"{q1/1e9:0.0}..{q2/1e9:0.0}B", g => g >= q1 && g < q2),
				($"{q2/1e9:0.0}..{q3/1e9:0.0}B", g => g >= q2 && g < q3),
				($">{q3/1e9:0.0}B",     g => g >= q3),
			};

			Console.WriteLine("===== SHIPPED ENGINE BY DEALER-GAMMA REGIME (equal-weight book, one obs per DATE) =====");
			Console.WriteLine($"{"regime",14} {"days",6} {"stratRet%",10} {"bhRet%",9} {"stratShp",9} {"bhShp",8} " +
				$"{"dShp",8} {"exp",6} {"stratDD%",9} {"bhDD%",8}");

			foreach (var (label, pred) in buckets)
			{
				var g = rows.Where(r => pred(r.Gex)).ToList();
				if (g.Count < 20) { Console.WriteLine($"{label,14} {g.Count,6}  (too few)"); continue; }
				var s = g.Select(x => x.Strat).ToList(); var b = g.Select(x => x.Bh).ToList();
				Console.WriteLine($"{label,14} {g.Count,6} {Cmp(s),10:0.0} {Cmp(b),9:0.0} {Shp(s),9:0.000} {Shp(b),8:0.000} " +
					$"{Shp(s) - Shp(b),8:+0.000;-0.000} {g.Average(x => x.Exp),6:0.000} {Dd(s),9:0.00} {Dd(b),8:0.00}");
			}

			var all = rows.Select(x => x.Strat).ToList(); var allB = rows.Select(x => x.Bh).ToList();
			Console.WriteLine($"{"ALL",14} {rows.Count,6} {Cmp(all),10:0.0} {Cmp(allB),9:0.0} {Shp(all),9:0.000} {Shp(allB),8:0.000} " +
				$"{Shp(all) - Shp(allB),8:+0.000;-0.000} {rows.Average(x => x.Exp),6:0.000} {Dd(all),9:0.00} {Dd(allB),8:0.00}");

			// NEXT-DAY test: GEX is known at the close, so acting on it means conditioning TOMORROW's exposure.
			// Same-day buckets above are descriptive only -- they use information from the day being scored.
			Console.WriteLine("\n===== TRADEABLE VERSION: bucket by the PRIOR day's GEX =====");
			Console.WriteLine($"{"regime(t-1)",14} {"days",6} {"stratRet%",10} {"bhRet%",9} {"stratShp",9} {"bhShp",8} {"dShp",8}");
			for (int i = 0; i < buckets.Length; i++)
			{
				var g = new List<(double S, double B)>();
				for (int k = 1; k < rows.Count; k++)
					if (buckets[i].P(rows[k - 1].Gex)) g.Add((rows[k].Strat, rows[k].Bh));
				if (g.Count < 20) { Console.WriteLine($"{buckets[i].Label,14} {g.Count,6}  (too few)"); continue; }
				var s = g.Select(x => x.S).ToList(); var b = g.Select(x => x.B).ToList();
				Console.WriteLine($"{buckets[i].Label,14} {g.Count,6} {Cmp(s),10:0.0} {Cmp(b),9:0.0} {Shp(s),9:0.000} {Shp(b),8:0.000} " +
					$"{Shp(s) - Shp(b),8:+0.000;-0.000}");
			}

			// ================= THE CONTROL THAT DECIDES IT =================
			// Negative-GEX days ARE big down days, and the engine is most de-levered right after a big down day,
			// which is also when the market most often bounces. So "GEX(t-1) < 0" may be nothing more than
			// "yesterday fell a lot" wearing an options-flow costume. Condition on the PRIOR DAY'S MARKET RETURN
			// instead and compare: if the dShp pattern is the same, GEX adds nothing.
			Console.WriteLine("\n===== CONTROL: bucket by the PRIOR day's MARKET RETURN instead of GEX =====");
			var priorRet = new List<(double Prior, double S, double B, double G)>();
			for (int k = 1; k < rows.Count; k++) priorRet.Add((rows[k - 1].Bh, rows[k].Strat, rows[k].Bh, rows[k - 1].Gex));
			var sortedPrior = priorRet.Select(x => x.Prior).OrderBy(x => x).ToList();
			double p10 = sortedPrior[(int)(sortedPrior.Count * 0.10)];
			double p25 = sortedPrior[(int)(sortedPrior.Count * 0.25)];
			double p75 = sortedPrior[(int)(sortedPrior.Count * 0.75)];
			double p90 = sortedPrior[(int)(sortedPrior.Count * 0.90)];
			(string L, Func<double, bool> P)[] pb =
			{
				($"< {p10*100:0.0}% (p10)", x => x < p10),
				($"{p10*100:0.0}..{p25*100:0.0}%", x => x >= p10 && x < p25),
				("middle 50%",             x => x >= p25 && x <= p75),
				($"{p75*100:0.0}..{p90*100:0.0}%", x => x > p75 && x <= p90),
				($"> {p90*100:0.0}% (p90)", x => x > p90),
			};
			Console.WriteLine($"{"priorRet(t-1)",16} {"days",6} {"stratShp",9} {"bhShp",8} {"dShp",8} {"negGexShare%",13}");
			foreach (var (L, P) in pb)
			{
				var g = priorRet.Where(x => P(x.Prior)).ToList();
				if (g.Count < 20) continue;
				var sList = g.Select(x => x.S).ToList(); var bList = g.Select(x => x.B).ToList();
				Console.WriteLine($"{L,16} {g.Count,6} {Shp(sList),9:0.000} {Shp(bList),8:0.000} " +
					$"{Shp(sList) - Shp(bList),8:+0.000;-0.000} {100.0 * g.Count(x => x.G < 0) / g.Count,13:0.0}");
			}

			// and the 2x2: does GEX still separate WITHIN a prior-return bucket?
			Console.WriteLine("\n2x2 -- does GEX add anything once prior-day return is held roughly fixed?");
			Console.WriteLine($"{"priorRet",16} {"GEX(t-1)",10} {"days",6} {"stratShp",9} {"bhShp",8} {"dShp",8}");
			foreach (var (L, P) in new[] { ($"< {p25*100:0.0}% (down)", (Func<double,bool>)(x => x < p25)),
										   ($">= {p25*100:0.0}%", x => x >= p25) })
				foreach (var (gl, gp) in new[] { ("< 0", (Func<double,bool>)(g => g < 0)), (">= 0", g => g >= 0) })
				{
					var g2 = priorRet.Where(x => P(x.Prior) && gp(x.G)).ToList();
					if (g2.Count < 20) { Console.WriteLine($"{L,16} {gl,10} {g2.Count,6}  (too few)"); continue; }
					var sL = g2.Select(x => x.S).ToList(); var bL = g2.Select(x => x.B).ToList();
					Console.WriteLine($"{L,16} {gl,10} {g2.Count,6} {Shp(sL),9:0.000} {Shp(bL),8:0.000} {Shp(sL) - Shp(bL),8:+0.000;-0.000}");
				}
		}

		private static double Cmp(List<double> r) { double e = 1; foreach (var x in r) e *= 1 + x; return (e - 1) * 100; }
		private static double Dd(List<double> r)
		{ double e = 1, p = 1, d = 0; foreach (var x in r) { e *= 1 + x; if (e > p) p = e; double q = (p - e) / p; if (q > d) d = q; } return d * 100; }
		private static double Shp(List<double> r)
		{
			if (r.Count < 2) return 0;
			double m = r.Average(), v = r.Sum(x => (x - m) * (x - m)) / (r.Count - 1), sd = Math.Sqrt(v);
			return sd > 0 ? m / sd * Math.Sqrt(252.0) : 0;
		}
	}
}
