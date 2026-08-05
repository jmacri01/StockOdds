using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// JOINT DdRatioK x DdMicroK x DdRatioMax sweep.
	//
	// The reason this is needed: the shipped DdRatioK = 0.75 was tuned with a TWO-window scaler. Bolting a third
	// window on top without re-tuning K means the new stage can look valuable simply by absorbing slack that
	// belongs to K -- both are, in part, participation dials. And the micro stage's apparent optimum saturated
	// against DdRatioMax = 2.0, a clamp that was also set before stage 2 existed.
	//
	// So the question is not "does the micro stage help at shipped K" (it did, +0.013) but "does it still help
	// once K is free to move, and is the clamp binding?" The answer is the MARGINAL value: best-with-micro minus
	// best-without-micro, at each clamp. If that margin collapses, the third window was double-counting K.
	public static class JointDdSweep
	{
		public static double OosFraction = 0.30;
		public static int    Samples     = 4;

		public static double[] RatioKs = { 0.5, 0.6, 0.75, 0.9, 1.0, 1.25 };
		public static double[] MicroKs = { 0.0, 0.75, 1.0, 1.25, 1.5 };   // 0 = micro stage OFF
		public static double[] RatioMaxes = { 1.5, 2.0, 3.0 };

		private static uint Fnv(string s) { uint h = 2166136261; foreach (char c in s) { h ^= c; h *= 16777619; } return h; }

		private sealed record Row(double RatioK, double MicroK, double RatioMax, double dShp, int Repl,
			double ShpW, double MedExc, double ExcW, double dRet, double Exp, double Bear2022);

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

			double sK = BankrollSimulator.DdRatioK, sMax = BankrollSimulator.DdRatioMax, sMk = BankrollSimulator.DdMicroK;
			int sMode = BankrollSimulator.DdMicroMode, sWin = BankrollSimulator.DdMicroWindow;

			try
			{
				BankrollSimulator.DdMicroWindow = 15;

				// baseline = shipped exactly
				BankrollSimulator.DdRatioK = sK; BankrollSimulator.DdRatioMax = sMax; BankrollSimulator.DdMicroMode = 0;
				var A = RunAll(syms, bars);
				Console.WriteLine($"\n===== JOINT DdRatioK x DdMicroK x DdRatioMax — BROAD OOS ({syms.Count} names) =====");
				Console.WriteLine($"baseline = shipped (K {sK:0.00}, max {sMax:0.0}, micro off) | micro window 15, DdRatioMin {BankrollSimulator.DdRatioMin:0.0} fixed");

				var rows = new List<Row>();
				foreach (double max in RatioMaxes)
				foreach (double k in RatioKs)
				foreach (double mk in MicroKs)
				{
					BankrollSimulator.DdRatioMax = max;
					BankrollSimulator.DdRatioK = k;
					BankrollSimulator.DdMicroMode = mk > 0 ? 1 : 0;
					BankrollSimulator.DdMicroK = mk > 0 ? mk : 1.0;
					var B = RunAll(syms, bars);
					rows.Add(Score(k, mk, max, syms, sample, rMap, A, B));
				}

				Console.WriteLine($"\n{"K",5} {"microK",7} {"max",5} {"dShp",8} {"repl",5} {"shpW%",6} {"medExc",8} {"excW%",6} {"dRet",7} {"exp",6} {"2022",8}");
				foreach (var r in rows.OrderByDescending(r => r.dShp).Take(18))
					Console.WriteLine($"{r.RatioK,5:0.00} {(r.MicroK > 0 ? r.MicroK.ToString("0.00") : "off"),7} {r.RatioMax,5:0.0} " +
						$"{r.dShp,8:+0.000;-0.000} {r.Repl + "/4",5} {r.ShpW,6:0.0} {r.MedExc,8:+0.000;-0.000} {r.ExcW,6:0.0} " +
						$"{r.dRet,7:+0.0;-0.0} {r.Exp,6:0.000} {r.Bear2022,8:+0.000;-0.000}");

				// THE decisive comparison: marginal value of the third window AFTER K is re-tuned, per clamp
				Console.WriteLine($"\n--- marginal value of the micro stage once K is free ---");
				Console.WriteLine($"{"max",5} {"best micro-OFF",28} {"best micro-ON",28} {"margin",8}");
				foreach (double max in RatioMaxes)
				{
					var off = rows.Where(r => r.RatioMax == max && r.MicroK == 0).OrderByDescending(r => r.dShp).First();
					var on  = rows.Where(r => r.RatioMax == max && r.MicroK > 0).OrderByDescending(r => r.dShp).First();
					Console.WriteLine($"{max,5:0.0} {$"K{off.RatioK:0.00} -> {off.dShp:+0.000} ({off.Repl}/4, {off.ShpW:0.0}%)",28} " +
						$"{$"K{on.RatioK:0.00}/mK{on.MicroK:0.00} -> {on.dShp:+0.000} ({on.Repl}/4, {on.ShpW:0.0}%)",28} " +
						$"{on.dShp - off.dShp,8:+0.000;-0.000}");
				}

				var bestAll = rows.OrderByDescending(r => r.dShp).First();
				var bestOffAll = rows.Where(r => r.MicroK == 0).OrderByDescending(r => r.dShp).First();
				Console.WriteLine($"\nBest overall : K{bestAll.RatioK:0.00} micro{(bestAll.MicroK > 0 ? bestAll.MicroK.ToString("0.00") : "off")} max{bestAll.RatioMax:0.0} " +
					$"-> {bestAll.dShp:+0.000} ({bestAll.Repl}/4, {bestAll.ShpW:0.0}% of names, medExc {bestAll.MedExc:+0.000}, 2022 {bestAll.Bear2022:+0.000})");
				Console.WriteLine($"Best K-only  : K{bestOffAll.RatioK:0.00} max{bestOffAll.RatioMax:0.0} " +
					$"-> {bestOffAll.dShp:+0.000} ({bestOffAll.Repl}/4, {bestOffAll.ShpW:0.0}% of names, medExc {bestOffAll.MedExc:+0.000}, 2022 {bestOffAll.Bear2022:+0.000})");
				Console.WriteLine($"=> third window is worth {bestAll.dShp - bestOffAll.dShp:+0.000} Sharpe once K is re-tuned");
			}
			finally
			{
				BankrollSimulator.DdRatioK = sK; BankrollSimulator.DdRatioMax = sMax;
				BankrollSimulator.DdMicroK = sMk; BankrollSimulator.DdMicroMode = sMode; BankrollSimulator.DdMicroWindow = sWin;
			}
		}

		// Basket gate on the configs the joint sweep actually favours -- which turned out to be K/clamp changes,
		// not the third window.
		public static async Task Basket(string interval, params (double K, double MicroK, double Max)[] cfgs)
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
			double sK = BankrollSimulator.DdRatioK, sMax = BankrollSimulator.DdRatioMax, sMk = BankrollSimulator.DdMicroK;
			int sMode = BankrollSimulator.DdMicroMode;
			try
			{
				List<BankrollResult> Go(double k, double mk, double max)
				{
					BankrollSimulator.DdRatioK = k; BankrollSimulator.DdRatioMax = max;
					BankrollSimulator.DdMicroMode = mk > 0 ? 1 : 0; BankrollSimulator.DdMicroK = mk > 0 ? mk : 1.0;
					return bars.OrderBy(kv => kv.Key).Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
				}
				var A = Go(sK, 0, sMax);
				Console.WriteLine($"\n===== BASKET GATE ({bars.Count} names, full history) =====");
				Console.WriteLine($"{"config",22} {"Sharpe",8} {"MedShp",8} {"Ret%",10} {"MedRet%",9} {"DD%",8} {"medR/dd",8} {"exp",6} {"ShpW",6} {"DdW",6}");
				void Row(string l, List<BankrollResult> r, bool bas) =>
					Console.WriteLine($"{l,22} {r.Average(x => x.SharpeRatio),8:0.000} {Median(r.Select(x => x.SharpeRatio).ToList()),8:0.000} " +
						$"{r.Average(x => x.TotalReturnPct),10:0.0} {Median(r.Select(x => x.TotalReturnPct).ToList()),9:0.0} " +
						$"{r.Average(x => x.MaxDrawdownPct),8:0.00} " +
						$"{Median(r.Select(x => x.MaxDrawdownPct > 0.01 ? x.TotalReturnPct / x.MaxDrawdownPct : 0).ToList()),8:0.000} " +
						$"{r.Average(x => x.Positions.Average(Math.Abs)),6:0.000} " +
						$"{(bas ? "" : r.Where((x, i) => x.SharpeRatio > A[i].SharpeRatio).Count() + "/" + r.Count),6} " +
						$"{(bas ? "" : r.Where((x, i) => x.MaxDrawdownPct < A[i].MaxDrawdownPct).Count() + "/" + r.Count),6}");
				Row($"shipped K{sK:0.00} max{sMax:0.0}", A, true);
				foreach (var c in cfgs)
					Row($"K{c.K:0.00} m{(c.MicroK > 0 ? c.MicroK.ToString("0.00") : "off")} max{c.Max:0.0}", Go(c.K, c.MicroK, c.Max), false);
			}
			finally
			{
				BankrollSimulator.DdRatioK = sK; BankrollSimulator.DdRatioMax = sMax;
				BankrollSimulator.DdMicroK = sMk; BankrollSimulator.DdMicroMode = sMode;
			}
		}

		private static Dictionary<string, BankrollResult> RunAll(List<string> syms, Dictionary<string, List<OhlcBar>> bars)
		{
			var o = new System.Collections.Concurrent.ConcurrentDictionary<string, BankrollResult>();
			Parallel.ForEach(syms, s => { try { o[s] = BankrollSimulator.Run(bars[s], 10_000.0); } catch { } });
			return o.ToDictionary(kv => kv.Key, kv => kv.Value);
		}

		private static Row Score(double k, double mk, double max, List<string> syms, Dictionary<string, int> sample,
			Dictionary<string, Dictionary<DateTime, double>> rMap,
			Dictionary<string, BankrollResult> A, Dictionary<string, BankrollResult> B)
		{
			var dsh = new List<double>(); var exc = new List<double>(); var dret = new List<double>();
			var smp = new List<int>(); var bear = new List<double>();
			double aE = 0, bE = 0; int nn = 0;
			var lo = new DateTime(2022, 1, 1); var hi = new DateTime(2022, 12, 31);

			foreach (var s in syms)
			{
				if (!A.TryGetValue(s, out var a) || !B.TryGetValue(s, out var b)) continue;
				int n = Math.Min(a.Positions.Count, b.Positions.Count), st = (int)(n * (1.0 - OosFraction));
				if (n - st < 120) continue;
				var ar = new List<double>(); var br = new List<double>();
				var ab = new List<double>(); var bb = new List<double>();
				double ae = 0, be = 0;
				for (int i = 0; i < n; i++)
				{
					var d = a.ReturnDates[i];
					if (!rMap[s].TryGetValue(d, out double r)) continue;
					if (i >= st) { ar.Add(a.Positions[i] * r); br.Add(b.Positions[i] * r); ae += Math.Abs(a.Positions[i]); be += Math.Abs(b.Positions[i]); }
					if (d >= lo && d <= hi) { ab.Add(a.Positions[i] * r); bb.Add(b.Positions[i] * r); }
				}
				if (ar.Count < 120) continue;
				double h = ae > 1e-9 ? be / ae : 1.0;
				dsh.Add(Shp(br) - Shp(ar));
				exc.Add(Rpd(br) - Rpd(ar.Select(x => x * h).ToList()));
				dret.Add(Cmp(br) - Cmp(ar));
				smp.Add(sample[s]); aE += ae; bE += be; nn++;
				if (ab.Count >= 60) bear.Add(Shp(bb) - Shp(ab));
			}

			double dShp = dsh.Average();
			int repl = 0;
			for (int s = 0; s < Samples; s++)
			{
				var idx = Enumerable.Range(0, smp.Count).Where(i => smp[i] == s).ToList();
				if (idx.Count > 0 && Math.Sign(idx.Average(i => dsh[i])) == Math.Sign(dShp)) repl++;
			}
			return new Row(k, mk, max, dShp, repl,
				100.0 * dsh.Count(x => x > 0) / dsh.Count, Median(exc),
				100.0 * exc.Count(x => x > 0) / exc.Count, dret.Average(), bE / Math.Max(1, nn) / 300.0,
				bear.Count > 0 ? bear.Average() : 0);
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
