using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Add a 15-bar drawdown to the peak-age scaler. Three shapes (see DdMicroMode): stack it as a second age
	// read on top of dd60/dd30, or substitute it for either existing window.
	//
	// The scaler is an EXPOSURE multiplier, so this is exactly the class of change where the flat-haircut control
	// has a blind spot -- it prices "holds less" but not a change in the TIMING of de-levering. So every arm is
	// scored on four things, not one:
	//   dShp / repl   broad OOS Sharpe (exposure-invariant) with 4-sample sign replication
	//   medExc/excW   excess over the matched flat haircut, MEDIAN and per-name breadth (never the mean alone)
	//   2022          the bear year on its own -- a de-levering rule that only works in a bull sample dies here
	//   basket        the 18-name gate, run separately on whatever survives
	public static class MicroDdSweep
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

			var rMap = new Dictionary<string, Dictionary<DateTime, double>>();
			foreach (var s in syms)
			{
				var b = bars[s]; var m = new Dictionary<DateTime, double>();
				for (int i = 1; i < b.Count; i++) if (b[i - 1].Close > 0) m[b[i].Date] = (b[i].Close - b[i - 1].Close) / b[i - 1].Close;
				rMap[s] = m;
			}

			int savedMode = BankrollSimulator.DdMicroMode, savedWin = BankrollSimulator.DdMicroWindow;
			double savedK = BankrollSimulator.DdMicroK;

			try
			{
				BankrollSimulator.DdMicroMode = 0;
				var A = RunAll(syms, bars);
				Console.WriteLine($"\n===== 15-BAR DRAWDOWN ADDED TO THE PEAK-AGE SCALER — BROAD OOS ({syms.Count} names) =====");
				Console.WriteLine("m1 = second stage on top of dd60/dd30 | m2 = dd60/dd15 | m3 = dd30/dd15");
				Console.WriteLine($"{"config",22} {"dShp",8} {"repl",5} {"shpW%",6} {"medExc",8} {"excW%",6} {"dRet",7} {"exp",13} {"2022dShp",9}");

				var keep = new List<(string Label, int Mode, double K, int Win, double dShp)>();

				foreach (var (mode, k, win) in Grid())
				{
					BankrollSimulator.DdMicroMode = mode; BankrollSimulator.DdMicroK = k; BankrollSimulator.DdMicroWindow = win;
					var B = RunAll(syms, bars);
					string label = mode == 1 ? $"m1 K{k,4:0.00} w{win,2:0}" : $"m{mode} w{win,2:0}";
					var r = Score(label, syms, sample, rMap, A, B);
					keep.Add((label, mode, k, win, r));
				}

				var best = keep.OrderByDescending(x => x.dShp).First();
				Console.WriteLine($"\nBest on Sharpe: {best.Label} ({best.dShp:+0.000})");
			}
			finally
			{
				BankrollSimulator.DdMicroMode = savedMode; BankrollSimulator.DdMicroWindow = savedWin;
				BankrollSimulator.DdMicroK = savedK;
			}
		}

		private static IEnumerable<(int Mode, double K, int Win)> Grid()
		{
			// K is monotone-positive up to the old grid edge (1.25), and since dd30 >= dd15 always, stage2 >= K --
			// so a K above 1 is mostly a LEVER. Extended well past the edge: if dShp keeps rising with K the knob
			// is a participation proxy, not an age read. DdRatioMax = 2.0 caps the product, so K ~ 2+ saturates.
			foreach (double k in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0 })
				yield return (1, k, 15);
		}

		// The basket gate -- the one that has killed every other candidate this session.
		public static async Task Basket(string interval, params (int Mode, double K, int Win)[] cfgs)
		{
			string[] want = { "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr",
				"smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };
			var bars = new Dictionary<string, List<OhlcBar>>();
			foreach (var s in want)
			{
				var b = BarCache.Load(s) ?? await YahooClient.GetBarsAsync(s, interval);
				b = b.Where(x => x.Date >= new DateTime(2020, 1, 1)).ToList();
				if (b.Count >= 120) bars[s] = b;
			}
			int sm = BankrollSimulator.DdMicroMode, sw = BankrollSimulator.DdMicroWindow;
			double sk = BankrollSimulator.DdMicroK;
			try
			{
				List<BankrollResult> Go(int mode, double k, int win)
				{
					BankrollSimulator.DdMicroMode = mode; BankrollSimulator.DdMicroK = k; BankrollSimulator.DdMicroWindow = win;
					return bars.OrderBy(kv => kv.Key).Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
				}
				var A = Go(0, 1, 15);
				var ks = bars.Keys.OrderBy(k => k).ToList();
				Console.WriteLine($"\n===== BASKET GATE ({ks.Count} names, full history) =====");
				Console.WriteLine($"{"config",14} {"Sharpe",8} {"MedShp",8} {"Ret%",10} {"MedRet%",9} {"DD%",8} {"medR/dd",8} {"exp",6} {"ShpW",6} {"DdW",6}");
				void Row(string l, List<BankrollResult> r, bool bas) =>
					Console.WriteLine($"{l,14} {r.Average(x => x.SharpeRatio),8:0.000} {Median(r.Select(x => x.SharpeRatio).ToList()),8:0.000} " +
						$"{r.Average(x => x.TotalReturnPct),10:0.0} {Median(r.Select(x => x.TotalReturnPct).ToList()),9:0.0} " +
						$"{r.Average(x => x.MaxDrawdownPct),8:0.00} " +
						$"{Median(r.Select(x => x.MaxDrawdownPct > 0.01 ? x.TotalReturnPct / x.MaxDrawdownPct : 0).ToList()),8:0.000} " +
						$"{r.Average(x => x.Positions.Average(Math.Abs)),6:0.000} " +
						$"{(bas ? "" : r.Where((x, i) => x.SharpeRatio > A[i].SharpeRatio).Count() + "/" + r.Count),6} " +
						$"{(bas ? "" : r.Where((x, i) => x.MaxDrawdownPct < A[i].MaxDrawdownPct).Count() + "/" + r.Count),6}");
				Row("shipped", A, true);
				foreach (var c in cfgs) Row($"m{c.Mode} K{c.K:0.00} w{c.Win}", Go(c.Mode, c.K, c.Win), false);
			}
			finally { BankrollSimulator.DdMicroMode = sm; BankrollSimulator.DdMicroWindow = sw; BankrollSimulator.DdMicroK = sk; }
		}

		private static Dictionary<string, BankrollResult> RunAll(List<string> syms, Dictionary<string, List<OhlcBar>> bars)
		{
			var o = new System.Collections.Concurrent.ConcurrentDictionary<string, BankrollResult>();
			Parallel.ForEach(syms, s => { try { o[s] = BankrollSimulator.Run(bars[s], 10_000.0); } catch { } });
			return o.ToDictionary(kv => kv.Key, kv => kv.Value);
		}

		private static double Score(string label, List<string> syms, Dictionary<string, int> sample,
			Dictionary<string, Dictionary<DateTime, double>> rMap,
			Dictionary<string, BankrollResult> A, Dictionary<string, BankrollResult> B)
		{
			var dsh = new List<double>(); var exc = new List<double>(); var dret = new List<double>();
			var smp = new List<int>(); var bear = new List<double>();
			double aE = 0, bE = 0;
			var bearLo = new DateTime(2022, 1, 1); var bearHi = new DateTime(2022, 12, 31);

			foreach (var s in syms)
			{
				if (!A.TryGetValue(s, out var a) || !B.TryGetValue(s, out var b)) continue;
				int n = Math.Min(a.Positions.Count, b.Positions.Count), st = (int)(n * (1.0 - OosFraction));
				if (n - st < 120) continue;

				var ar = new List<double>(); var br = new List<double>();
				var abr = new List<double>(); var bbr = new List<double>();
				double ae = 0, be = 0;
				for (int i = 0; i < n; i++)
				{
					var d = a.ReturnDates[i];
					if (!rMap[s].TryGetValue(d, out double r)) continue;
					if (i >= st) { ar.Add(a.Positions[i] * r); br.Add(b.Positions[i] * r); ae += Math.Abs(a.Positions[i]); be += Math.Abs(b.Positions[i]); }
					if (d >= bearLo && d <= bearHi) { abr.Add(a.Positions[i] * r); bbr.Add(b.Positions[i] * r); }
				}
				if (ar.Count < 120) continue;
				double h = ae > 1e-9 ? be / ae : 1.0;
				dsh.Add(Shp(br) - Shp(ar));
				exc.Add(Rpd(br) - Rpd(ar.Select(x => x * h).ToList()));
				dret.Add(Cmp(br) - Cmp(ar));
				smp.Add(sample[s]); aE += ae; bE += be;
				if (abr.Count >= 60) bear.Add(Shp(bbr) - Shp(abr));
			}

			double dShp = dsh.Average();
			int repl = 0;
			for (int s = 0; s < Samples; s++)
			{
				var idx = Enumerable.Range(0, smp.Count).Where(i => smp[i] == s).ToList();
				if (idx.Count == 0) continue;
				if (Math.Sign(idx.Average(i => dsh[i])) == Math.Sign(dShp)) repl++;
			}

			Console.WriteLine($"{label,22} {dShp,8:+0.000;-0.000} {repl + "/4",5} {100.0 * dsh.Count(x => x > 0) / dsh.Count,6:0.0} " +
				$"{Median(exc),8:+0.000;-0.000} {100.0 * exc.Count(x => x > 0) / exc.Count,6:0.0} {dret.Average(),7:+0.0;-0.0} " +
				$"{$"{aE / dsh.Count:0}->{bE / dsh.Count:0}",13} {(bear.Count > 0 ? bear.Average() : 0),9:+0.000;-0.000}");
			return dShp;
		}

		private static double Median(List<double> xs)
		{ if (xs.Count == 0) return 0; var s = xs.OrderBy(x => x).ToList(); int m = s.Count / 2; return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0; }
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
