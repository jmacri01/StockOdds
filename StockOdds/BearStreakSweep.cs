using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Dial the position smoother with the CONSECUTIVE ST BEAR CANDLE COUNT. Two forms, both acting only on ST
	// Bear candles and leaving the shipped depth ramp untouched everywhere else:
	//   mode 3  replace the period outright with a streak ramp
	//   mode 4  dial the depth ramp's OWN period toward the base as the streak lengthens (most literal reading)
	// Negative slope = dial smoothing DOWN with streak age (the proposal). Positive = the sign control.
	//
	// Reports the STREAK-LENGTH DISTRIBUTION first: a rule keyed on a counter that rarely exceeds 3 cannot do much
	// with a 20-bar saturation, and a null result has to be read against how often the counter actually gets there.
	// Every table carries mean AND median excess plus per-name breadth -- three candidates this session looked good
	// on mean ret/dd excess alone and all three died once breadth was printed.
	public static class BearStreakSweep
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

			int savedMode = BankrollSimulator.DurMode; double savedSlope = BankrollSimulator.DurSlope,
				savedFull = BankrollSimulator.DurFull, savedHv = BankrollSimulator.DurHvRef;

			try
			{
				BankrollSimulator.DurHvRef = 0;
				BankrollSimulator.DurMode = 0;
				var A = RunAll(syms, bars);

				// ---- how long do ST Bear streaks actually run? ----
				var streaks = new List<int>();
				long bearBars = 0, allBars = 0;
				var atLeast = new long[25];
				foreach (var s in syms)
				{
					if (!A.TryGetValue(s, out var r)) continue;
					int run = 0;
					foreach (var st in r.StState)
					{
						allBars++;
						if (st == ShortTermState.Bear)
						{
							run++; bearBars++;
							for (int k = 1; k < atLeast.Length && k <= run; k++) atLeast[k]++;
						}
						else { if (run > 0) streaks.Add(run); run = 0; }
					}
					if (run > 0) streaks.Add(run);
				}
				streaks.Sort();
				Console.WriteLine($"\n===== ST BEAR STREAK LENGTH (over {syms.Count} names) =====");
				Console.WriteLine($"ST Bear is {100.0 * bearBars / allBars:0.0}% of bars | {streaks.Count:N0} streaks | " +
					$"mean {streaks.Average():0.00}, median {streaks[streaks.Count / 2]}, p90 {streaks[(int)(streaks.Count * 0.90)]}, max {streaks[^1]}");
				Console.WriteLine("share of ALL bars sitting at streak >= k:");
				Console.WriteLine("  " + string.Join("  ", new[] { 1, 2, 3, 4, 5, 8, 12, 20 }
					.Where(k => k < atLeast.Length).Select(k => $"k{k}:{100.0 * atLeast[k] / allBars:0.0}%")));

				Console.WriteLine($"\n===== SMOOTHING DIALED BY CONSECUTIVE ST BEAR CANDLES — BROAD OOS =====");
				Console.WriteLine("m3 = replace period with a streak ramp | m4 = dial the depth ramp's own period with streak age");
				Console.WriteLine("s<0 = dial smoothing DOWN as the streak runs (the proposal) | s>0 = sign control");
				Console.WriteLine($"\n{"config",16} {"dShp",8} {"repl",5} {"shpW%",6} {"excess",8} {"repl",5} {"medExc",8} {"excW%",6} " +
					$"{"dRet",7} {"TiT",11} {"freed",6}");

				var results = new List<(string Label, double dShp, int shpRepl, double shpW, double exc, int excRepl, double medExc, double excW)>();
				// "Half smoothing" and its neighbours: a MULTIPLIER on the depth ramp's own period on ST Bear bars.
				// This interpolates a curve already mapped in absolute terms (P5 -0.076 ... P220 +0.084), so the answer
				// is largely predictable from where half lands in bars -- which is why the mean shipped period is printed.
				BankrollSimulator.DurMode = 0;
				Console.WriteLine($"shipped depth ramp on ST Bear bars averages P{A.Values.Where(r => r.StBearMeanPeriod > 0).Average(r => r.StBearMeanPeriod):0.0} " +
					$"(base P{BankrollSimulator.PositionSmoothPeriod}, ceiling P{BankrollSimulator.KamaSmoothMaxPeriod}) -- so x0.5 lands near " +
					$"P{0.5 * A.Values.Where(r => r.StBearMeanPeriod > 0).Average(r => r.StBearMeanPeriod):0.0}");
				foreach (double mult in new[] { 0.25, 0.5, 0.75, 1.5, 2.0, 3.0 })
				{
					BankrollSimulator.StBearSmoothMult = mult;
					var B = RunAll(syms, bars);
					var row = Score($"stBear x{mult,4:0.00}", syms, sample, bars, rMap, A, B);
					results.Add((row.Label, row.dShp, row.shpRepl, row.shpW, row.exc, row.excRepl, row.medExc, row.excW));
				}
				BankrollSimulator.StBearSmoothMult = 1.0;

				var best = results.OrderByDescending(r => r.dShp).First();
				Console.WriteLine($"\nBest on Sharpe: {best.Label} dShp {best.dShp:+0.000} ({best.shpRepl}/4, {best.shpW:0.0}% of names), " +
					$"excess mean {best.exc:+0.000} median {best.medExc:+0.000} ({best.excW:0.0}% of names)");
				int good = results.Count(r => r.dShp > 0 && r.shpRepl == 4 && r.medExc > 0 && r.excW > 50);
				Console.WriteLine(good == 0
					? "NO config is positive on all four gates (dSharpe > 0, 4/4, median excess > 0, breadth > 50%)."
					: $"{good} config(s) pass all four gates.");
			}
			finally
			{
				BankrollSimulator.StBearSmoothPeriod = 0; BankrollSimulator.StBearSmoothMult = 1.0;
				BankrollSimulator.DurMode = savedMode; BankrollSimulator.DurSlope = savedSlope;
				BankrollSimulator.DurFull = savedFull; BankrollSimulator.DurHvRef = savedHv;
			}
		}

		// The ST-Bear period sweep is MONOTONE with no interior optimum, all the way to a period that effectively
		// FREEZES the position on ST Bear candles (P220 -> alpha 0.009). That shape usually means the knob is a
		// proxy for something simpler -- here "don't de-lever into short-term weakness" -- which is exactly the bet
		// that flatters itself on a bull-heavy, survivor-heavy 2020-2025 sample. So score it by REGIME: the 2022
		// bear year on its own, against the same window's baseline. If the edge is real it survives 2022; if it is
		// a beta/dip-buying tilt it should invert there.
		public static async Task Regime(string interval)
		{
			var uni = await Universe.BuildAsync();
			var bars = BarCache.LoadAll(uni.Select(u => u.Symbol));
			var syms = uni.Where(u => bars.ContainsKey(u.Symbol) && bars[u.Symbol].Count > 0
					&& u.Shares * bars[u.Symbol][^1].Close >= Universe.MinMarketCap)
				.Select(u => u.Symbol).ToList();
			var rMap = new Dictionary<string, Dictionary<DateTime, double>>();
			foreach (var s in syms)
			{
				var b = bars[s]; var m = new Dictionary<DateTime, double>();
				for (int i = 1; i < b.Count; i++) if (b[i - 1].Close > 0) m[b[i].Date] = (b[i].Close - b[i - 1].Close) / b[i - 1].Close;
				rMap[s] = m;
			}

			var windows = new (string Name, DateTime Lo, DateTime Hi)[]
			{
				("2022 BEAR", new DateTime(2022, 1, 1), new DateTime(2022, 12, 31)),
				("2020-21 BULL", new DateTime(2020, 4, 1), new DateTime(2021, 12, 31)),
				("2023-25 BULL", new DateTime(2023, 1, 1), new DateTime(2025, 12, 31)),
				("FULL", DateTime.MinValue, DateTime.MaxValue),
			};

			double saved = BankrollSimulator.StBearSmoothPeriod;
			try
			{
				BankrollSimulator.StBearSmoothPeriod = 0;
				var A = RunAll(syms, bars);

				Console.WriteLine($"\n===== ST-BEAR SMOOTHING BY REGIME ({syms.Count} names) =====");
				Console.WriteLine($"{"window",14} {"period",7} {"shipShp",8} {"varShp",8} {"dShp",8} {"shpW%",6} " +
					$"{"medExc",8} {"excW%",6} {"shipRet%",9} {"varRet%",9} {"dRet",7} {"exp",12}");

				foreach (var w in windows)
				{
					foreach (double per in new[] { 50.0, 110.0, 220.0 })
					{
						BankrollSimulator.StBearSmoothPeriod = per;
						var B = RunAll(syms, bars);

						var dsh = new List<double>(); var exc = new List<double>();
						var aRet = new List<double>(); var bRet = new List<double>();
						double aE = 0, bE = 0;
						foreach (var s in syms)
						{
							if (!A.TryGetValue(s, out var a) || !B.TryGetValue(s, out var b)) continue;
							int n = Math.Min(a.Positions.Count, b.Positions.Count);
							var ar = new List<double>(); var br = new List<double>();
							double ae = 0, be = 0;
							for (int i = 0; i < n; i++)
							{
								var d = a.ReturnDates[i];
								if (d < w.Lo || d > w.Hi) continue;
								if (!rMap[s].TryGetValue(d, out double r)) continue;
								ar.Add(a.Positions[i] * r); br.Add(b.Positions[i] * r);
								ae += Math.Abs(a.Positions[i]); be += Math.Abs(b.Positions[i]);
							}
							if (ar.Count < 60) continue;
							double h = ae > 1e-9 ? be / ae : 1.0;
							dsh.Add(Shp(br) - Shp(ar));
							exc.Add(Rpd(br) - Rpd(ar.Select(x => x * h).ToList()));
							aRet.Add(Cmp(ar)); bRet.Add(Cmp(br));
							aE += ae; bE += be;
						}
						if (dsh.Count == 0) continue;

						Console.WriteLine($"{w.Name,14} {"P" + per,7:0} {"",8} {"",8} {dsh.Average(),8:+0.000;-0.000} " +
							$"{100.0 * dsh.Count(x => x > 0) / dsh.Count,6:0.0} {Median(exc),8:+0.000;-0.000} " +
							$"{100.0 * exc.Count(x => x > 0) / exc.Count,6:0.0} {aRet.Average(),9:0.0} {bRet.Average(),9:0.0} " +
							$"{bRet.Average() - aRet.Average(),7:+0.0;-0.0} {$"{aE / dsh.Count / 1:0}->{bE / dsh.Count:0}",12}");
					}
					Console.WriteLine();
				}
			}
			finally { BankrollSimulator.StBearSmoothPeriod = saved; }
		}

		// basket gate
		public static async Task Basket(string interval, double period)
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
			double saved = BankrollSimulator.StBearSmoothPeriod;
			try
			{
				List<BankrollResult> Go(double p)
				{
					BankrollSimulator.StBearSmoothPeriod = p;
					return bars.OrderBy(kv => kv.Key).Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
				}
				var A = Go(0); var B = Go(period);
				var ks = bars.Keys.OrderBy(k => k).ToList();
				Console.WriteLine($"\n===== BASKET GATE ({ks.Count} names, full history): StBearSmoothPeriod = {period:0} =====");
				Console.WriteLine($"{"config",9} {"Sharpe",8} {"MedShp",8} {"Ret%",10} {"MedRet%",9} {"DD%",8} {"medR/dd",8} {"TiT%",6} {"exp",6}");
				void Row(string l, List<BankrollResult> r) =>
					Console.WriteLine($"{l,9} {r.Average(x => x.SharpeRatio),8:0.000} {Median(r.Select(x => x.SharpeRatio).ToList()),8:0.000} " +
						$"{r.Average(x => x.TotalReturnPct),10:0.0} {Median(r.Select(x => x.TotalReturnPct).ToList()),9:0.0} " +
						$"{r.Average(x => x.MaxDrawdownPct),8:0.00} " +
						$"{Median(r.Select(x => x.MaxDrawdownPct > 0.01 ? x.TotalReturnPct / x.MaxDrawdownPct : 0).ToList()),8:0.000} " +
						$"{r.Average(x => 100.0 * x.Positions.Count(p => Math.Abs(p) > 0.05) / Math.Max(1, x.Positions.Count)),6:0.0} " +
						$"{r.Average(x => x.Positions.Average(Math.Abs)),6:0.000}");
				Row("shipped", A); Row($"P{period:0}", B);
				Console.WriteLine($"  Sharpe better on {B.Where((x, i) => x.SharpeRatio > A[i].SharpeRatio).Count()}/{B.Count} | " +
					$"drawdown better on {B.Where((x, i) => x.MaxDrawdownPct < A[i].MaxDrawdownPct).Count()}/{B.Count}");
				for (int i = 0; i < ks.Count; i++)
					Console.WriteLine($"      {ks[i],6} shp {A[i].SharpeRatio,7:0.000} -> {B[i].SharpeRatio,7:0.000}   " +
						$"ret {A[i].TotalReturnPct,8:0.0} -> {B[i].TotalReturnPct,8:0.0}   dd {A[i].MaxDrawdownPct,6:0.0} -> {B[i].MaxDrawdownPct,6:0.0}");
			}
			finally { BankrollSimulator.StBearSmoothPeriod = saved; }
		}

		private static Dictionary<string, BankrollResult> RunAll(List<string> syms, Dictionary<string, List<OhlcBar>> bars)
		{
			var o = new System.Collections.Concurrent.ConcurrentDictionary<string, BankrollResult>();
			Parallel.ForEach(syms, s => { try { o[s] = BankrollSimulator.Run(bars[s], 10_000.0); } catch { } });
			return o.ToDictionary(kv => kv.Key, kv => kv.Value);
		}

		private static (string Label, double dShp, int shpRepl, double shpW, double exc, int excRepl, double medExc, double excW)
			Score(string label, List<string> syms, Dictionary<string, int> sample, Dictionary<string, List<OhlcBar>> bars,
				Dictionary<string, Dictionary<DateTime, double>> rMap,
				Dictionary<string, BankrollResult> A, Dictionary<string, BankrollResult> B)
		{
			var per = new List<(int Sample, double dShp, double exc, double dRet, double aTit, double bTit, double aExp, double bExp, int Bars)>();

			foreach (var s in syms)
			{
				if (!A.TryGetValue(s, out var a) || !B.TryGetValue(s, out var b)) continue;
				int n = Math.Min(a.Positions.Count, b.Positions.Count), st = (int)(n * (1.0 - OosFraction));
				if (n - st < 120) continue;
				var ar = new List<double>(); var br = new List<double>();
				double aE = 0, bE = 0, aT = 0, bT = 0; int cnt = 0;
				for (int i = st; i < n; i++)
				{
					if (!rMap[s].TryGetValue(a.ReturnDates[i], out double r)) continue;
					ar.Add(a.Positions[i] * r); br.Add(b.Positions[i] * r);
					aE += Math.Abs(a.Positions[i]); bE += Math.Abs(b.Positions[i]);
					if (Math.Abs(a.Positions[i]) > 0.05) aT++;
					if (Math.Abs(b.Positions[i]) > 0.05) bT++;
					cnt++;
				}
				if (cnt < 120) continue;
				double h = aE > 1e-9 ? bE / aE : 1.0;
				per.Add((sample[s], Shp(br) - Shp(ar), Rpd(br) - Rpd(ar.Select(x => x * h).ToList()),
					Cmp(br) - Cmp(ar), aT, bT, aE, bE, cnt));
			}

			double dShp = per.Average(p => p.dShp), exc = per.Average(p => p.exc);
			int shpRepl = 0, excRepl = 0;
			for (int s = 0; s < Samples; s++)
			{
				var g = per.Where(p => p.Sample == s).ToList();
				if (g.Count == 0) continue;
				if (Math.Sign(g.Average(p => p.dShp)) == Math.Sign(dShp)) shpRepl++;
				if (Math.Sign(g.Average(p => p.exc)) == Math.Sign(exc)) excRepl++;
			}
			double shpW = 100.0 * per.Count(p => p.dShp > 0) / per.Count;
			double excW = 100.0 * per.Count(p => p.exc > 0) / per.Count;
			double medExc = Median(per.Select(p => p.exc).ToList());
			double barsTot = per.Sum(p => (double)p.Bars);
			double aEt = per.Sum(p => p.aExp), bEt = per.Sum(p => p.bExp);

			Console.WriteLine($"{label,16} {dShp,8:+0.000;-0.000} {shpRepl + "/4",5} {shpW,6:0.0} {exc,8:+0.000;-0.000} " +
				$"{excRepl + "/4",5} {medExc,8:+0.000;-0.000} {excW,6:0.0} {per.Average(p => p.dRet),7:+0.0;-0.0} " +
				$"{$"{100.0 * per.Sum(p => p.aTit) / barsTot:0.0}->{100.0 * per.Sum(p => p.bTit) / barsTot:0.0}",11} " +
				$"{100.0 * (aEt - bEt) / aEt,6:0.0}");

			return (label, dShp, shpRepl, shpW, exc, excRepl, medExc, excW);
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
