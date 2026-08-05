using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Per-ST-state ablation of the KAMA-distance smoother: turn it off in ONE short-term state at a time
	// (that state's bars fall back to the flat PositionSmoothPeriod floor) and see what the smoother was
	// contributing there. Bookended by the two global configs -- on everywhere (shipped) and off everywhere.
	//
	// Scored on Sharpe (exposure-invariant, so a move there is real selection) AND on ret/dd against a
	// flat haircut matched to the same mean exposure, because the smoother changes how much is held.
	// The bar share of each state is printed so a flat result reads as "nothing there" rather than "no effect".
	public static class KamaByStateSweep
	{
		public static string[] Symbols =
			{ "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr", "smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };

		public static DateTime StartDate = new DateTime(2020, 1, 1);

		public static async Task Run(string interval)
		{
			var barsBySymbol = new Dictionary<string, List<OhlcBar>>();
			foreach (var sym in Symbols)
			{
				try
				{
					var b = (await YahooClient.GetBarsAsync(sym, interval)).Where(x => x.Date >= StartDate).ToList();
					if (b.Count >= 120) barsBySymbol[sym] = b;
					else Console.WriteLine($"  skipping {sym}: {b.Count} bars");
				}
				catch (Exception ex) { Console.WriteLine($"  skipping {sym}: {ex.Message}"); }
			}

			bool   savedOn    = BankrollSimulator.KamaSmooth;
			int    savedMask  = BankrollSimulator.KamaSmoothOffMask;
			double savedHair  = BankrollSimulator.FlatHaircut;

			List<BankrollResult> Eval(bool kamaOn, int offMask, double haircut)
			{
				BankrollSimulator.KamaSmooth = kamaOn;
				BankrollSimulator.KamaSmoothOffMask = offMask;
				BankrollSimulator.FlatHaircut = haircut;
				return barsBySymbol.OrderBy(kv => kv.Key)
					.Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
			}

			try
			{
				var baseRes = Eval(true, 0, 1.0);
				double baseExp = MeanExposure(baseRes);
				double baseSh  = baseRes.Average(r => Safe(r.SharpeRatio));
				double baseRpd = RetPerDd(baseRes);

				// share of bars in each ST state (identical across configs -- the state machine is upstream)
				var stateShare = new double[4];
				int totalBars = baseRes.Sum(r => r.StState.Count);
				foreach (var r in baseRes)
					foreach (var s in r.StState) stateShare[(int)s]++;
				for (int i = 0; i < 4; i++) stateShare[i] = 100.0 * stateShare[i] / Math.Max(1, totalBars);

				Console.WriteLine("\n===== KAMA SMOOTHING OFF, ONE ST STATE AT A TIME =====");
				Console.WriteLine($"Symbols {barsBySymbol.Count} | from {StartDate:yyyy-MM-dd}");
				Console.WriteLine($"Baseline (KAMA on everywhere): Sharpe {baseSh:0.000}, ret {baseRes.Average(r => Safe(r.TotalReturnPct)):0.0}%, " +
					$"dd {baseRes.Average(r => Safe(r.MaxDrawdownPct)):0.00}%, exp {baseExp:0.000}, ret/dd {baseRpd:0.000}");
				Console.WriteLine($"ST bar share: Bull {stateShare[0]:0.0}%  BullNeut {stateShare[1]:0.0}%  BearNeut {stateShare[2]:0.0}%  Bear {stateShare[3]:0.0}%");
				Console.WriteLine("flat = ret/dd of a signal-free haircut matched to the SAME mean exposure");
				Console.WriteLine();
				Console.WriteLine($"{"off in",10} {"bars%",6} {"TiT%",6} {"exp",6} {"Sharpe",8} {"dShp",8} {"MedShp",8} " +
					$"{"Ret%",10} {"DD%",8} {"r/dd",8} {"medR/dd",8} {"flat",8} {"excess",8} {"excW",7} {"ShpW",6} {"DdW",5}");

				void Report(string label, double barPct, List<BankrollResult> rs, bool isBase = false)
				{
					double exp = MeanExposure(rs);
					double sh  = rs.Average(r => Safe(r.SharpeRatio));
					double rpd = RetPerDd(rs);
					double med = Median(rs.Select(r => Safe(r.SharpeRatio)).ToList());
					double medRpd = Median(rs.Select(PerNameRpd).ToList());
					double tit = MeanTit(rs);
					int shW = rs.Where((r, i) => Safe(r.SharpeRatio) > Safe(baseRes[i].SharpeRatio) + 1e-9).Count();
					int ddW = rs.Where((r, i) => Safe(r.MaxDrawdownPct) < Safe(baseRes[i].MaxDrawdownPct) - 1e-9).Count();

					// matched-exposure control (only meaningful when exposure actually moved). excW = the number of
					// names where this config's ret/dd beats the SAME name's ret/dd under the matched flat control,
					// so a large mean excess driven by two outliers can be told apart from a broad one.
					string flat = "", exc = "", excW = "";
					if (Math.Abs(exp - baseExp) > 1e-6 && baseExp > 1e-9)
					{
						double h = SolveHaircut(exp, baseExp, hair => Eval(true, 0, hair));
						var ctrl = Eval(true, 0, h);
						double f = RetPerDd(ctrl);
						flat = $"{f:0.000}"; exc = $"{rpd - f:+0.000;-0.000}";
						int w = rs.Where((r, i) => PerNameRpd(r) > PerNameRpd(ctrl[i]) + 1e-9).Count();
						excW = $"{w}/{rs.Count}";
					}

					Console.WriteLine($"{label,10} {barPct,6:0.0} {tit,6:0.0} {exp,6:0.000} {sh,8:0.000} " +
						$"{(isBase ? "" : (sh - baseSh).ToString("+0.000;-0.000")),8} {med,8:0.000} " +
						$"{rs.Average(r => Safe(r.TotalReturnPct)),10:0.0} {rs.Average(r => Safe(r.MaxDrawdownPct)),8:0.00} " +
						$"{rpd,8:0.000} {medRpd,8:0.000} {flat,8} {exc,8} {excW,7} " +
						$"{(isBase ? "" : shW + "/" + rs.Count),6} {(isBase ? "" : ddW + "/" + rs.Count),5}");
				}

				Report("SHIPPED", 0.0, baseRes, isBase: true);   // reference row: KAMA ramp on everywhere
				Console.WriteLine();

				string[] names = { "Bull", "BullNeut", "BearNeut", "Bear" };
				for (int s = 0; s < 4; s++)
					Report(names[s], stateShare[s], Eval(true, 1 << s, 1.0));

				// combinations suggested by the single-state pass
				Console.WriteLine();
				Report("Bull+Bear", stateShare[0] + stateShare[3], Eval(true, (1 << 0) | (1 << 3), 1.0));
				Report("neutrals", stateShare[1] + stateShare[2], Eval(true, (1 << 1) | (1 << 2), 1.0));
				Report("ALL (off)", 100.0, Eval(false, 0, 1.0));
			}
			finally
			{
				BankrollSimulator.KamaSmooth = savedOn;
				BankrollSimulator.KamaSmoothOffMask = savedMask;
				BankrollSimulator.FlatHaircut = savedHair;
			}
		}

		// KAMA smoothing off in ONE ST state (default ST Bear), results broken out by HV band, shipped
		// alongside. Per-band flat-haircut control included because the exposure change is large and
		// band-dependent. Per-name rows under each band -- the bands are small (1-7 names), so a band
		// mean is only interpretable next to the names that produced it.
		public static async Task ByHv(string interval, int offMask = 1 << 3, string label = "Bear off")
		{
			var barsBySymbol = new Dictionary<string, List<OhlcBar>>();
			foreach (var sym in Symbols)
			{
				try
				{
					var b = (await YahooClient.GetBarsAsync(sym, interval)).Where(x => x.Date >= StartDate).ToList();
					if (b.Count >= 120) barsBySymbol[sym] = b;
				}
				catch { }
			}
			var syms = barsBySymbol.Keys.OrderBy(k => k).ToList();

			bool   savedOn   = BankrollSimulator.KamaSmooth;
			int    savedMask = BankrollSimulator.KamaSmoothOffMask;
			double savedHair = BankrollSimulator.FlatHaircut;

			List<BankrollResult> Eval(int mask, double haircut)
			{
				BankrollSimulator.KamaSmooth = true;
				BankrollSimulator.KamaSmoothOffMask = mask;
				BankrollSimulator.FlatHaircut = haircut;
				return syms.Select(s => BankrollSimulator.Run(barsBySymbol[s], 10_000.0)).ToList();
			}

			try
			{
				var shipped = Eval(0, 1.0);
				var variant = Eval(offMask, 1.0);

				double[] edges = { 0, 30, 50, 75, 100, double.MaxValue };
				string[] bandName = { "<30", "30-50", "50-75", "75-100", "100+" };
				var bandOf = new int[syms.Count];
				for (int s = 0; s < syms.Count; s++)
				{
					double hv = Volatility.AnnualizedHistoricalPct(barsBySymbol[syms[s]]);
					int b = 0; while (b < edges.Length - 2 && hv >= edges[b + 1]) b++;
					bandOf[s] = b;
				}

				Console.WriteLine($"\n===== KAMA SMOOTHING OFF IN ST {label.ToUpper()} — BY HV BAND =====");
				Console.WriteLine($"{syms.Count} symbols | from {StartDate:yyyy-MM-dd} | shipped = KAMA ramp on everywhere");
				Console.WriteLine("flat = ret/dd of a signal-free haircut matched to that BAND's variant exposure");

				for (int b = 0; b < bandName.Length; b++)
				{
					var idx = Enumerable.Range(0, syms.Count).Where(s => bandOf[s] == b).ToList();
					if (idx.Count == 0) continue;

					List<BankrollResult> Sub(List<BankrollResult> all) => idx.Select(i => all[i]).ToList();
					var A = Sub(shipped); var B = Sub(variant);
					double expA = MeanExposure(A), expB = MeanExposure(B);

					// matched-exposure control on this band only
					string flat = "", exc = "";
					if (Math.Abs(expB - expA) > 1e-6 && expA > 1e-9)
					{
						double lo = 0.0, hi = expB > expA ? 4.0 : 1.0, h = 1.0;
						for (int it = 0; it < 20; it++)
						{
							h = 0.5 * (lo + hi);
							double e = MeanExposure(Sub(Eval(0, h)));
							if (e > expB) hi = h; else lo = h;
						}
						double f = RetPerDd(Sub(Eval(0, h)));
						flat = $"{f:0.000}"; exc = $"{RetPerDd(B) - f:+0.000;-0.000}";
					}

					Console.WriteLine($"\n--- HV {bandName[b]}  (n={idx.Count}: {string.Join(", ", idx.Select(i => syms[i]))}) ---");
					Console.WriteLine($"{"config",9} {"TiT%",6} {"exp",6} {"Sharpe",8} {"dShp",8} {"MedShp",8} {"Ret%",10} " +
						$"{"MedRet%",9} {"DD%",8} {"r/dd",8} {"medR/dd",8} {"flat",8} {"excess",8} {"ShpW",6} {"DdW",5}");
					Console.WriteLine($"{"shipped",9} {MeanTit(A),6:0.0} {expA,6:0.000} {A.Average(r => Safe(r.SharpeRatio)),8:0.000} " +
						$"{"",8} {Median(A.Select(r => Safe(r.SharpeRatio)).ToList()),8:0.000} " +
						$"{A.Average(r => Safe(r.TotalReturnPct)),10:0.0} {Median(A.Select(r => Safe(r.TotalReturnPct)).ToList()),9:0.0} " +
						$"{A.Average(r => Safe(r.MaxDrawdownPct)),8:0.00} {RetPerDd(A),8:0.000} {Median(A.Select(PerNameRpd).ToList()),8:0.000}");
					Console.WriteLine($"{label,9} {MeanTit(B),6:0.0} {expB,6:0.000} {B.Average(r => Safe(r.SharpeRatio)),8:0.000} " +
						$"{B.Average(r => Safe(r.SharpeRatio)) - A.Average(r => Safe(r.SharpeRatio)),8:+0.000;-0.000} " +
						$"{Median(B.Select(r => Safe(r.SharpeRatio)).ToList()),8:0.000} " +
						$"{B.Average(r => Safe(r.TotalReturnPct)),10:0.0} {Median(B.Select(r => Safe(r.TotalReturnPct)).ToList()),9:0.0} " +
						$"{B.Average(r => Safe(r.MaxDrawdownPct)),8:0.00} {RetPerDd(B),8:0.000} {Median(B.Select(PerNameRpd).ToList()),8:0.000} " +
						$"{flat,8} {exc,8} " +
						$"{B.Where((r, i) => Safe(r.SharpeRatio) > Safe(A[i].SharpeRatio) + 1e-9).Count() + "/" + idx.Count,6} " +
						$"{B.Where((r, i) => Safe(r.MaxDrawdownPct) < Safe(A[i].MaxDrawdownPct) - 1e-9).Count() + "/" + idx.Count,5}");

					Console.WriteLine($"      {"sym",6} {"shpShp",8} {"varShp",8} {"dShp",8} {"shpRet%",10} {"varRet%",10} {"shpDD%",8} {"varDD%",8} {"shpTiT",7} {"varTiT",7}");
					foreach (var i in idx)
						Console.WriteLine($"      {syms[i],6} {Safe(shipped[i].SharpeRatio),8:0.000} {Safe(variant[i].SharpeRatio),8:0.000} " +
							$"{Safe(variant[i].SharpeRatio) - Safe(shipped[i].SharpeRatio),8:+0.000;-0.000} " +
							$"{Safe(shipped[i].TotalReturnPct),10:0.0} {Safe(variant[i].TotalReturnPct),10:0.0} " +
							$"{Safe(shipped[i].MaxDrawdownPct),8:0.00} {Safe(variant[i].MaxDrawdownPct),8:0.00} " +
							$"{Tit(shipped[i]),7:0.0} {Tit(variant[i]),7:0.0}");
				}
			}
			finally
			{
				BankrollSimulator.KamaSmooth = savedOn;
				BankrollSimulator.KamaSmoothOffMask = savedMask;
				BankrollSimulator.FlatHaircut = savedHair;
			}
		}

		private static double Tit(BankrollResult r) => r.Positions.Count > 0
			? 100.0 * r.Positions.Count(p => Math.Abs(p) > 0.05) / r.Positions.Count : 0.0;

		private static double SolveHaircut(double targetExp, double baseExp, Func<double, List<BankrollResult>> eval)
		{
			double lo = 0.0, hi = targetExp > baseExp ? 4.0 : 1.0;
			for (int i = 0; i < 20; i++)
			{
				double mid = 0.5 * (lo + hi);
				double e = MeanExposure(eval(mid));
				if (e > targetExp) hi = mid; else lo = mid;
				if (Math.Abs(e - targetExp) < 1e-5) return mid;
			}
			return 0.5 * (lo + hi);
		}

		private static double PerNameRpd(BankrollResult r) =>
			Safe(r.MaxDrawdownPct) > 0.01 ? Safe(r.TotalReturnPct) / Safe(r.MaxDrawdownPct) : 0.0;

		private static double RetPerDd(List<BankrollResult> rs) =>
			rs.Average(x => Safe(x.MaxDrawdownPct) > 0.01 ? Safe(x.TotalReturnPct) / Safe(x.MaxDrawdownPct) : 0.0);

		// time in trade: % of bars holding a real position, averaged per name (same >0.05 threshold the
		// options overlay uses). Distinct from mean exposure -- the engine is in the market most of the
		// time but SMALL, so the two move independently and both are needed to read a de-risking change.
		private static double MeanTit(List<BankrollResult> rs) =>
			rs.Average(r => r.Positions.Count > 0
				? 100.0 * r.Positions.Count(p => Math.Abs(p) > 0.05) / r.Positions.Count : 0.0);

		private static double MeanExposure(List<BankrollResult> rs)
		{
			double sum = 0; int n = 0;
			foreach (var r in rs) { foreach (var p in r.Positions) { sum += Math.Abs(p); n++; } }
			return n > 0 ? sum / n : 0.0;
		}

		private static double Safe(double x) => double.IsNaN(x) || double.IsInfinity(x) ? 0.0 : x;

		private static double Median(List<double> xs)
		{
			if (xs.Count == 0) return 0.0;
			var s = xs.OrderBy(x => x).ToList();
			int m = s.Count / 2;
			return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
		}
	}
}
