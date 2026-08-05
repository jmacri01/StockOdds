using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// BROAD-UNIVERSE, OUT-OF-SAMPLE, 4-SAMPLE REPLICATION of the HV-conditioned KAMA-in-ST-Bear result.
	//
	// The 18-name basket said: turning the KAMA-distance smoother off on ST-Bear candles costs Sharpe in low
	// HV and pays in high HV, with the excess over a matched-exposure haircut monotone across five bands
	// (-0.240 -> +3.072). Band n's there were 1-7 names, so it needs a real universe before it means anything.
	//
	// Method, matching the README's broad tables:
	//   universe : US-listed common stock, market cap >= $500M (Universe.cs), ~5y daily bars
	//   scoring  : the LAST 30% of each name's bar series -- the engine still warms up on the full history,
	//              only the scored window is held out (BankrollResult exposes aligned per-bar series for this)
	//   samples  : 4 disjoint samples by stable hash of the ticker, so every band is checked 4 independent times
	//   control  : a flat haircut matched to the variant's mean exposure. A haircut is a CONSTANT multiplier on
	//              the final position, so the haircut's return series is exactly h * shippedReturns with
	//              h = targetExposure / baseExposure -- computed arithmetically, no second engine run.
	//
	// A band only counts as replicating if the sign of the effect is the same in all 4 samples.
	public static class BroadReplication
	{
		public static double OosFraction   = 0.30;
		public static int    Samples       = 4;
		public static int    OffMask       = 1 << 3;      // ST Bear
		public static string OffLabel      = "ST Bear";
		public static double[] BandEdges   = { 0, 30, 50, 75, 100, double.MaxValue };
		public static string[] BandNames   = { "<30", "30-50", "50-75", "75-100", "100+" };
		public static bool   FetchIfMissing = true;
		public static int    LimitSymbols  = 0;   // >0 = smoke test on the first N candidates

		private sealed class NameRun
		{
			public string Symbol = "";
			public int Sample, Band;
			public double[] ShipRet = Array.Empty<double>(), VarRet = Array.Empty<double>();
			public double ShipExp, VarExp, ShipTit, VarTit;
		}

		// stable across processes and .NET versions (string.GetHashCode is randomized per run)
		private static uint Fnv(string s)
		{
			uint h = 2166136261;
			foreach (char c in s) { h ^= c; h *= 16777619; }
			return h;
		}

		public static async Task Run(string interval)
		{
			var uni = await Universe.BuildAsync();
			if (LimitSymbols > 0) uni = uni.Take(LimitSymbols).ToList();
			if (FetchIfMissing) await BarCache.PrimeAsync(uni.Select(u => u.Symbol), interval);

			var bars = BarCache.LoadAll(uni.Select(u => u.Symbol));
			Console.WriteLine($"Loaded {bars.Count} symbols with >= {BarCache.MinBars} bars");

			// market-cap gate: shares outstanding * last close
			var eligible = new List<UniverseName>();
			foreach (var u in uni)
			{
				if (!bars.TryGetValue(u.Symbol, out var b) || b.Count == 0) continue;
				u.MarketCap = u.Shares * b[^1].Close;
				if (u.MarketCap >= Universe.MinMarketCap) eligible.Add(u);
			}
			Console.WriteLine($"Eligible at >= ${Universe.MinMarketCap / 1e6:0}M market cap: {eligible.Count}");
			if (eligible.Count == 0) return;

			// ---- run the engine twice per name (shipped, variant); everything else is arithmetic ----
			bool savedOn = BankrollSimulator.KamaSmooth;
			int savedMask = BankrollSimulator.KamaSmoothOffMask;
			double savedHair = BankrollSimulator.FlatHaircut;
			var runs = new List<NameRun>();

			try
			{
				BankrollSimulator.FlatHaircut = 1.0;
				var sw = System.Diagnostics.Stopwatch.StartNew();

				BankrollSimulator.KamaSmooth = true; BankrollSimulator.KamaSmoothOffMask = 0;
				var shipped = RunAll(eligible, bars);
				BankrollSimulator.KamaSmoothOffMask = OffMask;
				var variant = RunAll(eligible, bars);
				BankrollSimulator.KamaSmoothOffMask = 0;
				Console.WriteLine($"Engine: {eligible.Count} names x 2 configs in {sw.Elapsed:hh\\:mm\\:ss}");

				foreach (var u in eligible)
				{
					if (!shipped.TryGetValue(u.Symbol, out var A) || !variant.TryGetValue(u.Symbol, out var B)) continue;
					int n = Math.Min(A.StratReturns.Count, B.StratReturns.Count);
					int start = (int)(n * (1.0 - OosFraction));
					if (n - start < 120) continue;

					double hv = Volatility.AnnualizedHistoricalPct(bars[u.Symbol]);
					int band = 0; while (band < BandEdges.Length - 2 && hv >= BandEdges[band + 1]) band++;

					runs.Add(new NameRun
					{
						Symbol  = u.Symbol,
						Sample  = (int)(Fnv(u.Symbol) % (uint)Samples),
						Band    = band,
						ShipRet = A.StratReturns.Skip(start).Take(n - start).ToArray(),
						VarRet  = B.StratReturns.Skip(start).Take(n - start).ToArray(),
						ShipExp = A.Positions.Skip(start).Take(n - start).Average(Math.Abs),
						VarExp  = B.Positions.Skip(start).Take(n - start).Average(Math.Abs),
						ShipTit = 100.0 * A.Positions.Skip(start).Take(n - start).Count(p => Math.Abs(p) > 0.05) / (n - start),
						VarTit  = 100.0 * B.Positions.Skip(start).Take(n - start).Count(p => Math.Abs(p) > 0.05) / (n - start),
					});
				}
			}
			finally
			{
				BankrollSimulator.KamaSmooth = savedOn;
				BankrollSimulator.KamaSmoothOffMask = savedMask;
				BankrollSimulator.FlatHaircut = savedHair;
			}

			Console.WriteLine($"\n===== BROAD OOS 4-SAMPLE REPLICATION: KAMA OFF IN {OffLabel.ToUpper()} =====");
			Console.WriteLine($"{runs.Count} names | last {OosFraction:P0} of each name's bars | {Samples} disjoint samples");
			Console.WriteLine($"sample sizes: {string.Join(", ", Enumerable.Range(0, Samples).Select(s => $"S{s}={runs.Count(r => r.Sample == s)}"))}");
			Console.WriteLine("excess = variant ret/dd MINUS a flat haircut matched to the variant's mean exposure");

			for (int b = 0; b < BandNames.Length; b++)
			{
				var band = runs.Where(r => r.Band == b).ToList();
				if (band.Count < Samples * 5) { Console.WriteLine($"\n--- HV {BandNames[b]}: n={band.Count}, too thin to split ---"); continue; }

				Console.WriteLine($"\n--- HV {BandNames[b]}  (n={band.Count}) ---");
				Console.WriteLine($"{"sample",7} {"n",5} {"shipShp",8} {"varShp",8} {"dShp",8} {"shipTiT",8} {"varTiT",8} " +
					$"{"shipExp",8} {"varExp",8} {"ship r/dd",10} {"var r/dd",9} {"flat",8} {"excess",8} {"ShpW%",6}");

				var dShps = new List<double>(); var excs = new List<double>();
				for (int s = 0; s < Samples; s++)
				{
					var g = band.Where(r => r.Sample == s).ToList();
					if (g.Count == 0) continue;
					var m = Measure(g);
					dShps.Add(m.dShp); excs.Add(m.excess);
					Console.WriteLine($"{"S" + s,7} {g.Count,5} {m.shipShp,8:0.000} {m.varShp,8:0.000} {m.dShp,8:+0.000;-0.000} " +
						$"{m.shipTit,8:0.0} {m.varTit,8:0.0} {m.shipExp,8:0.000} {m.varExp,8:0.000} " +
						$"{m.shipRpd,10:0.000} {m.varRpd,9:0.000} {m.flatRpd,8:0.000} {m.excess,8:+0.000;-0.000} {m.shpWinPct,6:0.0}");
				}
				var all = Measure(band);
				Console.WriteLine($"{"POOLED",7} {band.Count,5} {all.shipShp,8:0.000} {all.varShp,8:0.000} {all.dShp,8:+0.000;-0.000} " +
					$"{all.shipTit,8:0.0} {all.varTit,8:0.0} {all.shipExp,8:0.000} {all.varExp,8:0.000} " +
					$"{all.shipRpd,10:0.000} {all.varRpd,9:0.000} {all.flatRpd,8:0.000} {all.excess,8:+0.000;-0.000} {all.shpWinPct,6:0.0}");

				int dPos = dShps.Count(x => x > 0), ePos = excs.Count(x => x > 0);
				Console.WriteLine($"   replication: dSharpe {Math.Max(dPos, dShps.Count - dPos)}/{dShps.Count} same sign " +
					$"({(dPos > dShps.Count - dPos ? "positive" : "negative")}) | " +
					$"excess {Math.Max(ePos, excs.Count - ePos)}/{excs.Count} same sign " +
					$"({(ePos > excs.Count - ePos ? "positive" : "negative")})");
			}
		}

		private static Dictionary<string, BankrollResult> RunAll(List<UniverseName> uni, Dictionary<string, List<OhlcBar>> bars)
		{
			var outp = new System.Collections.Concurrent.ConcurrentDictionary<string, BankrollResult>();
			Parallel.ForEach(uni, u =>
			{
				try { outp[u.Symbol] = BankrollSimulator.Run(bars[u.Symbol], 10_000.0); }
				catch { }
			});
			return outp.ToDictionary(kv => kv.Key, kv => kv.Value);
		}

		private sealed record M(double shipShp, double varShp, double dShp, double shipTit, double varTit,
			double shipExp, double varExp, double shipRpd, double varRpd, double flatRpd, double excess, double shpWinPct);

		private static M Measure(List<NameRun> g)
		{
			double shipShp = g.Average(r => Sharpe(r.ShipRet));
			double varShp  = g.Average(r => Sharpe(r.VarRet));
			double shipExp = g.Average(r => r.ShipExp), varExp = g.Average(r => r.VarExp);

			double shipRpd = g.Average(r => Rpd(r.ShipRet));
			double varRpd  = g.Average(r => Rpd(r.VarRet));

			// matched flat haircut: scale each name's SHIPPED position by that name's own h, so the control
			// holds the same exposure name-by-name rather than only on average
			double flatRpd = g.Average(r =>
			{
				double h = r.ShipExp > 1e-9 ? r.VarExp / r.ShipExp : 1.0;
				var scaled = new double[r.ShipRet.Length];
				for (int i = 0; i < scaled.Length; i++) scaled[i] = r.ShipRet[i] * h;
				return Rpd(scaled);
			});

			int wins = g.Count(r => Sharpe(r.VarRet) > Sharpe(r.ShipRet) + 1e-12);
			return new M(shipShp, varShp, varShp - shipShp,
				g.Average(r => r.ShipTit), g.Average(r => r.VarTit), shipExp, varExp,
				shipRpd, varRpd, flatRpd, varRpd - flatRpd, 100.0 * wins / g.Count);
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
