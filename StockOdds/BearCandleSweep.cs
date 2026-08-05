using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Skip the KAMA smoothing ramp only on BEAR CANDLES -- a decision candle closing below the PRIOR candle's low.
	// A single decisive down bar rather than a state, so it fires far less often than ST Bear and the firing rate
	// is reported alongside everything else.
	//
	// Every column the previous rounds established as load-bearing, in one table:
	//   dShp / repl   broad OOS mean Sharpe change and 4-sample sign replication
	//   excess        vs a flat haircut matched per name to the config's own exposure (analytic)
	//   dRet          mean per-name compounded return given up
	//   breakeven     return per unit-exposure-bar the freed capital must earn to match shipped, vs shipEff
	// Arms: mode 1 (skip on bear candles) and mode 2 (the inverse control -- smooth ONLY on bear candles).
	public static class BearCandleSweep
	{
		public static double OosFraction = 0.30;
		public static int    Samples     = 4;

		private static uint Fnv(string s) { uint h = 2166136261; foreach (char c in s) { h ^= c; h *= 16777619; } return h; }

		public static async Task Run(string interval)
		{
			var uni = await Universe.BuildAsync();
			await BarCache.PrimeAsync(uni.Select(u => u.Symbol), interval);
			var bars = BarCache.LoadAll(uni.Select(u => u.Symbol));
			var syms = uni.Where(u => bars.ContainsKey(u.Symbol) && bars[u.Symbol].Count > 0
					&& u.Shares * bars[u.Symbol][^1].Close >= Universe.MinMarketCap)
				.Select(u => u.Symbol).ToList();
			var sample = syms.ToDictionary(s => s, s => (int)(Fnv(s) % (uint)Samples));

			// underlying returns by date, for the capital-efficiency terms
			var rMap = new Dictionary<string, Dictionary<DateTime, double>>();
			foreach (var s in syms)
			{
				var b = bars[s]; var m = new Dictionary<DateTime, double>();
				for (int i = 1; i < b.Count; i++) if (b[i - 1].Close > 0) m[b[i].Date] = (b[i].Close - b[i - 1].Close) / b[i - 1].Close;
				rMap[s] = m;
			}

			int savedMode = BankrollSimulator.KamaBearCandleMode;
			try
			{
				Dictionary<string, BankrollResult> Go(int mode)
				{
					BankrollSimulator.KamaBearCandleMode = mode;
					var o = new System.Collections.Concurrent.ConcurrentDictionary<string, BankrollResult>();
					Parallel.ForEach(syms, s => { try { o[s] = BankrollSimulator.Run(bars[s], 10_000.0); } catch { } });
					return o.ToDictionary(kv => kv.Key, kv => kv.Value);
				}

				var A = Go(0);
				double fireRate = 100.0 * A.Values.Sum(r => (double)r.BearCandleBars) / A.Values.Sum(r => (double)r.Positions.Count);
				Console.WriteLine($"\n===== SKIP KAMA SMOOTHING ON BEAR CANDLES (close < prior low) =====");
				Console.WriteLine($"{syms.Count} names, >=${Universe.MinMarketCap / 1e6:0}M | bear candles fire on {fireRate:0.0}% of bars " +
					$"(ST Bear, for scale, is ~30%)");

				foreach (int mode in new[] { 1, 2 })
				{
					var B = Go(mode);
					Console.WriteLine($"\n--- mode {mode}: {(mode == 1 ? "skip the ramp ON bear candles" : "INVERSE CONTROL - smooth ONLY on bear candles")} ---");
					Report(syms, sample, bars, rMap, A, B);
				}
			}
			finally { BankrollSimulator.KamaBearCandleMode = savedMode; }
		}

		private static void Report(List<string> syms, Dictionary<string, int> sample, Dictionary<string, List<OhlcBar>> bars,
			Dictionary<string, Dictionary<DateTime, double>> rMap,
			Dictionary<string, BankrollResult> A, Dictionary<string, BankrollResult> B)
		{
			double[] edges = { 0, 30, 50, 75, 100, double.MaxValue };
			string[] names = { "<30", "30-50", "50-75", "75-100", "100+" };

			var rows = new List<(int Band, int Sample, double aShp, double bShp, double flatRpd, double bRpd,
				double aRet, double bRet, double aDd, double bDd, double aPnl, double bPnl, double aExp, double bExp,
				double aTit, double bTit, int Bars)>();

			foreach (var s in syms)
			{
				if (!A.TryGetValue(s, out var a) || !B.TryGetValue(s, out var b)) continue;
				int n = Math.Min(a.Positions.Count, b.Positions.Count), st = (int)(n * (1.0 - OosFraction));
				if (n - st < 120) continue;
				double hv = Volatility.AnnualizedHistoricalPct(bars[s]);
				int band = 0; while (band < edges.Length - 2 && hv >= edges[band + 1]) band++;

				var ar = new List<double>(); var br = new List<double>();
				double aPnl = 0, bPnl = 0, aExp = 0, bExp = 0, aTit = 0, bTit = 0; int cnt = 0;
				for (int i = st; i < n; i++)
				{
					if (!rMap[s].TryGetValue(a.ReturnDates[i], out double r)) continue;
					double pa = a.Positions[i], pb = b.Positions[i];
					ar.Add(pa * r); br.Add(pb * r);
					aPnl += pa * r * 100; bPnl += pb * r * 100;
					aExp += Math.Abs(pa); bExp += Math.Abs(pb);
					if (Math.Abs(pa) > 0.05) aTit++;
					if (Math.Abs(pb) > 0.05) bTit++;
					cnt++;
				}
				if (cnt < 120) continue;
				double h = aExp > 1e-9 ? bExp / aExp : 1.0;
				rows.Add((band, sample[s], Shp(ar), Shp(br), Rpd(ar.Select(x => x * h).ToList()), Rpd(br),
					Cmp(ar), Cmp(br), Dd(ar), Dd(br), aPnl, bPnl, aExp, bExp, aTit, bTit, cnt));
			}

			Console.WriteLine($"{"band",8} {"names",6} {"dShp",8} {"repl",5} {"shpW%",6} {"excess",8} {"repl",5} " +
				$"{"medExc",8} {"excW%",6} {"dRet",7} {"TiT",11} {"freed",6} {"breakeven",9}");

			for (int b = 0; b <= names.Length; b++)
			{
				bool tot = b == names.Length;
				var g = rows.Where(r => tot || r.Band == b).ToList();
				if (g.Count < 20) continue;

				double dShp = g.Average(r => r.bShp - r.aShp);
				double exc  = g.Average(r => r.bRpd - r.flatRpd);
				int shpRepl = 0, excRepl = 0;
				for (int s = 0; s < Samples; s++)
				{
					var gs = g.Where(r => r.Sample == s).ToList();
					if (gs.Count == 0) continue;
					if (Math.Sign(gs.Average(r => r.bShp - r.aShp)) == Math.Sign(dShp)) shpRepl++;
					if (Math.Sign(gs.Average(r => r.bRpd - r.flatRpd)) == Math.Sign(exc)) excRepl++;
				}
				double aP = g.Sum(r => r.aPnl), bP = g.Sum(r => r.bPnl);
				double aE = g.Sum(r => r.aExp), bE = g.Sum(r => r.bExp);
				double barsTot = g.Sum(r => (double)r.Bars);
				double shipEff = aE > 0 ? aP / aE : 0;
				double freed = aE - bE;
				double breakeven = freed > 1e-9 ? (aP - bP) / freed : double.NaN;

				// BREADTH, not just the mean: the share of names where the config actually beats its own
				// matched-exposure control. A large mean excess carried by a handful of compounders reads
				// identically to a broad one until this column is printed.
				double excW = 100.0 * g.Count(r => r.bRpd > r.flatRpd) / g.Count;
				double shpW = 100.0 * g.Count(r => r.bShp > r.aShp) / g.Count;
				double medExc = Median(g.Select(r => r.bRpd - r.flatRpd).ToList());

				Console.WriteLine($"{(tot ? "TOTAL" : names[b]),8} {g.Count,6} {dShp,8:+0.000;-0.000} {shpRepl + "/4",5} " +
					$"{shpW,6:0.0} {exc,8:+0.000;-0.000} {excRepl + "/4",5} {medExc,8:+0.000;-0.000} {excW,6:0.0} " +
					$"{g.Average(r => r.bRet - r.aRet),7:+0.0;-0.0} " +
					$"{$"{100.0 * g.Sum(r => r.aTit) / barsTot:0.0}->{100.0 * g.Sum(r => r.bTit) / barsTot:0.0}",11} " +
					$"{100.0 * freed / aE,6:0.0} {breakeven,9:0.0000}");
			}
		}

		// The basket gate: 18 names, full history. Several layers won broad and were removed anyway for hurting
		// this set, so nothing ships until it has been looked at here.
		public static async Task Basket(string interval, int mode)
		{
			string[] syms = { "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr",
				"smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };
			var bars = new Dictionary<string, List<OhlcBar>>();
			foreach (var s in syms)
			{
				var b = BarCache.Load(s) ?? await YahooClient.GetBarsAsync(s, interval);
				b = b.Where(x => x.Date >= new DateTime(2020, 1, 1)).ToList();
				if (b.Count >= 120) bars[s] = b;
			}
			int saved = BankrollSimulator.KamaBearCandleMode;
			try
			{
				List<BankrollResult> Go(int m)
				{
					BankrollSimulator.KamaBearCandleMode = m;
					return bars.OrderBy(kv => kv.Key).Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
				}
				var A = Go(0); var B = Go(mode);
				var ks = bars.Keys.OrderBy(k => k).ToList();

				Console.WriteLine($"\n===== BASKET GATE ({ks.Count} names, full history): KamaBearCandleMode = {mode} =====");
				Console.WriteLine($"{"config",9} {"Sharpe",8} {"MedShp",8} {"Ret%",10} {"MedRet%",9} {"DD%",8} {"r/dd",8} {"medR/dd",8} {"TiT%",6} {"exp",6}");
				void Row(string l, List<BankrollResult> r) =>
					Console.WriteLine($"{l,9} {r.Average(x => x.SharpeRatio),8:0.000} {Median(r.Select(x => x.SharpeRatio).ToList()),8:0.000} " +
						$"{r.Average(x => x.TotalReturnPct),10:0.0} {Median(r.Select(x => x.TotalReturnPct).ToList()),9:0.0} " +
						$"{r.Average(x => x.MaxDrawdownPct),8:0.00} " +
						$"{r.Average(x => x.MaxDrawdownPct > 0.01 ? x.TotalReturnPct / x.MaxDrawdownPct : 0),8:0.000} " +
						$"{Median(r.Select(x => x.MaxDrawdownPct > 0.01 ? x.TotalReturnPct / x.MaxDrawdownPct : 0).ToList()),8:0.000} " +
						$"{r.Average(x => 100.0 * x.Positions.Count(p => Math.Abs(p) > 0.05) / Math.Max(1, x.Positions.Count)),6:0.0} " +
						$"{r.Average(x => x.Positions.Average(Math.Abs)),6:0.000}");
				Row("shipped", A); Row($"mode{mode}", B);
				Console.WriteLine($"  Sharpe better on {B.Where((x, i) => x.SharpeRatio > A[i].SharpeRatio).Count()}/{B.Count} | " +
					$"return better on {B.Where((x, i) => x.TotalReturnPct > A[i].TotalReturnPct).Count()}/{B.Count} | " +
					$"drawdown better on {B.Where((x, i) => x.MaxDrawdownPct < A[i].MaxDrawdownPct).Count()}/{B.Count}");
				for (int i = 0; i < ks.Count; i++)
					Console.WriteLine($"      {ks[i],6} shp {A[i].SharpeRatio,7:0.000} -> {B[i].SharpeRatio,7:0.000}   " +
						$"ret {A[i].TotalReturnPct,8:0.0} -> {B[i].TotalReturnPct,8:0.0}   dd {A[i].MaxDrawdownPct,6:0.0} -> {B[i].MaxDrawdownPct,6:0.0}");
			}
			finally { BankrollSimulator.KamaBearCandleMode = saved; }
		}

		private static double Median(List<double> xs)
		{
			if (xs.Count == 0) return 0;
			var s = xs.OrderBy(x => x).ToList();
			int m = s.Count / 2;
			return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
		}

		private static double Shp(List<double> r)
		{
			if (r.Count < 2) return 0;
			double m = r.Average(), v = r.Sum(x => (x - m) * (x - m)) / (r.Count - 1), sd = Math.Sqrt(v);
			double s = sd > 0 ? m / sd * Math.Sqrt(252.0) : 0;
			return double.IsNaN(s) || double.IsInfinity(s) ? 0 : s;
		}
		private static double Cmp(List<double> r) { double e = 1; foreach (var x in r) e *= 1 + x; return (e - 1) * 100; }
		private static double Dd(List<double> r)
		{ double e = 1, p = 1, d = 0; foreach (var x in r) { e *= 1 + x; if (e > p) p = e; double q = (p - e) / p; if (q > d) d = q; } return d * 100; }
		private static double Rpd(List<double> r) { double dd = Dd(r); return dd > 0.01 ? Cmp(r) / dd : 0; }
	}
}
