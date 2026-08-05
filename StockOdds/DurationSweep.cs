using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Sweeps the DURATION axis on the position smoother straight on the BROAD OOS universe with 4-sample
	// replication -- not the 18-name basket. The last HV-band episode is the reason: an ordered, plausible
	// gradient on 18 names inverted completely on 2,543. The broad rig costs ~1s per config once bars are
	// cached, so there is no reason to look at the basket first.
	//
	// Scored exactly like BroadReplication: last 30% of each name's bars, 4 disjoint samples by FNV hash,
	// and an analytic flat-haircut control matched per name to the config's own exposure.
	public static class DurationSweep
	{
		public static double OosFraction = 0.30;
		public static int    Samples     = 4;

		private sealed class Cfg
		{
			public string Label = "";
			public int Mode, Source; public double Slope, Full;
			public double HvRef, HvExp = 1.0;
			// CONTROL: a constant smoothing period with the KAMA ramp off. Any "duration" config that merely
			// smooths more on average has to beat these, or the result is a global-smoothing finding wearing
			// a duration costume. Same logic as the flat-haircut control on the exposure side.
			public double FlatPeriod;
		}

		private static readonly List<Cfg> Configs = new();

		private static void Build()
		{
			if (Configs.Count > 0) return;
			foreach (double p in new[] { 10.0, 20.0, 30.0, 50.0 })
				Configs.Add(new Cfg { Label = $"CONTROL flat P{p,2:0}", FlatPeriod = p });
			// The first pass put the optimum on the grid edge (m1/belowK/s-1.0/f40, the largest saturation
			// tested, monotone in f). Extend past it rather than report a boundary optimum.
			// flat-DurFull reference points: the two that won the unscaled sweep
			foreach (var (m, sl, f) in new[] { (1, -1.0, 40.0), (2, -0.5, 40.0) })
				Configs.Add(new Cfg { Label = $"FLAT m{m} s{sl,4:+0.0;-0.0} f{f,2:0}", Mode = m, Source = 0, Slope = sl, Full = f });

			// HV-SCALED decay: durFullEff = DurFull * (ref/HV)^exp. exp +1 = fast decay when volatile (the
			// proposal); exp -1 = the sign control. ref is the HV at which the flat DurFull applies unchanged.
			foreach (int mode in new[] { 1, 2 })
			foreach (double slope in new[] { -1.0, -0.5 })
			foreach (double full in new[] { 20.0, 40.0, 60.0 })
			foreach (double href in new[] { 30.0, 60.0, 90.0 })
			foreach (double hexp in new[] { 1.0, -1.0 })
				Configs.Add(new Cfg
				{
					Label = $"m{mode} s{slope,4:+0.0;-0.0} f{full,2:0} hv{href,2:0}^{hexp,2:+0;-0}",
					Mode = mode, Source = 0, Slope = slope, Full = full, HvRef = href, HvExp = hexp
				});
		}

		private static uint Fnv(string s)
		{
			uint h = 2166136261;
			foreach (char c in s) { h ^= c; h *= 16777619; }
			return h;
		}

		public static async Task Run(string interval)
		{
			Build();
			var uni = await Universe.BuildAsync();
			await BarCache.PrimeAsync(uni.Select(u => u.Symbol), interval);
			var bars = BarCache.LoadAll(uni.Select(u => u.Symbol));

			var eligible = uni.Where(u => bars.ContainsKey(u.Symbol) && bars[u.Symbol].Count > 0
				&& u.Shares * bars[u.Symbol][^1].Close >= Universe.MinMarketCap).ToList();
			var syms = eligible.Select(u => u.Symbol).ToList();
			var sample = syms.ToDictionary(s => s, s => (int)(Fnv(s) % (uint)Samples));
			Console.WriteLine($"Eligible: {syms.Count} names, {Samples} samples, OOS last {OosFraction:P0}");

			int savedMode = BankrollSimulator.DurMode, savedSrc = BankrollSimulator.DurSource;
			double savedSlope = BankrollSimulator.DurSlope, savedFull = BankrollSimulator.DurFull;

			bool savedKama = BankrollSimulator.KamaSmooth;
			int  savedBase = BankrollSimulator.PositionSmoothPeriod;

			// per-name OOS slice of a config: returns, exposure, TiT
			Dictionary<string, (double[] Ret, double Exp, double Tit)> Eval(int mode, int src, double slope, double full, double flatPeriod = 0, double hvRef = 0, double hvExp = 1)
			{
				if (flatPeriod > 0)
				{
					// control arm: constant period, no KAMA ramp, no duration
					BankrollSimulator.KamaSmooth = false; BankrollSimulator.DurMode = 0;
					BankrollSimulator.PositionSmoothPeriod = (int)flatPeriod;
					BankrollSimulator.DurHvRef = 0;
				}
				else
				{
					BankrollSimulator.KamaSmooth = savedKama; BankrollSimulator.PositionSmoothPeriod = savedBase;
					BankrollSimulator.DurMode = mode; BankrollSimulator.DurSource = src;
					BankrollSimulator.DurSlope = slope; BankrollSimulator.DurFull = full;
					BankrollSimulator.DurHvRef = hvRef; BankrollSimulator.DurHvExp = hvExp;
				}
				var outp = new System.Collections.Concurrent.ConcurrentDictionary<string, (double[], double, double)>();
				Parallel.ForEach(syms, s =>
				{
					try
					{
						var r = BankrollSimulator.Run(bars[s], 10_000.0);
						int n = r.StratReturns.Count, st = (int)(n * (1.0 - OosFraction));
						if (n - st < 120) return;
						var ret = r.StratReturns.Skip(st).Take(n - st).ToArray();
						var pos = r.Positions.Skip(st).Take(n - st).ToList();
						outp[s] = (ret, pos.Average(Math.Abs), 100.0 * pos.Count(p => Math.Abs(p) > 0.05) / pos.Count);
					}
					catch { }
				});
				return outp.ToDictionary(kv => kv.Key, kv => kv.Value);
			}

			try
			{
				var baseline = Eval(0, 0, 0, 0);
				var keys = baseline.Keys.OrderBy(k => k).ToList();
				Console.WriteLine($"Baseline (shipped): Sharpe {keys.Average(k => Sharpe(baseline[k].Ret)):0.000} " +
					$"| ret/dd {keys.Average(k => Rpd(baseline[k].Ret)):0.000} | TiT {keys.Average(k => baseline[k].Tit):0.0} " +
					$"| exp {keys.Average(k => baseline[k].Exp):0.000}");

				Console.WriteLine("\n===== SMOOTHER DURATION AXIS — BROAD OOS, 4-SAMPLE =====");
				Console.WriteLine("m1 = duration REPLACES the distance ramp | m2 = duration ADDS to it");
				Console.WriteLine("s>0 = smooth HARDER with age | s<0 = start smoothed, get MORE RESPONSIVE with age");
				Console.WriteLine();
				Console.WriteLine($"{"config",30} {"dShp",8} {"repl",6} {"excess",8} {"repl",6} {"TiT",6} {"exp",6} {"win%",6}");

				var rows = new List<(string Label, double dShp, int shpRepl, double exc, int excRepl, double tit, double exp, double win)>();

				foreach (var c in Configs)
				{
					var v = Eval(c.Mode, c.Source, c.Slope, c.Full, c.FlatPeriod, c.HvRef, c.HvExp);
					var k = keys.Where(v.ContainsKey).ToList();
					if (k.Count < 100) continue;

					double dShp = k.Average(x => Sharpe(v[x].Ret) - Sharpe(baseline[x].Ret));
					double exc  = k.Average(x =>
					{
						double h = baseline[x].Exp > 1e-9 ? v[x].Exp / baseline[x].Exp : 1.0;
						var scaled = baseline[x].Ret.Select(y => y * h).ToArray();
						return Rpd(v[x].Ret) - Rpd(scaled);
					});
					int shpRepl = 0, excRepl = 0;
					for (int s = 0; s < Samples; s++)
					{
						var g = k.Where(x => sample[x] == s).ToList();
						if (g.Count == 0) continue;
						double d = g.Average(x => Sharpe(v[x].Ret) - Sharpe(baseline[x].Ret));
						double e = g.Average(x =>
						{
							double h = baseline[x].Exp > 1e-9 ? v[x].Exp / baseline[x].Exp : 1.0;
							return Rpd(v[x].Ret) - Rpd(baseline[x].Ret.Select(y => y * h).ToArray());
						});
						if (Math.Sign(d) == Math.Sign(dShp)) shpRepl++;
						if (Math.Sign(e) == Math.Sign(exc)) excRepl++;
					}
					double win = 100.0 * k.Count(x => Sharpe(v[x].Ret) > Sharpe(baseline[x].Ret)) / k.Count;
					rows.Add((c.Label, dShp, shpRepl, exc, excRepl, k.Average(x => v[x].Tit), k.Average(x => v[x].Exp), win));
				}

				foreach (var r in rows.OrderByDescending(r => r.dShp))
					Console.WriteLine($"{r.Label,30} {r.dShp,8:+0.000;-0.000} {r.shpRepl + "/4",6} {r.exc,8:+0.000;-0.000} " +
						$"{r.excRepl + "/4",6} {r.tit,6:0.0} {r.exp,6:0.000} {r.win,6:0.0}");

				var best = rows.OrderByDescending(r => r.dShp).FirstOrDefault();
				Console.WriteLine(best.dShp > 0
					? $"\nBest on Sharpe: {best.Label}  dShp {best.dShp:+0.000} ({best.shpRepl}/4), excess {best.exc:+0.000} ({best.excRepl}/4), {best.win:0.0}% of names"
					: "\nNo duration config beats shipped on broad OOS Sharpe.");
			}
			finally
			{
				BankrollSimulator.DurMode = savedMode; BankrollSimulator.DurSource = savedSrc;
				BankrollSimulator.DurSlope = savedSlope; BankrollSimulator.DurFull = savedFull;
				BankrollSimulator.KamaSmooth = savedKama; BankrollSimulator.PositionSmoothPeriod = savedBase;
				BankrollSimulator.DurHvRef = 0; BankrollSimulator.DurHvExp = 1.0;
			}
		}

		// The basket gate. Several layers in this project won on the broad universe and were removed anyway
		// because they hurt the 18-name high-vol basket (see fixed-n50-back-to-basics), so a broad winner is
		// not a ship candidate until it has been looked at here. Full history, not the OOS slice.
		public static async Task Basket(string interval, int mode, int src, double slope, double full, double hvRef = 0, double hvExp = 1)
		{
			string[] syms = { "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr",
				"smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };
			var bars = new Dictionary<string, List<OhlcBar>>();
			foreach (var s in syms)
			{
				var b = BarCache.Load(s) ?? (await YahooClient.GetBarsAsync(s, interval));
				b = b.Where(x => x.Date >= new DateTime(2020, 1, 1)).ToList();
				if (b.Count >= 120) bars[s] = b;
			}

			int savedMode = BankrollSimulator.DurMode, savedSrc = BankrollSimulator.DurSource;
			double savedSlope = BankrollSimulator.DurSlope, savedFull = BankrollSimulator.DurFull;
			try
			{
				List<BankrollResult> Go(int m)
				{
					BankrollSimulator.DurMode = m; BankrollSimulator.DurSource = src;
					BankrollSimulator.DurSlope = slope; BankrollSimulator.DurFull = full;
					BankrollSimulator.DurHvRef = m == 0 ? 0 : hvRef; BankrollSimulator.DurHvExp = hvExp;
					return bars.OrderBy(kv => kv.Key).Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
				}
				var A = Go(0); var B = Go(mode);

				Console.WriteLine($"\n===== BASKET GATE (18 names, full history): m{mode} src{src} s{slope:+0.0;-0.0} f{full:0} =====");
				Console.WriteLine($"{"config",9} {"Sharpe",8} {"MedShp",8} {"Ret%",10} {"MedRet%",9} {"DD%",8} {"r/dd",8} {"medR/dd",8} {"TiT%",6} {"exp",6}");
				void Row(string lbl, List<BankrollResult> r) =>
					Console.WriteLine($"{lbl,9} {r.Average(x => x.SharpeRatio),8:0.000} {Med(r.Select(x => x.SharpeRatio)),8:0.000} " +
						$"{r.Average(x => x.TotalReturnPct),10:0.0} {Med(r.Select(x => x.TotalReturnPct)),9:0.0} " +
						$"{r.Average(x => x.MaxDrawdownPct),8:0.00} " +
						$"{r.Average(x => x.MaxDrawdownPct > 0.01 ? x.TotalReturnPct / x.MaxDrawdownPct : 0),8:0.000} " +
						$"{Med(r.Select(x => x.MaxDrawdownPct > 0.01 ? x.TotalReturnPct / x.MaxDrawdownPct : 0)),8:0.000} " +
						$"{r.Average(x => 100.0 * x.Positions.Count(p => Math.Abs(p) > 0.05) / Math.Max(1, x.Positions.Count)),6:0.0} " +
						$"{r.Average(x => x.Positions.Average(Math.Abs)),6:0.000}");
				Row("shipped", A); Row("duration", B);
				Console.WriteLine($"  Sharpe better on {B.Where((x, i) => x.SharpeRatio > A[i].SharpeRatio).Count()}/{B.Count} names | " +
					$"return better on {B.Where((x, i) => x.TotalReturnPct > A[i].TotalReturnPct).Count()}/{B.Count} | " +
					$"drawdown better on {B.Where((x, i) => x.MaxDrawdownPct < A[i].MaxDrawdownPct).Count()}/{B.Count}");
				foreach (var (s, i) in bars.Keys.OrderBy(k => k).Select((s, i) => (s, i)))
					Console.WriteLine($"      {s,6} shp {A[i].SharpeRatio,7:0.000} -> {B[i].SharpeRatio,7:0.000}   " +
						$"ret {A[i].TotalReturnPct,8:0.0} -> {B[i].TotalReturnPct,8:0.0}   dd {A[i].MaxDrawdownPct,6:0.0} -> {B[i].MaxDrawdownPct,6:0.0}");
			}
			finally
			{
				BankrollSimulator.DurMode = savedMode; BankrollSimulator.DurSource = savedSrc;
				BankrollSimulator.DurSlope = savedSlope; BankrollSimulator.DurFull = savedFull;
				BankrollSimulator.DurHvRef = 0; BankrollSimulator.DurHvExp = 1.0;
			}
		}

		private static double Med(IEnumerable<double> xs)
		{
			var s = xs.OrderBy(x => x).ToList();
			if (s.Count == 0) return 0;
			int m = s.Count / 2;
			return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
		}

		private static double Sharpe(double[] r)
		{
			if (r.Length < 2) return 0;
			double m = r.Average();
			double v = r.Sum(x => (x - m) * (x - m)) / (r.Length - 1);
			double sd = Math.Sqrt(v);
			double s = sd > 0 ? m / sd * Math.Sqrt(252.0) : 0;
			return double.IsNaN(s) || double.IsInfinity(s) ? 0 : s;
		}

		private static double Rpd(double[] r)
		{
			double eq = 1, peak = 1, dd = 0;
			foreach (var x in r) { eq *= 1 + x; if (eq > peak) peak = eq; double d = (peak - eq) / peak; if (d > dd) dd = d; }
			double ret = (eq - 1) * 100.0, ddp = dd * 100.0;
			if (double.IsNaN(ret) || double.IsInfinity(ret)) return 0;
			return ddp > 0.01 ? ret / ddp : 0;
		}
	}
}
