using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// "Skipping KAMA smoothing on ST Bear frees up a lot of time-in-trade -- isn't that worth it? How much
	// return is actually given up?"
	//
	// Prices the trade properly instead of just reporting that Sharpe fell. Three numbers:
	//
	//   1. RETURN GIVEN UP -- mean and median OOS total return, shipped vs variant, in points.
	//   2. EFFICIENCY -- return per unit of exposure per bar, sum(pos*r) / sum(pos). If releasing capital also
	//      raises the return per dollar deployed, the release is genuinely productive. If efficiency FALLS, the
	//      variant earns less per dollar AND holds fewer dollars, which is strictly worse however it is framed.
	//   3. BREAKEVEN REDEPLOYMENT RATE -- the return the freed capital would have to earn somewhere else for the
	//      variant to match shipped:  (shippedPnL - variantPnL) / (shippedExposureBars - variantExposureBars).
	//      Compare it against the engine's own efficiency: if breakeven exceeds what the strategy itself earns per
	//      unit deployed, the freed capital cannot be redeployed profitably enough to pay for the shortfall, even
	//      assuming a perfect second name to rotate into and zero switching cost.
	//
	// All three are arithmetic (sum of pos*r), not compounded -- exact for the capital-allocation question and
	// close enough for the return gap. Broad OOS universe, then the basket for concreteness.
	public static class TitWorthIt
	{
		public static double OosFraction = 0.30;
		public static int    OffMask     = 1 << 3;   // ST Bear

		public static async Task Run(string interval)
		{
			var uni = await Universe.BuildAsync();
			await BarCache.PrimeAsync(uni.Select(u => u.Symbol), interval);
			var bars = BarCache.LoadAll(uni.Select(u => u.Symbol));
			var eligible = uni.Where(u => bars.ContainsKey(u.Symbol) && bars[u.Symbol].Count > 0
				&& u.Shares * bars[u.Symbol][^1].Close >= Universe.MinMarketCap).ToList();
			var syms = eligible.Select(u => u.Symbol).ToList();

			bool savedOn = BankrollSimulator.KamaSmooth;
			int savedMask = BankrollSimulator.KamaSmoothOffMask;

			try
			{
				BankrollSimulator.KamaSmooth = true;
				BankrollSimulator.KamaSmoothOffMask = 0;
				var A = RunAll(syms, bars);
				BankrollSimulator.KamaSmoothOffMask = OffMask;
				var B = RunAll(syms, bars);
				BankrollSimulator.KamaSmoothOffMask = 0;

				Report("BROAD OOS (last 30%)", syms, bars, A, B, oos: true);
				Report("BROAD FULL HISTORY", syms, bars, A, B, oos: false);

				// the 18-name basket, full history, for a concrete deployment read
				string[] bsk = { "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr",
					"smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };
				var have = bsk.Where(bars.ContainsKey).ToList();
				if (have.Count > 0) Report($"BASKET ({have.Count} names, full history)", have, bars, A, B, oos: false);
			}
			finally
			{
				BankrollSimulator.KamaSmooth = savedOn;
				BankrollSimulator.KamaSmoothOffMask = savedMask;
			}
		}

		private static Dictionary<string, BankrollResult> RunAll(List<string> syms, Dictionary<string, List<OhlcBar>> bars)
		{
			var o = new System.Collections.Concurrent.ConcurrentDictionary<string, BankrollResult>();
			Parallel.ForEach(syms, s => { try { o[s] = BankrollSimulator.Run(bars[s], 10_000.0); } catch { } });
			return o.ToDictionary(kv => kv.Key, kv => kv.Value);
		}

		private static void Report(string title, List<string> syms, Dictionary<string, List<OhlcBar>> bars,
			Dictionary<string, BankrollResult> shipped, Dictionary<string, BankrollResult> variant, bool oos)
		{
			double[] edges = { 0, 30, 50, 75, 100, double.MaxValue };
			string[] names = { "<30", "30-50", "50-75", "75-100", "100+" };
			int nb = names.Length;

			var aPnl = new double[nb]; var bPnl = new double[nb];      // sum(pos*r), points
			var aExp = new double[nb]; var bExp = new double[nb];      // sum(pos), exposure-bars
			var aBars = new int[nb];
			var aTit = new double[nb]; var bTit = new double[nb];      // in-trade bar counts
			var aRet = new List<double>[nb]; var bRet = new List<double>[nb];   // per-name compounded return
			var aDd = new List<double>[nb]; var bDd = new List<double>[nb];
			for (int i = 0; i < nb; i++) { aRet[i] = new(); bRet[i] = new(); aDd[i] = new(); bDd[i] = new(); }

			foreach (var s in syms)
			{
				if (!shipped.TryGetValue(s, out var A) || !variant.TryGetValue(s, out var B)) continue;
				var bb = bars[s];
				var rByDate = new Dictionary<DateTime, double>();
				for (int i = 1; i < bb.Count; i++) if (bb[i - 1].Close > 0) rByDate[bb[i].Date] = (bb[i].Close - bb[i - 1].Close) / bb[i - 1].Close;

				int n = Math.Min(A.Positions.Count, B.Positions.Count);
				int start = oos ? (int)(n * (1.0 - OosFraction)) : 0;
				if (n - start < 120) continue;

				double hv = Volatility.AnnualizedHistoricalPct(bb);
				int band = 0; while (band < edges.Length - 2 && hv >= edges[band + 1]) band++;

				var aR = new List<double>(); var bR = new List<double>();
				for (int i = start; i < n; i++)
				{
					if (!rByDate.TryGetValue(A.ReturnDates[i], out double r)) continue;
					double pa = A.Positions[i], pb = B.Positions[i];
					aPnl[band] += pa * r * 100.0; bPnl[band] += pb * r * 100.0;
					aExp[band] += Math.Abs(pa);   bExp[band] += Math.Abs(pb);
					aBars[band]++;
					if (Math.Abs(pa) > 0.05) aTit[band]++;
					if (Math.Abs(pb) > 0.05) bTit[band]++;
					aR.Add(pa * r); bR.Add(pb * r);
				}
				aRet[band].Add(Compound(aR)); bRet[band].Add(Compound(bR));
				aDd[band].Add(MaxDd(aR));     bDd[band].Add(MaxDd(bR));
			}

			Console.WriteLine($"\n===== {title}: KAMA OFF IN ST BEAR — IS THE RELEASED TIME WORTH IT? =====");
			Console.WriteLine($"{"band",8} {"names",6} {"shipRet%",9} {"varRet%",9} {"dRet",8} {"shipDD%",8} {"varDD%",8} " +
				$"{"TiT",11} {"exp-bars",9} {"shipEff",8} {"varEff",8} {"BREAKEVEN",10}");

			for (int b = 0; b <= nb; b++)
			{
				bool tot = b == nb;
				int i0 = tot ? 0 : b, i1 = tot ? nb : b + 1;
				double ap = 0, bp = 0, ae = 0, be = 0, at = 0, bt = 0; int abar = 0;
				var ar = new List<double>(); var br = new List<double>(); var ad = new List<double>(); var bd = new List<double>();
				for (int i = i0; i < i1; i++)
				{
					ap += aPnl[i]; bp += bPnl[i]; ae += aExp[i]; be += bExp[i];
					at += aTit[i]; bt += bTit[i]; abar += aBars[i];
					ar.AddRange(aRet[i]); br.AddRange(bRet[i]); ad.AddRange(aDd[i]); bd.AddRange(bDd[i]);
				}
				if (ar.Count == 0) continue;

				double shipEff = ae > 0 ? ap / ae : 0;          // points of return per unit-exposure-bar
				double varEff  = be > 0 ? bp / be : 0;
				double freed   = ae - be;                        // exposure-bars released
				double shortfall = ap - bp;                      // points of return given up
				double breakeven = freed > 1e-9 ? shortfall / freed : double.NaN;

				Console.WriteLine($"{(tot ? "TOTAL" : names[b]),8} {ar.Count,6} {ar.Average(),9:0.0} {br.Average(),9:0.0} " +
					$"{br.Average() - ar.Average(),8:+0.0;-0.0} {ad.Average(),8:0.00} {bd.Average(),8:0.00} " +
					$"{$"{100.0 * at / abar:0.0}->{100.0 * bt / abar:0.0}",11} {100.0 * freed / ae,9:0.0} " +
					$"{shipEff,8:0.0000} {varEff,8:0.0000} {breakeven,10:0.0000}");
			}
			Console.WriteLine("  Ret% = mean per-name compounded return over the window | eff = points of return per unit-exposure-bar");
			Console.WriteLine("  exp-bars = % of exposure-bars RELEASED | BREAKEVEN = return/unit-exposure-bar the freed capital must earn to match shipped");
		}

		private static double Compound(List<double> r) { double e = 1; foreach (var x in r) e *= 1 + x; return (e - 1) * 100.0; }
		private static double MaxDd(List<double> r)
		{
			double e = 1, p = 1, d = 0;
			foreach (var x in r) { e *= 1 + x; if (e > p) p = e; double dd = (p - e) / p; if (dd > d) d = dd; }
			return d * 100.0;
		}
	}
}
