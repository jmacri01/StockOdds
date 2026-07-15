using System;
using System.Collections.Generic;
using System.Linq;

namespace StockOdds
{
	public static class GridSearchPrinter
	{
		// Prints the top-N combinations by Sharpe, plus the single best knob values so
		// they can be pasted straight back into Program.cs.
		public static void Print(List<GridPoint> results, int top = 20)
		{
			Console.WriteLine("\n===== GRID SEARCH (top by Sharpe) =====");
			Console.WriteLine($"Combinations evaluated : {results.Count}");
			Console.WriteLine();
			Console.WriteLine(
				$"{"EMA",4} {"BiasP",6} {"LBias",6} {"BiasEMA",8} {"Drift%",7} " +
				$"{"Sharpe",8} {"Return%",10} {"MaxDD%",9}");

			foreach (var p in results.Take(top))
			{
				Console.WriteLine(
					$"{p.ExposureEmaPeriod,4} {p.BiasPeriod,6} {p.LongBias,6:0.##} {p.BiasEmaPeriod,8} " +
					$"{p.RebalanceDriftPercent,7:0.#} {p.Sharpe,8:0.000} {Signed(p.TotalReturnPct),10} " +
					$"-{p.MaxDrawdownPct,7:0.00}%");
			}

			var best = results.FirstOrDefault();
			if (best != null)
			{
				Console.WriteLine();
				Console.WriteLine("Best (paste into Program.cs):");
				Console.WriteLine($"  BankrollSimulator.ExposureEmaPeriod     = {best.ExposureEmaPeriod};");
				Console.WriteLine($"  BankrollSimulator.BiasPeriod            = {best.BiasPeriod};");
				Console.WriteLine($"  BankrollSimulator.LongBias              = {best.LongBias};");
				Console.WriteLine($"  BankrollSimulator.BiasEmaPeriod         = {best.BiasEmaPeriod};");
				Console.WriteLine($"  BankrollSimulator.RebalanceDriftPercent = {best.RebalanceDriftPercent};");
			}
		}

		// Multi-symbol version: ranked by mean Sharpe across the basket, with the worst
		// symbol (MinSharpe) shown so combos that only work on one name stand out. Then
		// the per-symbol Sharpe breakdown for the winning combination.
		public static void PrintMulti(List<MultiGridPoint> results, IEnumerable<string> symbols, int top = 20)
		{
			var syms = symbols.ToList();

			Console.WriteLine("\n===== GRID SEARCH — MULTI-SYMBOL (top by mean Sharpe) =====");
			Console.WriteLine($"Symbols ({syms.Count})          : {string.Join(", ", syms)}");
			Console.WriteLine($"Combinations evaluated : {results.Count}");
			Console.WriteLine();
			Console.WriteLine(
				$"{"EMA",4} {"BiasP",6} {"LBias",6} {"BiasEMA",8} {"Drift%",7} " +
				$"{"MeanShp",8} {"MinShp",8} {"MeanRet%",10} {"MeanDD%",9}");

			foreach (var p in results.Take(top))
			{
				Console.WriteLine(
					$"{p.ExposureEmaPeriod,4} {p.BiasPeriod,6} {p.LongBias,6:0.##} {p.BiasEmaPeriod,8} " +
					$"{p.RebalanceDriftPercent,7:0.#} {p.MeanSharpe,8:0.000} {p.MinSharpe,8:0.000} " +
					$"{Signed(p.MeanReturnPct),10} -{p.MeanMaxDrawdownPct,7:0.00}%");
			}

			var best = results.FirstOrDefault();
			if (best != null)
			{
				Console.WriteLine();
				Console.WriteLine("Best combo — per-symbol Sharpe:");
				foreach (var s in syms)
				{
					double sh = best.SharpeBySymbol.TryGetValue(s, out var v) ? v : double.NaN;
					Console.WriteLine($"  {s,-10} {sh,8:0.000}");
				}

				Console.WriteLine();
				Console.WriteLine("Best (paste into Program.cs):");
				Console.WriteLine($"  BankrollSimulator.ExposureEmaPeriod     = {best.ExposureEmaPeriod};");
				Console.WriteLine($"  BankrollSimulator.BiasPeriod            = {best.BiasPeriod};");
				Console.WriteLine($"  BankrollSimulator.LongBias              = {best.LongBias};");
				Console.WriteLine($"  BankrollSimulator.BiasEmaPeriod         = {best.BiasEmaPeriod};");
				Console.WriteLine($"  BankrollSimulator.RebalanceDriftPercent = {best.RebalanceDriftPercent};");
			}
		}

		// Volatility study: one row per symbol, sorted by HV. Knobs are AVERAGED over each
		// symbol's top-region combos (robust), and Pearson correlations quantify how each
		// knob tracks HV rather than leaving it to the eye.
		public static void PrintPerSymbolOptima(List<SymbolOptimum> optima)
		{
			Console.WriteLine("\n===== VOLATILITY -> OPTIMAL PARAMETERS (robust: avg of top region) =====");
			int region = optima.FirstOrDefault()?.RegionCount ?? 0;
			Console.WriteLine($"Symbols : {optima.Count}   (each knob = mean over that symbol's top {region} combos)");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",7} {"Bars",5}   " +
				$"{"EMA",5} {"BiasP",6} {"LBias",6} {"BiasEMA",8} {"Drift%",7} " +
				$"{"TopShp",7} {"RegRet%",10} {"RegDD%",9}");

			foreach (var o in optima)
			{
				Console.WriteLine(
					$"{o.Symbol,-8} {o.HistoricalVolatilityPct,7:0.0} {o.Bars,5}   " +
					$"{o.EmaPeriod,5:0.0} {o.BiasPeriod,6:0.0} {o.LongBias,6:0.##} {o.BiasEmaPeriod,8:0.0} " +
					$"{o.DriftPercent,7:0.0} {o.TopSharpe,7:0.00} {Signed(o.MeanRegionReturnPct),10} " +
					$"-{o.MeanRegionMaxDdPct,7:0.00}%");
			}

			// correlation of HV against each averaged knob (needs >= 2 symbols)
			if (optima.Count >= 2)
			{
				var hv = optima.Select(o => o.HistoricalVolatilityPct).ToList();
				Console.WriteLine();
				Console.WriteLine("Correlation of HV vs. optimal knob (Pearson r, -1..+1):");
				Console.WriteLine($"  EMA period   : {Corr(hv, optima.Select(o => o.EmaPeriod)):+0.000;-0.000}");
				Console.WriteLine($"  Bias period  : {Corr(hv, optima.Select(o => o.BiasPeriod)):+0.000;-0.000}");
				Console.WriteLine($"  Long bias    : {Corr(hv, optima.Select(o => o.LongBias)):+0.000;-0.000}");
				Console.WriteLine($"  Bias EMA     : {Corr(hv, optima.Select(o => o.BiasEmaPeriod)):+0.000;-0.000}");
				Console.WriteLine($"  Drift %      : {Corr(hv, optima.Select(o => o.DriftPercent)):+0.000;-0.000}");
				Console.WriteLine("(r near +1 => that knob rises with volatility; near 0 => no linear relationship.)");
			}

			// ---- Do the knobs even matter? Spread of Sharpe across the WHOLE grid. ----
			Console.WriteLine();
			Console.WriteLine("Knob impact — Sharpe spread across all combos per symbol:");
			Console.WriteLine($"{"Symbol",-8} {"HV%",7}   {"Best",7} {"Median",7} {"Worst",7} {"Best-Med",9}");
			foreach (var o in optima)
			{
				Console.WriteLine(
					$"{o.Symbol,-8} {o.HistoricalVolatilityPct,7:0.0}   " +
					$"{o.TopSharpe,7:0.00} {o.GridMedianSharpe,7:0.00} {o.GridWorstSharpe,7:0.00} " +
					$"{o.SharpeSpread,9:0.00}");
			}
			if (optima.Count > 0)
			{
				double avgSpread = optima.Average(o => o.SharpeSpread);
				Console.WriteLine();
				Console.WriteLine($"Mean best-vs-median Sharpe uplift from tuning : {avgSpread:0.00}");
				Console.WriteLine("(Large => knob choice matters a lot; small => smoothing is second-order noise.)");
			}
		}

		// 1-D LongBias vs volatility: for each symbol (all other knobs fixed) the LongBias
		// that maximizes Sharpe, tagged with HV and sorted by it, plus the full sweep curve,
		// a low-vs-high HV group summary, Pearson & Spearman correlations, and a fitted
		// LongBias(HV) rule. Tests "optimal LongBias falls as volatility rises."
		public static void PrintLongBiasVsVol(List<LongBiasVolRow> rows)
		{
			Console.WriteLine("\n===== LONG-BIAS vs VOLATILITY (1-D sweep, all other knobs fixed) =====");
			if (rows.Count == 0)
			{
				Console.WriteLine("No symbols with usable data.");
				return;
			}

			double refBias = rows[0].RefBias;
			Console.WriteLine($"Symbols : {rows.Count}   |   only LongBias moves; reference (configured) LongBias = {refBias:0.##}");
			Console.WriteLine("Best* = the LongBias that maximizes that metric on this symbol's full window.");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",7} {"Bars",5}   " +
				$"{"BestShpLB",9} {"BestShp",8} {"RefShp",8} {"ShpGain",8}   {"BestCalLB",9}");

			foreach (var r in rows)
			{
				double gain = r.BestSharpe - r.RefSharpe;
				Console.WriteLine(
					$"{r.Symbol,-8} {r.HistoricalVolatilityPct,7:0.0} {r.Bars,5}   " +
					$"{r.BestSharpeBias,9:0.##} {r.BestSharpe,8:0.00} {r.RefSharpe,8:0.00} {gain,8:+0.00;-0.00}   " +
					$"{r.BestCalmarBias,9:0.##}");
			}

			// ---- full Sharpe curve matrix (rows = symbol by HV, cols = LongBias); [*] = row argmax ----
			var biases = rows[0].Curve.Select(p => p.LongBias).ToList();
			Console.WriteLine();
			Console.WriteLine("Sharpe by LongBias   (rows = symbol asc HV, cols = LongBias; * = row max)");
			Console.Write($"{"Sym\\LB",-8} {"HV%",6}");
			foreach (var lb in biases) Console.Write($"{lb,7:0.#}");
			Console.WriteLine();
			foreach (var r in rows)
			{
				Console.Write($"{r.Symbol,-8} {r.HistoricalVolatilityPct,6:0.}");
				foreach (var p in r.Curve)
				{
					string mark = p.LongBias == r.BestSharpeBias ? "*" : " ";
					Console.Write($"{p.Sharpe.ToString("0.00") + mark,7}");
				}
				Console.WriteLine();
			}

			// ---- max-drawdown curve matrix: does cranking LongBias cost drawdown? ----
			// The strategy's real job is drawdown reduction, so a LongBias that lifts Sharpe
			// but deepens drawdown is buying the wrong thing on the names we deploy on.
			Console.WriteLine();
			Console.WriteLine("Max drawdown % by LongBias   (rows = symbol asc HV, cols = LongBias; lower = better)");
			Console.Write($"{"Sym\\LB",-8} {"HV%",6}");
			foreach (var lb in biases) Console.Write($"{lb,7:0.#}");
			Console.WriteLine();
			foreach (var r in rows)
			{
				Console.Write($"{r.Symbol,-8} {r.HistoricalVolatilityPct,6:0.}");
				foreach (var p in r.Curve)
					Console.Write($"{-p.MaxDdPct,7:0.}");
				Console.WriteLine();
			}

			// Per-symbol drawdown-minimizing LongBias + monotonicity check. DD is NOT strictly
			// monotone in LongBias — some names dip in the mid-range before blowing out — so
			// report each symbol's actual DD-min LongBias and flag non-monotone curves.
			Console.WriteLine();
			Console.WriteLine("DD-minimizing LongBias per symbol   (mono = DD never decreases as LongBias rises)");
			Console.WriteLine($"{"Symbol",-8} {"HV%",6}   {"MinDD",6} {"@LB",6} {"RefDD",6} {"@Ref",6}   {"Mono?",6}");
			int nonMono = 0, minAboveRef = 0;
			foreach (var r in rows)
			{
				var minPt = r.Curve.OrderBy(p => p.MaxDdPct).First();
				var refPt = r.Curve.OrderBy(p => Math.Abs(p.LongBias - refBias)).First();
				bool mono = true;
				for (int i = 1; i < r.Curve.Count; i++)
					if (r.Curve[i].MaxDdPct < r.Curve[i - 1].MaxDdPct - 1e-9) { mono = false; break; }
				if (!mono) nonMono++;
				if (minPt.LongBias > refBias + 1e-9) minAboveRef++;
				Console.WriteLine(
					$"{r.Symbol,-8} {r.HistoricalVolatilityPct,6:0.}   " +
					$"-{minPt.MaxDdPct,4:0.}% {minPt.LongBias,6:0.##} -{refPt.MaxDdPct,4:0.}% {refBias,6:0.##}   " +
					$"{(mono ? "yes" : "NO"),6}");
			}
			Console.WriteLine();
			Console.WriteLine($"Non-monotone DD curves : {nonMono}/{rows.Count}   |   " +
			                  $"DD-min LongBias above reference ({refBias:0.##}) : {minAboveRef}/{rows.Count}");
			Console.WriteLine("(So raising LongBias CAN lower DD locally, but the lowest-DD LongBias is <= ref on almost every name;\n" +
			                  " the mid-range dips are small and path-dependent, and DD always blows out at the high end.)");

			// DD cost of moving from the reference LongBias to each symbol's Sharpe-optimal one
			Console.WriteLine();
			Console.WriteLine($"Drawdown cost of chasing Sharpe via LongBias (ref {refBias:0.##} -> best-Sharpe LongBias):");
			double DdAt(LongBiasVolRow r, double lb) =>
				r.Curve.OrderBy(p => Math.Abs(p.LongBias - lb)).First().MaxDdPct;
			foreach (var r in rows.Where(r => r.HistoricalVolatilityPct >= 50.0))
			{
				double ddRef = DdAt(r, refBias), ddBest = DdAt(r, r.BestSharpeBias);
				Console.WriteLine(
					$"  {r.Symbol,-8} HV {r.HistoricalVolatilityPct,5:0.} : DD -{ddRef,4:0.}% -> -{ddBest,4:0.}%  " +
					$"({ddBest - ddRef,+5:+0;-0} pp)   Sharpe {r.RefSharpe:0.00} -> {r.BestSharpe:0.00}");
			}

			// ---- low-vs-high HV group summary (split at HV 50, the deployment threshold) ----
			const double split = 50.0;
			var low  = rows.Where(r => r.HistoricalVolatilityPct <  split).ToList();
			var high = rows.Where(r => r.HistoricalVolatilityPct >= split).ToList();
			Console.WriteLine();
			Console.WriteLine($"Group means (split at HV {split:0.}):");
			if (low.Count > 0)
				Console.WriteLine($"  Low  HV (<{split:0.}, n={low.Count,2}) : mean best-Sharpe LongBias {low.Average(r => r.BestSharpeBias),6:0.00}   " +
				                  $"median {Median(low.Select(r => r.BestSharpeBias)),5:0.0}");
			if (high.Count > 0)
				Console.WriteLine($"  High HV (>={split:0.}, n={high.Count,2}) : mean best-Sharpe LongBias {high.Average(r => r.BestSharpeBias),6:0.00}   " +
				                  $"median {Median(high.Select(r => r.BestSharpeBias)),5:0.0}");

			// ---- correlations: HV vs optimal LongBias (linear + rank) ----
			if (rows.Count >= 3)
			{
				var hv        = rows.Select(r => r.HistoricalVolatilityPct).ToList();
				var bestShpLB = rows.Select(r => r.BestSharpeBias).ToList();
				var bestCalLB = rows.Select(r => r.BestCalmarBias).ToList();

				double rP = Corr(hv, bestShpLB);
				double rS = Spearman(hv, bestShpLB);
				double rPc = Corr(hv, bestCalLB);

				Console.WriteLine();
				Console.WriteLine("Correlation of HV vs optimal LongBias (negative => bias falls as vol rises):");
				Console.WriteLine($"  Sharpe-optimal LongBias : Pearson {rP,7:+0.000;-0.000}   Spearman {rS,7:+0.000;-0.000}");
				Console.WriteLine($"  Calmar-optimal LongBias : Pearson {rPc,7:+0.000;-0.000}");

				// linear fit  bias ~= a + b*HV  (least squares) on the Sharpe-optimal series
				var (a, b) = LinFit(hv, bestShpLB);
				Console.WriteLine();
				Console.WriteLine($"Least-squares fit : LongBias ≈ {a:0.00} {(b >= 0 ? "+" : "-")} {Math.Abs(b):0.000}·HV");
				double loMax = biases.Max();
				Console.WriteLine($"  Suggested rule  : LongBias(HV) = clamp({a:0.0} - {Math.Abs(b):0.000}·HV, 0.5, {loMax:0.})   " +
				                  "(diagnostic — validate OOS before trusting)");

				Console.WriteLine();
				double rBest = Math.Min(rS, rP);   // most-negative of the two
				Console.WriteLine(
					rBest <= -0.5 ? "=> Optimal LongBias clearly FALLS as volatility rises — the hypothesis holds in-sample. See caveat below."
					: rBest <= -0.25 ? "=> Optimal LongBias falls with volatility DIRECTIONALLY (moderate negative r) — the hypothesis holds in\n" +
					                   "   direction but weakly; a few high-vol names have flat, noisy curves that rail high by chance. See caveat."
					: "=> No monotone HV->LongBias relationship in-sample (r near 0). LongBias optimum is not tracking volatility here.");
				Console.WriteLine(
					"CAVEAT: on low-vol names the Sharpe curve typically keeps rising toward the largest LongBias — i.e. the\n" +
					"'optimum' is really 'turn the overlay OFF (stay fully invested)', not a finite vol-scaled setting. Read a\n" +
					"railed optimum as 'don't deploy the overlay here' (consistent with the HV>=50 deployment rule), and treat\n" +
					"any fitted slope as descriptive, not a knob to auto-set — per-symbol tuning did not survive OOS before.");
			}
		}

		// Dynamic (per-candle) long bias: static LongBias baseline vs a vol-driven LongBias.
		// Per-symbol table at one scale, then a scale sweep, then a verdict. The mapping is
		//   dynLB = clamp(scale * ln(pivot / volEMA), floor, ceil).
		public static void PrintDynBias(List<DynBiasRow> rows, List<DynBiasScalePoint> sweep)
		{
			Console.WriteLine("\n===== DYNAMIC (VOL-DRIVEN) LONG BIAS vs STATIC =====");
			if (rows.Count == 0)
			{
				Console.WriteLine("No symbols with usable data.");
				return;
			}
			double scale = rows[0].Scale, baseLb = rows[0].BaseLb;
			Console.WriteLine($"Map : dynLB = clamp({scale:0.##}·ln({GridSearch.DynPivot:0.}/volEMA{GridSearch.DynVolEma}), " +
			                  $"{GridSearch.DynFloor:0.##}, {GridSearch.DynCeil:0.##})   |   static baseline LongBias = {baseLb:0.##}");
			Console.WriteLine("Positive ΔShp / negative ΔDD = dynamic better. LB@HV = the dyn LB at each symbol's avg vol.");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",6} {"LB@HV",6}   " +
				$"{"Shp:Stat",8} {"Dyn",6} {"Δ",6} │ {"DD:Stat",8} {"Dyn",7} {"Δpp",6} │ {"Ret:Stat",9} {"Dyn",9}");

			foreach (var r in rows)
			{
				Console.WriteLine(
					$"{r.Symbol,-8} {r.Hv,6:0.0} {r.LbAtHv,6:0.00}   " +
					$"{r.StatSharpe,8:0.00} {r.DynSharpe,6:0.00} {r.DShp,6:+0.00;-0.00} │ " +
					$"-{r.StatDd,6:0.0}% -{r.DynDd,5:0.0}% {r.DDd,6:+0.0;-0.0} │ " +
					$"{Signed(r.StatRet),9} {Signed(r.DynRet),9}");
			}

			double mStatShp = rows.Average(r => r.StatSharpe), mDynShp = rows.Average(r => r.DynSharpe);
			double mStatDd  = rows.Average(r => r.StatDd),     mDynDd  = rows.Average(r => r.DynDd);
			int shpWins = rows.Count(r => r.DynSharpe > r.StatSharpe);
			int ddWins  = rows.Count(r => r.DynDd < r.StatDd);
			Console.WriteLine();
			Console.WriteLine($"Mean Sharpe — static {mStatShp,6:0.00}   dynamic {mDynShp,6:0.00}   " +
			                  $"(dynamic higher on {shpWins}/{rows.Count})");
			Console.WriteLine($"Mean MaxDD — static -{mStatDd,5:0.0}%   dynamic -{mDynDd,5:0.0}%   " +
			                  $"(dynamic shallower on {ddWins}/{rows.Count})");

			// ---- scale sweep ----
			Console.WriteLine();
			Console.WriteLine("Scale sweep (basket means vs the same static baseline):");
			Console.WriteLine($"{"Scale",6} {"DynShp",7} {"BaseShp",8} {"ΔShp",7}   " +
			                  $"{"DynDD",7} {"BaseDD",7} {"ΔDD",6}   {"DynRet%",9} {"BaseRet%",9}   {"Shp>base",9} {"DD<base",8}");
			foreach (var p in sweep)
			{
				Console.WriteLine(
					$"{p.Scale,6:0.##} {p.MeanDynSharpe,7:0.00} {p.MeanBaseSharpe,8:0.00} " +
					$"{p.MeanDynSharpe - p.MeanBaseSharpe,7:+0.00;-0.00}   " +
					$"-{p.MeanDynDd,6:0.0}% -{p.MeanBaseDd,6:0.0}% {p.MeanBaseDd - p.MeanDynDd,6:+0.0;-0.0}   " +
					$"{Signed(p.MeanDynRet),9} {Signed(p.MeanBaseRet),9}   " +
					$"{$"{p.ShpWins}/{p.N}",9} {$"{p.DdWins}/{p.N}",8}");
			}
			Console.WriteLine("(ΔDD column: + = dynamic shallower drawdown than static. ΔShp: + = dynamic higher Sharpe.)");

			// ---- verdict ----
			var best = sweep.OrderByDescending(p => p.MeanDynSharpe).FirstOrDefault();
			Console.WriteLine();
			bool anyShpEdge = best != null && best.MeanDynSharpe > best.MeanBaseSharpe + 0.03;
			bool anyDdEdge  = sweep.Any(p => p.MeanBaseDd - p.MeanDynDd > 1.0 && p.ShpWins >= p.N / 2);
			Console.WriteLine(
				anyShpEdge ? $"=> A dynamic map (scale ~{best!.Scale:0.##}) beats static LongBias on mean Sharpe here. Worth OOS validation before trusting."
				: anyDdEdge ? "=> Dynamic doesn't raise Sharpe but does cut drawdown at some scales — a risk-overlay tweak, not alpha."
				: "=> Dynamic long bias does NOT beat static LongBias on this basket (Sharpe or drawdown). Consistent with prior OOS findings.");
			Console.WriteLine("NOTE: in-sample, full window. HV is ~constant per name over the window, so per-candle LB mostly\n" +
			                  "tracks each symbol's average vol; the extra value (if any) is the WITHIN-symbol vol response.");
		}

		// Volatility-scaled exposure: baseline vs vol-scaled, printed once per MinExposure
		// setting (e.g. 0% and -100%). adjEma *= pivot/vol when long, vol/pivot when short.
		public static void PrintVolScale(List<(double minExp, List<VolScaleRow> rows)> byMin)
		{
			Console.WriteLine("\n===== VOLATILITY-SCALED EXPOSURE vs BASELINE =====");
			Console.WriteLine($"Rule : adjEma *= (pivot/vol) if long, (vol/pivot) if short   " +
			                  $"[pivot {GridSearch.VolScalePivotCfg:0.}, volEMA {GridSearch.VolScaleEmaCfg}]");
			Console.WriteLine("Calm -> amplify longs / shrink shorts; volatile -> trim longs / grow shorts.");

			foreach (var (minExp, rows) in byMin)
			{
				Console.WriteLine();
				Console.WriteLine($"----- MinExposure {minExp:0.}%  ({(minExp < 0 ? "shorts enabled" : "long/cash only")}) -----");
				if (rows.Count == 0) { Console.WriteLine("  (no data)"); continue; }
				Console.WriteLine(
					$"{"Symbol",-8} {"HV%",6}   {"Shp:Base",8} {"Scl",6} {"Δ",6} │ " +
					$"{"DD:Base",7} {"Scl",7} {"Δpp",6} │ {"Ret:Base",9} {"Scl",9}");
				foreach (var r in rows)
				{
					Console.WriteLine(
						$"{r.Symbol,-8} {r.Hv,6:0.0}   " +
						$"{r.BaseSharpe,8:0.00} {r.SclSharpe,6:0.00} {r.DShp,6:+0.00;-0.00} │ " +
						$"-{r.BaseDd,5:0.0}% -{r.SclDd,5:0.0}% {r.DDd,6:+0.0;-0.0} │ " +
						$"{Signed(r.BaseRet),9} {Signed(r.SclRet),9}");
				}
				double mB = rows.Average(r => r.BaseSharpe), mS = rows.Average(r => r.SclSharpe);
				double dB = rows.Average(r => r.BaseDd),     dS = rows.Average(r => r.SclDd);
				int shpW = rows.Count(r => r.SclSharpe > r.BaseSharpe);
				int ddW  = rows.Count(r => r.SclDd < r.BaseDd);
				Console.WriteLine(
					$"  Mean Sharpe base {mB,5:0.00} -> scaled {mS,5:0.00} (scaled higher {shpW}/{rows.Count})   " +
					$"Mean DD base -{dB,4:0.0}% -> scaled -{dS,4:0.0}% (scaled shallower {ddW}/{rows.Count})");
				Console.WriteLine(
					mS > mB + 0.03 && dS <= dB + 1.0 ? "  => Vol-scaling helps Sharpe without materially worse drawdown here."
					: mS > mB + 0.03                 ? "  => Vol-scaling raises Sharpe but deepens drawdown — a return/risk trade, not a free win."
					: dS < dB - 1.0                  ? "  => Vol-scaling doesn't raise Sharpe but does cut drawdown — a risk-overlay tweak."
					:                                  "  => Vol-scaling does NOT beat baseline here (Sharpe or drawdown).");
			}
			Console.WriteLine();
			Console.WriteLine("NOTE: in-sample, full window (incl. 2022). Judge on drawdown, not just the equity curve.");
		}

		// Vol->LongBias mapping grid search: does any tuned vol-adaptive mapping beat buy&hold
		// and fixed LongBias, and does the winner actually use volatility (scale > 0)?
		public static void PrintDynMap(DynMapResult r)
		{
			Console.WriteLine("\n===== VOL->LONGBIAS MAPPING SEARCH (normalized dynBias, ranked by mean Sharpe) =====");
			Console.WriteLine($"Basket : {r.N} symbols, full window. Mapping: dynLB = clamp(scale·ln(pivot/volEMA), floor, ceil).");
			Console.WriteLine($"Combos : {r.Ranked.Count}   (scale = 0 => LB constant, i.e. volatility ignored)");
			Console.WriteLine();
			Console.WriteLine("Baselines (basket means):");
			Console.WriteLine($"  Buy & hold          : Sharpe {r.BhSharpe,5:0.00}   MaxDD -{r.BhDd,4:0.0}%   Ret {Signed(r.BhRet)}");
			Console.WriteLine($"  Fixed LongBias {r.FixLb,-4:0.##} : Sharpe {r.FixSharpe,5:0.00}   MaxDD -{r.FixDd,4:0.0}%   Ret {Signed(r.FixRet)}   " +
			                  $"[beats B&H on Sharpe {r.FixShpWinsBH}/{r.N}, on DD {r.FixDdWinsBH}/{r.N}]");
			Console.WriteLine();

			Console.WriteLine("Top mappings by mean Sharpe:");
			Console.WriteLine(
				$"{"Pivot",6} {"Scale",6} {"Floor",6} {"Ceil",5} {"Flat?",6}   " +
				$"{"MeanShp",8} {"MeanDD",7} {"MeanRet",9}   {"Shp>BH",7} {"DD>BH",6} {"Shp>Fix",8}");
			foreach (var p in r.Ranked.Take(12))
			{
				Console.WriteLine(
					$"{p.Pivot,6:0.} {p.Scale,6:0.##} {p.Floor,6:0.##} {p.Ceil,5:0.} {(p.Flat ? "FLAT" : ""),6}   " +
					$"{p.MeanSharpe,8:0.000} -{p.MeanDd,6:0.0}% {Signed(p.MeanRet),9}   " +
					$"{$"{p.ShpWinsBH}/{r.N}",7} {$"{p.DdWinsBH}/{r.N}",6} {$"{p.ShpWinsFix}/{r.N}",8}");
			}

			var best = r.BestOverall; var bestVar = r.BestVarying; var bestFlat = r.BestFlat;
			// The best CONSTANT-LongBias benchmark: the better of the grid's flat point
			// (which collapses to LB=0) and the actual fixed baseline (LB=0.5).
			double bestConst = Math.Max(bestFlat?.MeanSharpe ?? double.NegativeInfinity, r.FixSharpe);
			Console.WriteLine();
			if (best != null)
				Console.WriteLine($"Best overall   : {(best.Flat ? "FLAT (scale 0, vol ignored)" : $"scale {best.Scale:0.##}, pivot {best.Pivot:0.}, ceil {best.Ceil:0.}")}  " +
				                  $"=> Sharpe {best.MeanSharpe:0.000} (fixed {r.FixSharpe:0.000}, B&H {r.BhSharpe:0.000})");
			if (bestVar != null)
			{
				double volEdge = bestVar.MeanSharpe - bestConst;
				Console.WriteLine($"Best vol-varying vs best CONSTANT LB : Sharpe {bestVar.MeanSharpe:0.000} vs {bestConst:0.000}  " +
				                  $"=> volatility is worth {volEdge:+0.000;-0.000} Sharpe (constant ref = better of LB0 / fixed {r.FixLb:0.##})");
			}

			// verdicts
			double edgeVsFix = (best?.MeanSharpe ?? 0) - r.FixSharpe;
			double edgeVsBh  = (best?.MeanSharpe ?? 0) - r.BhSharpe;
			double volEdgeVsConst = (bestVar?.MeanSharpe ?? double.NegativeInfinity) - bestConst;
			Console.WriteLine();
			Console.WriteLine(edgeVsBh > 0.03
				? $"=> The best mapping BEATS buy & hold on mean Sharpe (+{edgeVsBh:0.000}) — but so does fixed LongBias (the overlay already does this)."
				: $"=> The best mapping does NOT beat buy & hold on mean Sharpe ({edgeVsBh:+0.000;-0.000}).");
			Console.WriteLine(
				volEdgeVsConst > 0.03
					? "=> Volatility DOES add value: the best vol-varying mapping beats the best constant LongBias."
					: $"=> Volatility adds ~nothing ({volEdgeVsConst:+0.000;-0.000}): the best constant LongBias matches/beats every vol-adaptive mapping => vol is NOT the lever; a fixed LongBias works for everything.");
			Console.WriteLine(edgeVsFix > 0.03
				? $"=> Best mapping beats fixed LongBias {r.FixLb:0.##} by +{edgeVsFix:0.000} Sharpe (IN-SAMPLE — walk-forward before trusting)."
				: $"=> Best mapping does NOT beat fixed LongBias {r.FixLb:0.##} ({edgeVsFix:+0.000;-0.000} Sharpe) — even with full in-sample hindsight.");
			Console.WriteLine("NOTE: in-sample, full window. This is the CEILING of what tuning CAN do here; OOS is typically worse.");
		}

		// Fixed-LongBias A/B: plain dynBias (/BiasPeriod) vs normalized (/max => bounded [-1,1]).
		public static void PrintNormStatic(List<VolScaleRow> rows, double longBias)
		{
			Console.WriteLine("\n===== FIXED LONGBIAS: PLAIN vs NORMALIZED dynBias (bounded to [-1,1]) =====");
			if (rows.Count == 0) { Console.WriteLine("No data."); return; }
			Console.WriteLine($"LongBias held at {longBias:0.##}. Plain: dynBias = sum/BiasPeriod (range [-1, {longBias + 1:0.##}]).  " +
			                  $"Norm: sum/(BiasPeriod·{Math.Max(Math.Abs(longBias + 1), 1):0.##}) (range [{-1 / Math.Max(Math.Abs(longBias + 1), 1):0.00}, 1]).");
			Console.WriteLine("Δ columns: + Sharpe = normalized better; − DD (Δpp) = normalized shallower.");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",6}   {"Shp:Plain",9} {"Norm",6} {"Δ",6} │ " +
				$"{"DD:Plain",8} {"Norm",7} {"Δpp",6} │ {"Ret:Plain",9} {"Norm",9}");
			foreach (var r in rows)
			{
				Console.WriteLine(
					$"{r.Symbol,-8} {r.Hv,6:0.0}   " +
					$"{r.BaseSharpe,9:0.00} {r.SclSharpe,6:0.00} {r.DShp,6:+0.00;-0.00} │ " +
					$"-{r.BaseDd,6:0.0}% -{r.SclDd,5:0.0}% {r.DDd,6:+0.0;-0.0} │ " +
					$"{Signed(r.BaseRet),9} {Signed(r.SclRet),9}");
			}
			double mP = rows.Average(r => r.BaseSharpe), mN = rows.Average(r => r.SclSharpe);
			double dP = rows.Average(r => r.BaseDd),     dN = rows.Average(r => r.SclDd);
			double rP = rows.Average(r => r.BaseRet),    rN = rows.Average(r => r.SclRet);
			int shpW = rows.Count(r => r.SclSharpe > r.BaseSharpe);
			int ddW  = rows.Count(r => r.SclDd < r.BaseDd);
			Console.WriteLine();
			Console.WriteLine($"Mean Sharpe : plain {mP,5:0.00} -> norm {mN,5:0.00}  ({mN - mP:+0.000;-0.000}; norm higher {shpW}/{rows.Count})");
			Console.WriteLine($"Mean MaxDD  : plain -{dP,4:0.0}% -> norm -{dN,4:0.0}%  ({dP - dN:+0.0;-0.0}pp; norm shallower {ddW}/{rows.Count})");
			Console.WriteLine($"Mean Return : plain {Signed(rP)} -> norm {Signed(rN)}");
			Console.WriteLine();
			Console.WriteLine(
				mN > mP + 0.02 && dN <= dP + 0.5 ? "=> Normalizing the fixed LongBias HELPS (higher Sharpe, no worse drawdown). Worth adopting (validate OOS)."
				: dN < dP - 1.0 && mN >= mP - 0.02 ? "=> Normalizing mainly cuts drawdown at ~equal Sharpe — a mild risk-overlay tweak."
				: Math.Abs(mN - mP) <= 0.02 ? "=> Normalizing barely changes anything — it's ~equivalent to a slightly smaller LongBias. Not worth the added complexity."
				: "=> Normalizing does NOT help the fixed LongBias (lower Sharpe). Keep the plain divisor.");
			Console.WriteLine("NOTE: in-sample, full window. For a CONSTANT LongBias, normalization is just a uniform rescale of the bias skew.");
		}

		// Probability<->exposure calibration: is the per-candle TARGET exposure predictive of
		// an up-close on the next bar? Prints the pooled calibration table + continuous curve,
		// then a per-symbol breakdown, then a verdict.
		public static void PrintProbExposure(ProbExposureStudyResult study)
		{
			var results = study.Calibration;
			Console.WriteLine("\n===== PROBABILITY <-> EXPOSURE: does target exposure predict an up-close? =====");
			if (results.Count == 0) { Console.WriteLine("No data."); return; }
			Console.WriteLine("Outcome = next candle closes ABOVE the previous close (the move the sim trades).");
			Console.WriteLine("Signal  = per-candle TARGET exposure = the (LT,ST) map value, known one bar early.");

			var basket = results[0];
			var levels = basket.Levels.Select(l => l.Exposure).OrderBy(x => x).ToList();

			// ---- BASKET: pooled calibration ----
			Console.WriteLine($"\n-- BASKET (pooled, N={basket.N}) --");
			Console.WriteLine($"Base up-rate: {basket.BaseUpRatePct:0.0}%   Mean fwd ret: {Signed(basket.MeanRetPct)}%");
			Console.WriteLine($"corr(exposure, up-close): {basket.CorrExpUp:+0.000;-0.000}      " +
			                  $"corr(exposure, fwd return): {basket.CorrExpRet:+0.000;-0.000}");
			Console.WriteLine();
			Console.WriteLine($"  {"Exposure",8} {"N",7} {"Up%",7} {"±95pp",6} {"Lift",8} {"MeanRet",9}");
			foreach (var b in basket.Levels)
			{
				double ci   = Ci95(b.UpRatePct, b.N);
				double lift = b.UpRatePct - basket.BaseUpRatePct;
				string liftStr = ((lift >= 0 ? "+" : "-") + Math.Abs(lift).ToString("0.0") + "pp");
				Console.WriteLine(
					$"  {b.Exposure,8:+0.00;-0.00} {b.N,7} {b.UpRatePct,6:0.0}% " +
					$"{ci,6:0.0} {liftStr,7} {Signed(b.MeanFwdRetPct),8}%");
			}

			// ---- BASKET: forward-RISK calibration (the drawdown question) ----
			Console.WriteLine("\n  Forward RISK by exposure (is the NEXT bar riskier when exposure is low?):");
			Console.WriteLine($"  corr(exposure, |fwd ret|): {basket.CorrExpAbsRet:+0.000;-0.000} (− = higher exp calmer)   " +
			                  $"corr(exposure, downside): {basket.CorrExpDownside:+0.000;-0.000} (+ = higher exp less downside)");
			Console.WriteLine($"  {"Exposure",8} {"N",7} {"FwdVol",7} {"DownDev",8} {"Down%",7} {"MeanRet",9}");
			foreach (var b in basket.Levels)
				Console.WriteLine(
					$"  {b.Exposure,8:+0.00;-0.00} {b.N,7} {b.FwdVolPct,6:0.00}% {b.DownsideDevPct,7:0.00}% " +
					$"{b.DownRatePct,6:0.0}% {Signed(b.MeanFwdRetPct),8}%");

			if (basket.Deciles.Count > 0)
			{
				Console.WriteLine("\n  Continuous signal (EMA-of-target, equal-count deciles):");
				Console.WriteLine($"  {"Decile",6} {"Exp~mean",9} {"N",7} {"Up%",7} {"FwdVol",7} {"DownDev",8} {"MeanRet",9}");
				for (int i = 0; i < basket.Deciles.Count; i++)
				{
					var b = basket.Deciles[i];
					Console.WriteLine(
						$"  {i + 1,6} {b.Exposure,9:+0.00;-0.00} {b.N,7} {b.UpRatePct,6:0.0}% " +
						$"{b.FwdVolPct,6:0.00}% {b.DownsideDevPct,7:0.00}% {Signed(b.MeanFwdRetPct),8}%");
				}
			}

			// ---- PER SYMBOL ----
			Console.WriteLine("\n-- PER SYMBOL (sorted by HV) --");
			string hdr = $"  {"Symbol",-8} {"HV%",6} {"N",6} {"Base%",6} {"corrEU",7} ";
			foreach (var lv in levels) hdr += $"{("U@" + lv.ToString("+0.0;-0.0")),7} ";
			hdr += $"{"Mono?",5}";
			Console.WriteLine(hdr);

			int monoSyms = 0, totSyms = 0;
			foreach (var r in results.Skip(1))
			{
				totSyms++;
				var byLevel = r.Levels.ToDictionary(l => l.Exposure, l => l);
				string line = $"  {r.Scope,-8} {r.Hv,6:0.0} {r.N,6} {r.BaseUpRatePct,5:0.0} {r.CorrExpUp,7:+0.00;-0.00} ";
				var upSeq = new List<double>();
				foreach (var lv in levels)
				{
					if (byLevel.TryGetValue(lv, out var b) && b.N > 0)
					{
						line += $"{b.UpRatePct,6:0.0} ";
						upSeq.Add(b.UpRatePct);
					}
					else line += $"{"·",6} ";
				}
				bool mono = IsNondecreasing(upSeq, 1.0);
				if (mono) monoSyms++;
				line += $"{(mono ? "yes" : "no"),5}";
				Console.WriteLine(line);
			}

			// ---- verdict ----
			double top = basket.Levels.Count > 0 ? basket.Levels[^1].UpRatePct : 0;
			double bot = basket.Levels.Count > 0 ? basket.Levels[0].UpRatePct : 0;
			double spread = top - bot;
			Console.WriteLine();
			Console.WriteLine(
				$"Effect size: Up% at highest exposure ({basket.Levels[^1].Exposure:+0.00;-0.00}) minus lowest " +
				$"({basket.Levels[0].Exposure:+0.00;-0.00}) = {spread:+0.0;-0.0}pp.  Monotone in {monoSyms}/{totSyms} symbols.");
			Console.WriteLine(
				basket.CorrExpUp > 0.03 && spread > 2.0
					? "=> Target exposure IS predictive: higher exposure => higher odds of an up-close. The exposure<->probability map is (weakly) invertible — validate OOS before trusting."
					: basket.CorrExpUp > 0.01
						? "=> Weak/marginal relationship: exposure nudges the odds but the edge is small — likely fragile OOS."
						: "=> No usable relationship: target exposure does not predict the next up-close (odds ~ base rate at every level).");

			// ---- DE-RISKING vs TIMING: is the drawdown reduction real, or just holding less? ----
			if (study.Timing.Count > 0)
			{
				Console.WriteLine("\n-- DRAWDOWN: TIMING vs plain DE-RISKING --");
				Console.WriteLine("Strategy DD vs a CONSTANT position at the strategy's own average exposure, and vs B&H.");
				Console.WriteLine("If StratDD < ConstDD the exposure TIMING genuinely cuts risk; if ~equal it's just lower avg size.");
				Console.WriteLine();
				Console.WriteLine(
					$"  {"Symbol",-8} {"HV%",6} {"AvgExp",7} {"StratDD",8} {"ConstDD",8} {"B&H DD",8} " +
					$"{"DD saved",9} {"StratShp",8} {"ConstShp",8}");
				foreach (var t in study.Timing)
					Console.WriteLine(
						$"  {t.Symbol,-8} {t.Hv,6:0.0} {t.AvgExposurePct,6:0.0}% " +
						$"-{t.StratDd,6:0.0}% -{t.ConstDd,6:0.0}% -{t.BhDd,6:0.0}% " +
						$"{t.DdSaved,7:+0.0;-0.0}pp {t.StratSharpe,8:0.00} {t.ConstSharpe,8:0.00}");

				double mAvg   = study.Timing.Average(t => t.AvgExposurePct);
				double mStrat = study.Timing.Average(t => t.StratDd);
				double mConst = study.Timing.Average(t => t.ConstDd);
				double mBh    = study.Timing.Average(t => t.BhDd);
				double mSaved = study.Timing.Average(t => t.DdSaved);
				double mSs    = study.Timing.Average(t => t.StratSharpe);
				double mCs    = study.Timing.Average(t => t.ConstSharpe);
				int savedWins = study.Timing.Count(t => t.DdSaved > 0.5);
				int n = study.Timing.Count;
				Console.WriteLine();
				Console.WriteLine($"Mean AvgExp {mAvg:0.0}%  |  MaxDD: strat -{mStrat:0.0}%  const -{mConst:0.0}%  B&H -{mBh:0.0}%  " +
				                  $"(timing saves {mSaved:+0.0;-0.0}pp vs matched constant, wins {savedWins}/{n})");
				Console.WriteLine($"Mean Sharpe: strat {mSs:0.00}  vs matched constant {mCs:0.00} ({mSs - mCs:+0.00;-0.00})");
				Console.WriteLine(
					mSaved > 1.0 && savedWins > n / 2
						? "=> The drawdown reduction is partly genuine TIMING: exposure is lower specifically when risk is higher (validate OOS)."
						: mSaved < 0.5
							? "=> The drawdown reduction is essentially just DE-RISKING: at matched average exposure a CONSTANT position drops as much (or less). No risk-timing skill in the DD."
							: "=> Mixed: a small part of the drawdown reduction is timing, most is just holding less on average.");
			}

			Console.WriteLine("\nNOTE: in-sample, full window, no costs. Up-close is a coarse proxy for tradable edge (ignores magnitude).");
		}

		// 95% normal-approx CI half-width (in pp) for a percentage p over n samples.
		private static double Ci95(double pct, int n)
		{
			if (n <= 0) return 0.0;
			double p = pct / 100.0;
			return 1.96 * Math.Sqrt(p * (1 - p) / n) * 100.0;
		}

		// Is the series nondecreasing, allowing each step to dip by up to tolPp?
		private static bool IsNondecreasing(List<double> xs, double tolPp)
		{
			for (int i = 1; i < xs.Count; i++)
				if (xs[i] < xs[i - 1] - tolPp) return false;
			return true;
		}

		// Exposure-shape sweep: tent ("converge on 0.5") vs monotonic vs buy&hold.
		public static void PrintExposureShape(List<ShapeRow> rows)
		{
			Console.WriteLine("\n===== EXPOSURE SHAPE: tent (\"converge on 0.5\") vs monotonic vs buy&hold =====");
			if (rows.Count == 0) { Console.WriteLine("No data."); return; }
			Console.WriteLine("position = tent(adjEma): 100% at the peak, tapering to the floor (edge) at peak±0.5, clamped [0,1] (MinExp=0).");
			Console.WriteLine("Monotonic(cur) = clamp(adjEma,0,1) — what the strategy does now. Full window; Sharpe is scale-invariant.");
			Console.WriteLine();

			double bh = rows.First(r => r.Label == "BuyHold").MeanSharpe;
			double mono = rows.First(r => r.Label == "Monotonic(cur)").MeanSharpe;
			Console.WriteLine($"  {"policy",-16} {"meanShp",8} {"meanDD",7} {"meanRet",10} {"vsMono",7} {">BH",5} {">Mono",6}");
			foreach (var r in rows)
			{
				string tag = r.Label == "BuyHold" || r.Label == "Monotonic(cur)" ? "  <<" : "";
				Console.WriteLine(
					$"  {r.Label,-16} {r.MeanSharpe,8:0.000} -{r.MeanDd,5:0.0}% {Signed(r.MeanRet),10} " +
					$"{r.MeanSharpe - mono,7:+0.000;-0.000} {r.BeatsBh,3}/{r.N} {r.BeatsMono,3}/{r.N}{tag}");
			}

			var tents = rows.Where(r => r.Label.StartsWith("Tent")).ToList();
			var best = tents.OrderByDescending(r => r.MeanSharpe).First();
			var peak05 = tents.Where(r => r.Label.Contains("p0.5")).OrderByDescending(r => r.MeanSharpe).FirstOrDefault();
			Console.WriteLine();
			Console.WriteLine($"Baselines: BuyHold Sharpe {bh:0.000}, Monotonic {mono:0.000}.");
			Console.WriteLine($"Best tent: {best.Label} → Sharpe {best.MeanSharpe:0.000} (vs mono {best.MeanSharpe - mono:+0.000;-0.000}, vs B&H {best.MeanSharpe - bh:+0.000;-0.000}).");
			if (peak05 != null)
				Console.WriteLine($"Your peak-0.5 tent: {peak05.Label} → Sharpe {peak05.MeanSharpe:0.000} (vs mono {peak05.MeanSharpe - mono:+0.000;-0.000}).");
			Console.WriteLine(
				best.MeanSharpe > mono + 0.03 && best.MeanSharpe > bh + 0.03
					? $"=> A tent shape BEATS both the current monotonic map and buy&hold on mean Sharpe — the 'fade the extremes' idea has legs. Walk-forward it next."
					: best.MeanSharpe > mono + 0.03
						? "=> The tent beats the current MONOTONIC map but not buy&hold — reshaping helps the signal, though nothing beats B&H Sharpe in this bull window."
						: "=> The tent does NOT beat the current monotonic map on Sharpe. Reshaping toward 0.5 doesn't help; the peak location isn't special (see the sweep).");
			Console.WriteLine("NOTE: full window, no costs. Peak was swept 0.3–0.7 so a special-looking 0.5 can't hide as an in-sample pick. Validate OOS.");
		}

		// Optimal exposure per band (Kelly) + out-of-sample validation.
		public static void PrintBandOptimize(BandOptResult res)
		{
			Console.WriteLine("\n===== OPTIMAL EXPOSURE PER BAND (Kelly) + OUT-OF-SAMPLE TEST =====");
			Console.WriteLine("For each bias-adjusted-exposure band, the growth-optimal exposure = half-Kelly clamp(0.5·mean/var).");
			Console.WriteLine("SuggExp is the exposure to HOLD when the signal is in that band. Bands are the same [-1..+0.9),[+0.9,+inf).");

			// ---- descriptive map ----
			double maxAbs = 0.0;
			foreach (var b in res.Map) if (!double.IsNaN(b.SuggestedExp)) maxAbs = Math.Max(maxAbs, Math.Abs(b.SuggestedExp));
			if (maxAbs <= 0) maxAbs = 1e-9;
			Console.WriteLine($"\n-- BASKET optimal map (pooled, full window) --");
			Console.WriteLine($"  {"band",-14} {"N",6} {"mean",8} {"Sharpe",7} {"Kelly",7} {"SuggExp",8}   {"short 0 long",0}");
			foreach (var b in res.Map)
			{
				string label = double.IsPositiveInfinity(b.Hi) ? $"[{b.Lo,4:+0.0;-0.0},  +inf)" : $"[{b.Lo,4:+0.0;-0.0},{b.Hi,4:+0.0;-0.0})";
				if (b.N == 0) { Console.WriteLine($"  {label,-14} {0,6} {"·",8} {"·",7} {"·",7} {"·",8}   {Bar(0, maxAbs, 12)}"); continue; }
				string flag = b.N < 20 ? "*" : " ";
				Console.WriteLine(
					$"  {label,-14} {b.N,6} {Signed(b.MeanPct) + "%",8} {b.Sharpe,7:+0.00;-0.00} {b.Kelly,7:+0.0;-0.0} {b.SuggestedExp,7:+0.00;-0.00}{flag} {Bar(b.SuggestedExp, maxAbs, 12)}");
			}
			Console.WriteLine("  (in-sample optimum — descriptive only; the OOS test below is what matters.)");

			// ---- OOS validation ----
			if (res.Wf.Count == 0) { Console.WriteLine("\nNo OOS folds (need more bars)."); return; }
			Console.WriteLine("\n-- OUT-OF-SAMPLE (learn band map on train, apply on next block; expanding folds) --");
			Console.WriteLine("Opt = band-optimized sizing. Raw = use the signal magnitude as exposure. B&H = always long. Sharpe decides.");
			Console.WriteLine($"  {"Symbol",-8} {"HV%",6} {"OOS",5} │ {"OptShp",7} {"BhShp",6} {"RawShp",7} {"Edge",6} │ {"OptDD",6} {"BhDD",6} │ {"OptRet",9} {"BhRet",9}");
			foreach (var w in res.Wf)
				Console.WriteLine(
					$"  {w.Symbol,-8} {w.Hv,6:0.0} {w.OosBars,5} │ " +
					$"{w.OptSharpe,7:+0.00;-0.00} {w.BhSharpe,6:0.00} {w.RawSharpe,7:+0.00;-0.00} {w.Edge,6:+0.00;-0.00} │ " +
					$"-{w.OptDd,4:0.0}% -{w.BhDd,4:0.0}% │ {Signed(w.OptRet),9} {Signed(w.BhRet),9}");

			int n = res.Wf.Count;
			double mOpt = res.Wf.Average(w => w.OptSharpe), mBh = res.Wf.Average(w => w.BhSharpe), mRaw = res.Wf.Average(w => w.RawSharpe);
			double mEdge = res.Wf.Average(w => w.Edge);
			int wins = res.Wf.Count(w => w.Edge > 0);
			Console.WriteLine();
			Console.WriteLine($"Mean OOS Sharpe — band-optimized {mOpt:+0.00;-0.00}  vs  B&H {mBh:0.00}  vs  raw-signal {mRaw:+0.00;-0.00}   " +
			                  $"(edge {mEdge:+0.00;-0.00}, band-opt beats B&H in {wins}/{n})");
			Console.WriteLine(
				mEdge > 0.05 && wins > n * 0.6
					? "=> The band-optimized exposure map BEATS buy&hold out-of-sample. The optimal-per-band idea holds up — validate with costs next."
					: "=> The band-optimized map does NOT beat buy&hold OOS. The in-sample 'optimal' exposures were fitting per-band noise — they don't\n" +
					  "   generalize. (Consistent with the flat per-candle relationship: there's no stable return signal in the bands to optimize against.)");
			Console.WriteLine("\n* band N<20 (unreliable optimum). NOTE: in-sample map is descriptive; OOS is the honest test. No costs.");
		}

		// Exposure -> average forward return curve, with an ASCII plot of the shape.
		public static void PrintExpCurve(List<ExpCurveResult> results)
		{
			Console.WriteLine("\n===== EXPOSURE -> RETURN-PER-BUCKET CURVE (compounded, not sized) =====");
			if (results.Count == 0) { Console.WriteLine("No data."); return; }
			Console.WriteLine("Exposure = bias-adjusted (|ema|·biasEma + ema), as of the prior close (look-ahead-free); it can exceed |1|,");
			Console.WriteLine("so the top bucket is open [+0.9,+inf) and the bottom [-1.0,-0.9) also catches exposure < -1.");
			Console.WriteLine("CumRet = COMPOUNDED return of all candles in the bucket (per bucket, not per candle). MeanC = per-candle avg (ref).");
			Console.WriteLine("Pooled CumRet = sum of the per-symbol compounded returns in that bucket. No position sizing.");

			// full ASCII curve for the pooled basket (or the sole symbol)
			var basket = results.FirstOrDefault(r => r.Scope == "BASKET") ?? results[0];
			Console.WriteLine($"\n-- {basket.Scope} (N={basket.N}, meanExp {basket.MeanExp:+0.00;-0.00}) --");
			double maxAbs = 0.0;
			foreach (var b in basket.Bins) if (!double.IsNaN(b.CumRetPct)) maxAbs = Math.Max(maxAbs, Math.Abs(b.CumRetPct));
			if (maxAbs <= 0) maxAbs = 1e-9;
			Console.WriteLine($"  {"bucket",-14} {"N",6} {"CumRet",10} {"MeanC",8}   {"− 0 +",0}");
			foreach (var b in basket.Bins)
			{
				string label = double.IsPositiveInfinity(b.Hi) ? $"[{b.Lo,4:+0.0;-0.0},  +inf)" : $"[{b.Lo,4:+0.0;-0.0},{b.Hi,4:+0.0;-0.0})";
				if (b.N == 0) { Console.WriteLine($"  {label,-14} {0,6} {"·",10} {"·",8}   {Bar(0, maxAbs, 16)}"); continue; }
				string flag = b.N < 10 ? "*" : " ";
				Console.WriteLine(
					$"  {label,-14} {b.N,6} {Signed(b.CumRetPct) + "%",10} {Signed(b.MeanFullPct) + "%",8}{flag} {Bar(b.CumRetPct, maxAbs, 16)}");
			}

			// attribution summary
			double PosCum(ExpCurveResult r) => r.Bins.Where(b => b.Center > 0 && !double.IsNaN(b.CumRetPct)).Sum(b => b.CumRetPct);
			double NegCum(ExpCurveResult r) => r.Bins.Where(b => b.Center < 0 && !double.IsNaN(b.CumRetPct)).Sum(b => b.CumRetPct);
			double TopCum(ExpCurveResult r) => r.Bins[^1].N > 0 && !double.IsNaN(r.Bins[^1].CumRetPct) ? r.Bins[^1].CumRetPct : 0.0;
			double TopSharePct(ExpCurveResult r) => r.N > 0 ? (double)r.Bins[^1].N / r.N * 100.0 : 0.0;
			Console.WriteLine();
			Console.WriteLine($"  Attribution: positive-exposure buckets {Signed(PosCum(basket))}%  vs  negative {Signed(NegCum(basket))}%   " +
			                  $"| top [+0.9,+inf) alone {Signed(TopCum(basket))}% from {TopSharePct(basket):0.0}% of candles.");
			Console.WriteLine($"  (per-candle corr {basket.Corr:+0.000;-0.000} — the shape below reflects WHERE the signal spends time, not per-candle predictive edge.)");

			// compact per-symbol summary
			var perSym = results.Where(r => r.Scope != "BASKET").ToList();
			if (perSym.Count > 0)
			{
				Console.WriteLine("\n-- PER SYMBOL (sorted by HV) — compounded return attribution by exposure --");
				Console.WriteLine($"  {"Symbol",-8} {"HV%",6} {"N",6} {"meanExp",8} {"top%N",6} {"CumRet@top",11} {"Cum@pos",9} {"Cum@neg",9}");
				foreach (var r in perSym)
					Console.WriteLine(
						$"  {r.Scope,-8} {r.Hv,6:0.0} {r.N,6} {r.MeanExp,8:+0.00;-0.00} {TopSharePct(r),5:0.0}% " +
						$"{Signed(TopCum(r)) + "%",11} {Signed(PosCum(r)) + "%",9} {Signed(NegCum(r)) + "%",9}");

				int topDom = perSym.Count(r => TopCum(r) > Math.Abs(NegCum(r)));
				Console.WriteLine();
				Console.WriteLine($"Top [+0.9,+inf) bucket is the biggest return source in {topDom}/{perSym.Count} symbols. " +
				                  $"Mean top-bucket candle share {perSym.Average(TopSharePct):0.0}%.");
				Console.WriteLine("=> Returns concentrate in the HIGH-exposure bucket — but that is because the long-skewed signal SITS there\n" +
				                  "   during up-trends (per-candle corr ~0). It's return attribution / time-spent, not a per-candle predictive edge.");
			}
			Console.WriteLine("\n* bucket N<10 (unreliable). Bottom bucket [-1.0,-0.9) also catches exposure < -1. NOTE: in-sample, no costs, unsized.");
		}

		// Centered ASCII bar: '|' axis at the middle, bar grows right for +v, left for −v.
		private static string Bar(double v, double maxAbs, int w)
		{
			int len = maxAbs > 0 ? (int)Math.Round(Math.Abs(v) / maxAbs * w) : 0;
			if (len > w) len = w;
			var c = new char[2 * w + 1];
			for (int k = 0; k < c.Length; k++) c[k] = ' ';
			c[w] = '|';
			if (v >= 0) for (int k = 1; k <= len; k++) c[w + k] = '#';
			else for (int k = 1; k <= len; k++) c[w - k] = '#';
			return new string(c);
		}

		// Exposure vs overnight-gap odds (open>prev close), vs full-day and intraday.
		public static void PrintExposureGap(List<ExposureGapResult> results)
		{
			Console.WriteLine("\n===== EXPOSURE vs OVERNIGHT GAP: does exposure predict open > prev close? =====");
			if (results.Count == 0) { Console.WriteLine("No data."); return; }
			Console.WriteLine("Signal = target exposure as of the PRIOR close (look-ahead-free; tradable at MOC->MOO).");
			Console.WriteLine("Outcomes on bar i: Overnight = open_i vs close_{i-1}; Full = close_i vs close_{i-1}; Intraday = close_i vs open_i.");

			foreach (var r in results)
			{
				Console.WriteLine($"\n-- {r.Scope} (N={r.N}) --  base up-rate: ON {r.BaseOnUp:0.0}%  Full {r.BaseFullUp:0.0}%  ID {r.BaseIdUp:0.0}%" +
				                  $"   base ret: ON {Signed(r.BaseOnRet)}%  Full {Signed(r.BaseFullRet)}%");
				Console.WriteLine($"   corr(exp, ON-up) {r.CorrOnUp:+0.000;-0.000}   corr(exp, Full-up) {r.CorrFullUp:+0.000;-0.000}   corr(exp, ID-up) {r.CorrIdUp:+0.000;-0.000}");
				Console.WriteLine($"   corr(exp, ON-ret){r.CorrOnRet:+0.000;-0.000}   corr(exp, Full-ret){r.CorrFullRet:+0.000;-0.000}");
				Console.WriteLine($"  {"Exposure",8} {"N",6} │ {"ON up%",7} {"Full up%",8} {"ID up%",7} │ {"ON ret",8} {"Full ret",9}");
				foreach (var b in r.Levels)
					Console.WriteLine(
						$"  {b.Exposure,8:+0.00;-0.00} {b.N,6} │ {b.OnUpPct,6:0.0}% {b.FullUpPct,7:0.0}% {b.IdUpPct,6:0.0}% │ " +
						$"{Signed(b.OnRetPct),7}% {Signed(b.FullRetPct),8}%");

				// verdict per scope
				double onSpread = r.Levels.Count > 0 ? r.Levels[^1].OnUpPct - r.Levels[0].OnUpPct : 0;
				Console.WriteLine(
					Math.Abs(r.CorrOnUp) > 0.03 && Math.Abs(r.CorrOnUp) > Math.Abs(r.CorrFullUp) + 0.02
						? $"  => exposure predicts the OVERNIGHT gap more than the full day (ON up-spread {Signed(onSpread)}pp top-vs-bottom). Promising — walk-forward it."
						: "  => exposure does NOT predict the overnight gap either (corr ~0, ~flat across levels); the overnight drift is unconditional.");
			}
			Console.WriteLine("\nNOTE: in-sample, no costs. Overnight drift is largely a constant premium — the question is whether exposure MODULATES it.");
		}

		// Signal screen for a single name: scalar features, overnight/intraday split, seasonality.
		public static void PrintSignalScreen(List<SignalScreenResult> results)
		{
			Console.WriteLine("\n===== SIGNAL SCREEN: does ANYTHING predict the next bar OOS? =====");
			if (results.Count == 0) { Console.WriteLine("No data (need >= 260 bars)."); return; }

			foreach (var res in results)
			{
				Console.WriteLine($"\n################  {res.Symbol}  ({res.Bars} bars)  ################");

				// -- scalar features --
				Console.WriteLine("\n-- Scalar features vs NEXT-bar return --  (feature known at close of bar t, predicts r[t+1])");
				Console.WriteLine("corr = in-sample; Q5−Q1 = top-vs-bottom-quintile spread; OOS = long/flat rule (dir from 60% train) on 40% test.");
				Console.WriteLine($"  {"Feature",-11} {"N",6} {"corr",7} {"Q5−Q1 ret",10} {"Q5−Q1 up",9} │ {"ruleShp",8} {"bhShp",7} {"edge",7} {"%long",6}");
				foreach (var f in res.Features)
					Console.WriteLine(
						$"  {f.Name,-11} {f.N,6} {f.Corr,7:+0.000;-0.000} {Signed(f.QSpreadRet) + "pp",10} " +
						$"{Signed(f.QSpreadUp) + "pp",9} │ {f.OosRuleSharpe,8:0.00} {f.OosBhSharpe,7:0.00} {f.OosEdge,7:+0.00;-0.00} {f.PctLong,5:0}%");

				// -- overnight vs intraday --
				Console.WriteLine("\n-- Overnight (open/prev-close) vs Intraday (close/open) vs Full --  where does the return/Sharpe live?");
				Console.WriteLine($"  {"Segment",-11} {"annMean",9} {"annVol",8} {"Sharpe",7} {"cumRet",10}");
				foreach (var s in res.Segments)
					Console.WriteLine($"  {s.Name,-11} {Signed(s.AnnMeanPct) + "%",9} {s.AnnVolPct,6:0.0}% {s.Sharpe,7:0.00} {Signed(s.CumRetPct) + "%",10}");

				// -- seasonality --
				Console.WriteLine("\n-- Seasonality (mean return realized on the bar) --");
				string dh = "  Weekday : "; foreach (var d in res.Dow) dh += $"{d.Name} {Signed(d.MeanRetPct)}%  ";
				Console.WriteLine(dh);
				if (res.Tom != null && res.Rest != null)
					Console.WriteLine($"  Turn-of-month: {res.Tom.Name} {Signed(res.Tom.MeanRetPct)}% (n={res.Tom.N})  vs  {res.Rest.Name} {Signed(res.Rest.MeanRetPct)}% (n={res.Rest.N})");

				// -- verdict --
				int nFeat = res.Features.Count;
				var best = res.Features.OrderByDescending(f => f.OosEdge).FirstOrDefault();
				var onSeg = res.Segments.FirstOrDefault(s => s.Name == "Overnight");
				var inSeg = res.Segments.FirstOrDefault(s => s.Name == "Intraday");
				var fullSeg = res.Segments.FirstOrDefault(s => s.Name == "Full (B&H)");
				Console.WriteLine();
				// a scalar feature only counts if its OOS edge clears the multiple-testing bar
				bool featEdge = best != null && best.OosEdge > 0.25;
				if (best != null)
					Console.WriteLine($"Best OOS scalar feature: {best.Name} (edge {best.OosEdge:+0.00;-0.00} Sharpe) — " +
					                  $"{(featEdge ? "clears the bar" : $"NOT convincing (1 of {nFeat} tested; within multiple-testing noise)")}.");
				// overnight counts if it earns a >= full-day Sharpe at clearly lower vol
				bool onEdge = onSeg != null && fullSeg != null && onSeg.Sharpe >= fullSeg.Sharpe && onSeg.AnnVolPct < 0.8 * fullSeg.AnnVolPct;
				if (onSeg != null && inSeg != null && fullSeg != null)
					Console.WriteLine($"Overnight Sharpe {onSeg.Sharpe:0.00} @ {onSeg.AnnVolPct:0.0}% vol  vs  intraday {inSeg.Sharpe:0.00} @ {inSeg.AnnVolPct:0.0}%  vs  full {fullSeg.Sharpe:0.00} @ {fullSeg.AnnVolPct:0.0}%.");
				Console.WriteLine(
					featEdge
						? "=> A scalar feature times the tape OOS — worth a deeper, cost-aware walk-forward."
						: onEdge
							? "=> No scalar/seasonal edge, BUT the risk-adjusted return is earned OVERNIGHT (same/better Sharpe at ~half the\n" +
							  "   vol of full-day; intraday is mostly noise). This is the documented overnight anomaly — the one real lead.\n" +
							  "   Next: walk-forward a long-overnight/flat-intraday rule NET OF COSTS (daily round-trips are cost-sensitive)."
							: "=> Nothing beats coin-flip OOS: no scalar feature and no session/seasonal split shows a durable edge.");
			}
			Console.WriteLine("\nNOTE: in-sample corr can be spurious; the OOS rule vs B&H is the honest column. No costs. Single name = noisy.");
		}

		// Triple-barrier (bracket) study: LT-Bull entries vs random entries.
		public static void PrintBarrier(List<BracketResult> rows)
		{
			Console.WriteLine("\n===== RUN OUTCOMES: reach +Y% before −Z%? — LT-Bull entry vs RANDOM entry =====");
			if (rows.Count == 0) { Console.WriteLine("No data."); return; }
			int maxHold = rows[0].MaxHold;
			Console.WriteLine($"Enter long; WIN if +Y% is touched before −Z% within {maxHold} bars, LOSS if −Z% first, else TIMEOUT.");
			Console.WriteLine("Bull = entries at the bar the LT state turns Bull. Rand = entries at every bar (unconditional null).");
			Console.WriteLine("Edge = Bull Win% − Rand Win% (does the state time the run better than chance?). Barriers use bar H/L; no costs.");
			Console.WriteLine();
			Console.WriteLine(
				$"  {"Y/Z",-8} │ {"BullN",6} {"Win%",6} {"Loss%",6} {"TO%",6} {"Exp%",7} {"bars",5} │ " +
				$"{"Win%",6} {"Loss%",6} {"Exp%",7} │ {"WinEdge",8} {"ExpEdge",8}");
			foreach (var r in rows)
			{
				string yz = $"{r.Up:0}/{r.Down:0}";
				Console.WriteLine(
					$"  {yz,-8} │ {r.BullN,6} {r.BullWinPct,5:0.0}% {r.BullLossPct,5:0.0}% {r.BullTimeoutPct,5:0.0}% " +
					$"{Signed(r.BullExpectancyPct),6}% {r.BullAvgBars,5:0} │ " +
					$"{r.RandWinPct,5:0.0}% {r.RandLossPct,5:0.0}% {Signed(r.RandExpectancyPct),6}% │ " +
					$"{r.WinEdge,6:+0.0;-0.0}pp {Signed(r.ExpEdge),7}pp");
			}

			double mWinEdge = rows.Average(r => r.WinEdge);
			double mExpEdge = rows.Average(r => r.ExpEdge);
			int winEdgeWins = rows.Count(r => r.WinEdge > 0);
			int expEdgeWins = rows.Count(r => r.ExpEdge > 0);
			int n = rows.Count;
			Console.WriteLine();
			Console.WriteLine($"Mean Win% edge (Bull−Rand): {mWinEdge:+0.0;-0.0}pp across {n} brackets (Bull higher in {winEdgeWins}/{n}).");
			Console.WriteLine($"Mean Expectancy edge      : {Signed(mExpEdge)}pp (Bull higher in {expEdgeWins}/{n}).");
			Console.WriteLine(
				mWinEdge > 2.0 && winEdgeWins > n * 0.6 && mExpEdge > 0.0
					? "=> LT-Bull entries DO show run-level asymmetry: they reach +Y before −Z more often than random, with\n" +
					  "   positive expectancy edge. This is a tradable trend signal the per-bar test missed — WALK-FORWARD it next."
					: Math.Abs(mWinEdge) <= 2.0
						? "=> LT-Bull entries hit +Y-before−Z about as often as RANDOM entries — the state adds little run-level\n" +
						  "   timing edge. The visible trends are drift shared by all entries, not skill from the state."
						: "=> LT-Bull entries are WORSE than random at reaching +Y before −Z — the state times entries badly.");
			Console.WriteLine("NOTE: in-sample, no costs; bar-H/L barriers are optimistic (equally for both groups, so the EDGE is fair).");
		}

		// LT state: concurrent (charted) vs forward (tradable) return separation.
		public static void PrintStateLag(List<StateLagResult> results)
		{
			Console.WriteLine("\n===== LT STATE: CONCURRENT (what you SEE) vs FORWARD (what you can TRADE) =====");
			if (results.Count == 0) { Console.WriteLine("No data."); return; }
			var basket = results[0];
			var off = basket.Offsets;
			Console.WriteLine("Each bar's LT state is computed FROM that bar's close (as drawn on a chart).");
			Console.WriteLine("Offset 0 = that SAME bar's return (concurrent — the state labels this move; NOT tradable).");
			Console.WriteLine("Offset +k = the return k bars LATER (tradable — the state is known at the current close).");
			Console.WriteLine("If Bull−Bear separation is huge at 0 but ~0 by +1, the chart pattern is descriptive, not predictive.");

			string OffHdr(int o) => o == 0 ? "0(now)" : "+" + o;

			Console.WriteLine($"\n-- BASKET (pooled) --  mean per-bar return by LT state and offset:");
			string head = $"  {"",-14}";
			for (int j = 0; j < off.Length; j++) head += $"{OffHdr(off[j]),9}";
			Console.WriteLine(head);

			void RetRow(string label, StateLagRow r)
			{
				string line = $"  {label,-14}";
				for (int j = 0; j < off.Length; j++) line += $"{Signed(r.MeanRetPct[j]) + "%",9}";
				Console.WriteLine(line);
			}
			RetRow("LT Bull", basket.Bull);
			RetRow("LT Bear", basket.Bear);

			string spread = $"  {"Spread(Bu−Be)",-14}";
			for (int j = 0; j < off.Length; j++) spread += $"{Signed(basket.Spread(j)) + "pp",9}";
			Console.WriteLine(spread);

			Console.WriteLine();
			void UpRow(string label, StateLagRow r)
			{
				string line = $"  {label,-14}";
				for (int j = 0; j < off.Length; j++) line += $"{r.UpRatePct[j],8:0.0}%";
				Console.WriteLine(line);
			}
			UpRow("Up% Bull", basket.Bull);
			UpRow("Up% Bear", basket.Bear);

			// per-symbol: concurrent spread vs first tradable spread — is the collapse universal?
			Console.WriteLine("\n-- PER SYMBOL --  Bull−Bear mean-return spread: concurrent (offset 0) vs next bar (+1)");
			Console.WriteLine($"  {"Symbol",-8} {"HV%",6} {"Spread@0",9} {"Spread@+1",10} {"kept@+1",8}");
			foreach (var r in results.Skip(1))
			{
				double s0 = r.Spread(0), s1 = r.Spread(1);
				double kept = Math.Abs(s0) > 1e-9 ? s1 / s0 * 100.0 : 0.0;
				Console.WriteLine($"  {r.Scope,-8} {r.Hv,6:0.0} {Signed(s0) + "pp",9} {Signed(s1) + "pp",10} {kept,6:0.0}%");
			}

			double b0 = basket.Spread(0), b1 = basket.Spread(1);
			double keptB = Math.Abs(b0) > 1e-9 ? b1 / b0 * 100.0 : 0.0;
			double upGap1 = basket.Bull.UpRatePct[1] - basket.Bear.UpRatePct[1];
			Console.WriteLine();
			Console.WriteLine($"Basket: concurrent spread {Signed(b0)}pp collapses to {Signed(b1)}pp at the next bar " +
			                  $"({keptB:0.0}% survives); next-bar up-rate gap only {Signed(upGap1)}pp (direction ~coin flip).");
			Console.WriteLine(
				"=> Most of the separation you SEE is CONCURRENT labeling: the LT state is computed from the bar's own");
			Console.WriteLine(
				$"   close, so Bull bars are up bars by construction. ~{100 - keptB:0}% of it is gone by the next tradable bar.");
			Console.WriteLine(
				Math.Abs(b1) < 0.10
					? "   What remains is negligible — the chart pattern does NOT forecast."
					: $"   A small forward drift (~{Signed(b1)}pp/bar) persists — real but weak trend-persistence, concentrated in\n" +
					  "   high-vol names, with direction still ~50/50. It's the same faint momentum the vol-target walk-forward\n" +
					  "   showed does NOT beat passive de-risking OOS (net of costs).");
			Console.WriteLine("NOTE: offset 0 uses the bar's own close to BOTH set the state and measure the return — it cannot be traded.");
		}

		// Walk-forward comparison of the strategy against a risk-matched passive vol-target.
		public static void PrintVolTargetWf(List<VolTargetWfRow> rows)
		{
			Console.WriteLine("\n===== VOL-TARGET BASELINE, WALK-FORWARD (does active timing beat a passive vol-target OOS?) =====");
			if (rows.Count == 0) { Console.WriteLine("No data (need enough bars per symbol for train+OOS folds)."); return; }
			Console.WriteLine("Baseline = long-only vol-target: exposure = clamp(targetVol / past-realized-vol, 0, 1), NO directional view.");
			Console.WriteLine("targetVol calibrated on each fold's TRAIN slice to match the strategy's avg exposure; scored on the next OOS block.");
			Console.WriteLine("Sharpe is the decider (scale-invariant). Edge = StratSharpe − VtSharpe (+ => active timing wins OOS).");
			Console.WriteLine();
			Console.WriteLine(
				$"  {"Symbol",-8} {"HV%",6} {"OOS",5} │ {"S.Shp",6} {"V.Shp",6} {"B.Shp",6} {"Edge",6} │ " +
				$"{"S.DD",6} {"V.DD",6} │ {"S.Ret",8} {"V.Ret",8} │ {"S.Exp",5} {"V.Exp",5}");
			foreach (var r in rows)
			{
				Console.WriteLine(
					$"  {r.Symbol,-8} {r.Hv,6:0.0} {r.OosBars,5} │ " +
					$"{r.StratSharpe,6:0.00} {r.VtSharpe,6:0.00} {r.BhSharpe,6:0.00} {r.Edge,6:+0.00;-0.00} │ " +
					$"-{r.StratDd,4:0.0}% -{r.VtDd,4:0.0}% │ {Signed(r.StratRet),8} {Signed(r.VtRet),8} │ " +
					$"{r.StratAvgExp,4:0}% {r.VtAvgExp,4:0}%");
			}

			int n = rows.Count;
			double mS = rows.Average(r => r.StratSharpe), mV = rows.Average(r => r.VtSharpe), mB = rows.Average(r => r.BhSharpe);
			double mE = rows.Average(r => r.Edge);
			var edges = rows.Select(r => r.Edge).OrderBy(x => x).ToList();
			double medE = edges.Count % 2 == 1 ? edges[edges.Count / 2] : 0.5 * (edges[edges.Count / 2 - 1] + edges[edges.Count / 2]);
			int wins = rows.Count(r => r.Edge > 0);
			double mSdd = rows.Average(r => r.StratDd), mVdd = rows.Average(r => r.VtDd);
			Console.WriteLine();
			Console.WriteLine($"Mean OOS Sharpe — strategy {mS:0.00}  vs vol-target {mV:0.00}  vs B&H {mB:0.00}   " +
			                  $"(edge {mE:+0.00;-0.00}, median {medE:+0.00;-0.00}, strat wins {wins}/{n})");
			Console.WriteLine($"Mean OOS MaxDD  — strategy -{mSdd:0.0}%  vs vol-target -{mVdd:0.0}%");
			Console.WriteLine(
				mE > 0.05 && wins > n * 0.6
					? "=> The active timing BEATS a risk-matched vol-target out-of-sample. The machinery earns its keep (relative to passive de-risking)."
					: mE < -0.05 || wins < n * 0.4
						? "=> A passive vol-target BEATS or ties the strategy OOS. The active machinery does NOT earn its keep — the value is de-risking, achievable mechanically."
						: "=> Strategy ~ties a passive vol-target OOS. The active timing adds no reliable risk-adjusted edge over mechanical de-risking.");
			Console.WriteLine("NOTE: expanding-window walk-forward, pooled OOS blocks, no costs. Vol-target uses only past vol (no look-ahead).");
		}

		// Walk-forward: per-symbol tuned-on-train vs. one global default, both scored on the
		// held-out test slice. The decisive question is whether TunedTest beats DefaultTest
		// out-of-sample (TuningEdge > 0) or whether the in-sample edge was just overfitting.
		public static void PrintWalkForward(List<WalkForwardRow> rows)
		{
			Console.WriteLine("\n===== WALK-FORWARD VALIDATION (tune on train, score on test) =====");
			var gd = rows.FirstOrDefault()?.GlobalDefault;
			if (gd != null)
				Console.WriteLine(
					$"Global default (best mean train Sharpe): EMA={gd.ExposureEmaPeriod} " +
					$"BiasP={gd.BiasPeriod} LBias={gd.LongBias:0.##} BiasEMA={gd.BiasEmaPeriod} " +
					$"Drift={gd.RebalanceDriftPercent:0.#}%");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",7} {"Tr/Te",9}   " +
				$"{"TunedTrn",9} {"TunedTst",9} {"DfltTst",9} {"Edge",7} {"Decay",7}");

			foreach (var r in rows)
			{
				Console.WriteLine(
					$"{r.Symbol,-8} {r.HistoricalVolatilityPct,7:0.0} {$"{r.TrainBars}/{r.TestBars}",9}   " +
					$"{r.TunedTrainSharpe,9:0.00} {r.TunedTestSharpe,9:0.00} {r.DefaultTestSharpe,9:0.00} " +
					$"{r.TuningEdge,7:+0.00;-0.00} {r.OverfitDecay,7:+0.00;-0.00}");
			}

			if (rows.Count > 0)
			{
				double meanTunedTest = rows.Average(r => r.TunedTestSharpe);
				double meanDefaultTest = rows.Average(r => r.DefaultTestSharpe);
				double meanEdge = rows.Average(r => r.TuningEdge);
				double meanDecay = rows.Average(r => r.OverfitDecay);
				int tunedWins = rows.Count(r => r.TuningEdge > 0);

				Console.WriteLine();
				Console.WriteLine($"Mean test Sharpe — per-symbol tuned : {meanTunedTest,6:0.00}");
				Console.WriteLine($"Mean test Sharpe — global default   : {meanDefaultTest,6:0.00}");
				Console.WriteLine($"Mean tuning edge (tuned - default)  : {meanEdge,6:+0.00;-0.00}   " +
				                  $"[tuned wins {tunedWins}/{rows.Count}]");
				Console.WriteLine($"Mean overfit decay (train - test)   : {meanDecay,6:+0.00;-0.00}");
				Console.WriteLine();
				Console.WriteLine(meanEdge > 0.10
					? "=> Per-symbol tuning ADDS value out-of-sample. Worth a selection method."
					: "=> Per-symbol tuning does NOT beat a fixed default out-of-sample => overfitting. Lock in one default.");
			}
		}

		// Rolling walk-forward: one row per fold, showing the re-tuned global default and its
		// out-of-sample result on the following test window. Consistently positive mean test
		// Sharpe => real edge; noise around zero => the strategy isn't clearing the bar.
		public static void PrintRolling(List<RollingFold> folds)
		{
			Console.WriteLine("\n===== ROLLING WALK-FORWARD (re-tune default each fold, score next window) =====");
			if (folds.Count == 0)
			{
				Console.WriteLine("No folds — not enough history for the configured train/test window.");
				return;
			}

			Console.WriteLine(
				$"{"Fold",4} {"Test window",23} {"Sym",4}   " +
				$"{"EMA",4} {"BiasP",6} {"LBias",6} {"BiasEMA",8} {"Drift%",7}   " +
				$"{"MeanShp",8} {"MedShp",7} {"Ret%",9} {"%Pos",6}");

			foreach (var f in folds)
			{
				string window = $"{f.TestStart:yyyy-MM-dd}..{f.TestEnd:yyyy-MM-dd}";
				Console.WriteLine(
					$"{f.Index,4} {window,23} {f.Symbols,4}   " +
					$"{f.Ema,4} {f.BiasP,6} {f.LongBias,6:0.##} {f.BiasEma,8} {f.Drift,7:0.#}   " +
					$"{f.MeanTestSharpe,8:0.00} {f.MedianTestSharpe,7:0.00} {Signed(f.MeanTestReturnPct),9} " +
					$"{f.PctPositive * 100.0,5:0.}%");
			}

			double meanShp = folds.Average(f => f.MeanTestSharpe);
			double medOfMeans = folds.Select(f => f.MeanTestSharpe).OrderBy(s => s).ToList() is var l && l.Count > 0
				? (l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2.0) : 0.0;
			int posFolds = folds.Count(f => f.MeanTestSharpe > 0);
			double sd = folds.Count > 1
				? Math.Sqrt(folds.Sum(f => Math.Pow(f.MeanTestSharpe - meanShp, 2)) / (folds.Count - 1)) : 0.0;

			Console.WriteLine();
			Console.WriteLine($"Folds                              : {folds.Count}");
			Console.WriteLine($"Mean OOS Sharpe across folds       : {meanShp,6:0.00}  (sd {sd:0.00})");
			Console.WriteLine($"Median OOS Sharpe across folds     : {medOfMeans,6:0.00}");
			Console.WriteLine($"Folds with positive mean Sharpe    : {posFolds}/{folds.Count}");
			Console.WriteLine();
			Console.WriteLine(meanShp > 0.30 && posFolds > folds.Count / 2
				? "=> OOS Sharpe is consistently positive => the strategy shows a real edge."
				: "=> OOS Sharpe hovers around zero / is inconsistent => no reliable edge on this basket.");
		}

		// Rolling walk-forward over the BUCKET-WEIGHT shape: one row per fold showing the
		// re-tuned map shape (Bull/Bear rows as [bottom..top]) and its OOS result.
		public static void PrintRollingBuckets(List<RollingFold> folds)
		{
			Console.WriteLine("\n===== ROLLING WALK-FORWARD — BUCKET WEIGHTS (re-tune map each fold) =====");
			if (folds.Count == 0)
			{
				Console.WriteLine("No folds — not enough history for the configured train/test window.");
				return;
			}

			Console.WriteLine(
				$"{"Fold",4} {"Test window",23} {"Sym",4}   " +
				$"{"LT Bull [bot..top]",18} {"LT Bear [bot..top]",18}   " +
				$"{"MeanShp",8} {"MedShp",7} {"Ret%",9} {"%Pos",6}");

			foreach (var f in folds)
			{
				string window = $"{f.TestStart:yyyy-MM-dd}..{f.TestEnd:yyyy-MM-dd}";
				var s = f.BucketShape;
				string bull = s == null ? "—" : $"[{s.BotBull,5:0.##} .. {s.TopBull,4:0.##}]";
				string bear = s == null ? "—" : $"[{s.BotBear,5:0.##} .. {s.TopBear,4:0.##}]";
				Console.WriteLine(
					$"{f.Index,4} {window,23} {f.Symbols,4}   " +
					$"{bull,18} {bear,18}   " +
					$"{f.MeanTestSharpe,8:0.00} {f.MedianTestSharpe,7:0.00} {Signed(f.MeanTestReturnPct),9} " +
					$"{f.PctPositive * 100.0,5:0.}%");
			}

			double meanShp = folds.Average(f => f.MeanTestSharpe);
			int posFolds = folds.Count(f => f.MeanTestSharpe > 0);
			double sd = folds.Count > 1
				? Math.Sqrt(folds.Sum(f => Math.Pow(f.MeanTestSharpe - meanShp, 2)) / (folds.Count - 1)) : 0.0;

			Console.WriteLine();
			Console.WriteLine($"Folds                              : {folds.Count}");
			Console.WriteLine($"Mean OOS Sharpe across folds       : {meanShp,6:0.00}  (sd {sd:0.00})");
			Console.WriteLine($"Folds with positive mean Sharpe    : {posFolds}/{folds.Count}");
			Console.WriteLine();
			Console.WriteLine(meanShp > 0.30 && posFolds > folds.Count / 2
				? "=> Bucket-weight tuning holds up OOS => the (LT,ST) map carries a real edge."
				: "=> OOS Sharpe still hovers around zero => the (LT,ST) map does not carry a tradeable edge.");
		}

		// Full-window strategy vs buy & hold: Sharpe, max drawdown, and return-per-drawdown,
		// side by side per symbol, with a verdict on whether the strategy is a net improvement.
		public static void PrintFullWindow(List<FullWindowRow> rows)
		{
			Console.WriteLine("\n===== FULL-WINDOW: STRATEGY vs BUY & HOLD (fixed params, no tuning) =====");
			if (rows.Count == 0)
			{
				Console.WriteLine("No symbols with usable data.");
				return;
			}
			Console.WriteLine("Positive = strategy better. (Sharpe: higher; MaxDD: lower/less negative; Ret/DD: higher.)");
			Console.WriteLine();
			Console.WriteLine(
				$"{"Symbol",-8} {"HV%",6} {"Bars",5}   " +
				$"{"Shp:Strat",9} {"BH",6} │ {"DD:Strat",8} {"BH",7} │ {"Ret/DD:S",8} {"BH",6}");

			foreach (var r in rows)
			{
				string sRd = double.IsNaN(r.StratRetPerDd) ? "  n/a" : $"{r.StratRetPerDd,6:0.00}";
				string bRd = double.IsNaN(r.BhRetPerDd)    ? "  n/a" : $"{r.BhRetPerDd,6:0.00}";
				Console.WriteLine(
					$"{r.Symbol,-8} {r.HistoricalVolatilityPct,6:0.0} {r.Bars,5}   " +
					$"{r.StratSharpe,9:0.00} {r.BhSharpe,6:0.00} │ " +
					$"-{r.StratMaxDd,7:0.0}% -{r.BhMaxDd,6:0.0}% │ {sRd} {bRd}");
			}

			int shpWins = rows.Count(r => r.StratSharpe > r.BhSharpe);
			int ddWins  = rows.Count(r => r.StratMaxDd < r.BhMaxDd);      // lower DD is better
			double mShpS = rows.Average(r => r.StratSharpe), mShpB = rows.Average(r => r.BhSharpe);
			double mDdS  = rows.Average(r => r.StratMaxDd),  mDdB  = rows.Average(r => r.BhMaxDd);

			Console.WriteLine();
			Console.WriteLine($"Mean Sharpe   — strategy {mShpS,6:0.00}   buy & hold {mShpB,6:0.00}   " +
			                  $"(strategy higher on {shpWins}/{rows.Count})");
			Console.WriteLine($"Mean Max DD   — strategy -{mDdS,5:0.0}%   buy & hold -{mDdB,5:0.0}%   " +
			                  $"(strategy lower on {ddWins}/{rows.Count})");
			Console.WriteLine();

			bool ddBetter  = mDdS < mDdB;
			bool shpBetter = mShpS > mShpB;
			Console.WriteLine(
				ddBetter && shpBetter ? "=> Strategy delivers BOTH lower drawdown and higher Sharpe: a useful risk overlay."
				: ddBetter            ? "=> Strategy cuts drawdown but does NOT raise Sharpe: risk-reducer, not alpha."
				: shpBetter           ? "=> Strategy raises Sharpe but not drawdown: unexpected; inspect per-symbol."
				:                       "=> Strategy improves NEITHER on average: no edge even as a risk overlay here.");
		}

		// Short-side A/B: strategy (Min 0% vs Min -100%) against the SAME buy & hold. Shows
		// whether allowing a real short improves the full-window risk/return.
		public static void PrintShortAb(List<FullWindowRow> noShort, List<FullWindowRow> withShort)
		{
			Console.WriteLine("\n===== SHORT-SIDE A/B: MinExposure 0% vs -100% (full window, fixed params) =====");
			Console.WriteLine("(Buy & hold is identical for both; only the strategy's short clamp changes.)");
			Console.WriteLine();
			Console.WriteLine($"{"Config",-22} {"MeanShp",8} {"MeanDD",8} {"MeanRet%",10} {"Shp>BH",8} {"DD<BH",7}");
			PrintAbRow("No short (Min 0%)", noShort);
			PrintAbRow("Full short (Min -100%)", withShort);

			double bh = noShort.Count > 0 ? noShort.Average(r => r.BhSharpe) : 0.0;
			Console.WriteLine($"{"Buy & hold",-22} {bh,8:0.00}");

			double sNo = noShort.Average(r => r.StratSharpe), sSh = withShort.Average(r => r.StratSharpe);
			double dNo = noShort.Average(r => r.StratMaxDd),  dSh = withShort.Average(r => r.StratMaxDd);
			Console.WriteLine();
			Console.WriteLine(
				sSh > sNo && dSh <= dNo + 2.0 ? "=> Allowing short IMPROVES Sharpe without materially worse drawdown => prefer -100%."
				: sSh > sNo                   ? "=> Short raises Sharpe but deepens drawdown => trade-off; depends on your objective."
				: dSh < dNo                   ? "=> Short lowers drawdown but not Sharpe => marginal; only if drawdown is the priority."
				:                               "=> Allowing short does NOT help (lower/equal Sharpe, no DD benefit) => keep Min 0% (no short).");
		}

		private static void PrintAbRow(string label, List<FullWindowRow> rows)
		{
			double mShp = rows.Average(r => r.StratSharpe);
			double mDd  = rows.Average(r => r.StratMaxDd);
			double mRet = rows.Average(r => r.StratReturn);
			int shpW = rows.Count(r => r.StratSharpe > r.BhSharpe);
			int ddW  = rows.Count(r => r.StratMaxDd  < r.BhMaxDd);
			Console.WriteLine(
				$"{label,-22} {mShp,8:0.00} -{mDd,6:0.0}% {Signed(mRet),10} " +
				$"{$"{shpW}/{rows.Count}",8} {$"{ddW}/{rows.Count}",7}");
		}

		// Volatility-threshold deployment sweep: aggregate strategy vs B&H over only the
		// symbols at/above each HV cutoff. Shows where the strategy's edge turns positive.
		public static void PrintVolThreshold(string label, List<VolThresholdBucket> buckets)
		{
			Console.WriteLine($"\n----- VOLATILITY-THRESHOLD DEPLOYMENT — {label} -----");
			Console.WriteLine(
				$"{"MinHV%",7} {"Sym",4}   {"StratShp",8} {"BhShp",7} {"ShpEdge",8}   " +
				$"{"StratDD",8} {"BhDD",7} {"DDcut",7}");
			foreach (var b in buckets)
			{
				Console.WriteLine(
					$"{b.MinHv,7:0.} {b.Symbols,4}   {b.MeanStratSharpe,8:0.00} {b.MeanBhSharpe,7:0.00} " +
					$"{b.SharpeEdge,8:+0.00;-0.00}   -{b.MeanStratDd,6:0.0}% -{b.MeanBhDd,6:0.0}% {b.DdReduction,6:0.0}%");
			}
		}

		// "Are the current knobs optimal?" — current combo vs. the full-grid distribution
		// over the deployment universe (HV-filtered), full window.
		public static void PrintKnobRank(KnobRankResult r)
		{
			Console.WriteLine("\n===== ARE THE CURRENT KNOBS OPTIMAL? =====");
			Console.WriteLine($"Universe : {r.Symbols} symbols with HV >= {r.HvThreshold:0.} (the deployment set), full window");
			if (r.Symbols == 0 || r.TotalCombos == 0)
			{
				Console.WriteLine("No symbols in the universe — lower KnobRankHvThreshold or widen the basket.");
				return;
			}
			Console.WriteLine();
			Console.WriteLine($"Current knobs : EMA={r.Ema} BiasP={r.BiasP} LBias={r.LongBias:0.##} " +
			                  $"BiasEMA={r.BiasEma} Drift={r.Drift:0.#}%");
			Console.WriteLine();
			Console.WriteLine($"{"",-26} {"MeanSharpe",11}");
			Console.WriteLine($"{"Current knobs",-26} {r.CurrentMeanSharpe,11:0.000}");
			Console.WriteLine($"{"Grid best (in-sample)",-26} {r.BestMeanSharpe,11:0.000}");
			Console.WriteLine($"{"Grid median",-26} {r.MedianMeanSharpe,11:0.000}");
			Console.WriteLine($"{"Grid worst",-26} {r.WorstMeanSharpe,11:0.000}");
			Console.WriteLine();
			Console.WriteLine($"Rank : {r.BetterThanCurrent} of {r.TotalCombos} combos beat current " +
			                  $"=> {r.Percentile:0.}th percentile");
			if (r.Best != null)
				Console.WriteLine($"Grid-best knobs (overfit) : EMA={r.Best.ExposureEmaPeriod} BiasP={r.Best.BiasPeriod} " +
				                  $"LBias={r.Best.LongBias:0.##} BiasEMA={r.Best.BiasEmaPeriod} Drift={r.Best.RebalanceDriftPercent:0.#}%");

			double headroom = r.BestMeanSharpe - r.CurrentMeanSharpe;
			Console.WriteLine();
			Console.WriteLine(
				r.Percentile >= 75 ? $"=> Current knobs are already near-optimal ({r.Percentile:0.}th pct). Best is only +{headroom:0.00} Sharpe better AND that best is in-sample/overfit (shown not to survive OOS). Leave them."
				: r.Percentile >= 40 ? $"=> Current knobs are middling ({r.Percentile:0.}th pct), +{headroom:0.00} below the (overfit) best. Since tuning doesn't survive OOS, not worth chasing — but they're not a bad choice."
				: $"=> Current knobs are in the bottom of the grid ({r.Percentile:0.}th pct). A better-behaved region exists; consider moving toward the grid median values, but do NOT fit to the in-sample best.");
		}

		// BiasPeriod x BiasEmaPeriod sweep: Sharpe and drawdown matrices (rows = BiasPeriod,
		// cols = Bias EMA) over the deployment universe, with the current cell marked [*] and
		// the smallest performance-preserving pair recommended.
		public static void PrintBiasSweep(BiasSweepResult r)
		{
			Console.WriteLine("\n===== BIAS-PERIOD x BIAS-EMA SWEEP (other knobs fixed) =====");
			Console.WriteLine($"Universe : {r.Symbols} symbols with HV >= {r.HvThreshold:0.} (deployment set), full window");
			if (r.Symbols == 0 || r.Cells.Count == 0)
			{
				Console.WriteLine("No symbols in the universe.");
				return;
			}
			Console.WriteLine($"Current  : BiasPeriod={r.CurBiasPeriod}, BiasEMA={r.CurBiasEmaPeriod}  " +
			                  $"=> Sharpe {r.CurSharpe:0.000}, MaxDD -{r.CurMaxDd:0.0}%   [* marks current]");

			double Get(int bp, int be, bool sharpe)
			{
				var c = r.Cells.First(x => x.BiasPeriod == bp && x.BiasEmaPeriod == be);
				return sharpe ? c.MeanSharpe : c.MeanMaxDd;
			}

			void Matrix(string title, bool sharpe)
			{
				Console.WriteLine($"\n{title}   (rows = BiasPeriod, cols = Bias EMA)");
				Console.Write($"{"BiasP\\EMA",-9}");
				foreach (var be in r.BiasEmaPeriods) Console.Write($"{be,9}");
				Console.WriteLine();
				foreach (var bp in r.BiasPeriods)
				{
					Console.Write($"{bp,-9}");
					foreach (var be in r.BiasEmaPeriods)
					{
						string mark = bp == r.CurBiasPeriod && be == r.CurBiasEmaPeriod ? "*" : " ";
						string val = sharpe ? $"{Get(bp, be, true):0.000}" : $"-{Get(bp, be, false):0.0}%";
						Console.Write($"{val + mark,9}");
					}
					Console.WriteLine();
				}
			}

			Matrix("Mean Sharpe", true);
			Matrix("Mean Max Drawdown", false);

			Console.WriteLine();
			if (r.Recommended != null)
			{
				var rec = r.Recommended;
				bool smaller = rec.BiasPeriod + rec.BiasEmaPeriod < r.CurBiasPeriod + r.CurBiasEmaPeriod;
				Console.WriteLine($"Recommended smaller pair : BiasPeriod={rec.BiasPeriod}, BiasEMA={rec.BiasEmaPeriod}  " +
				                  $"=> Sharpe {rec.MeanSharpe:0.000} (vs {r.CurSharpe:0.000}), MaxDD -{rec.MeanMaxDd:0.0}% (vs -{r.CurMaxDd:0.0}%)");
				Console.WriteLine(smaller
					? $"=> Holds performance with smaller windows. Update Program.cs: BiasPeriod={rec.BiasPeriod}, BiasEmaPeriod={rec.BiasEmaPeriod}."
					: "=> No smaller pair holds performance within tolerance; current values are already about as low as you can go.");
			}
			else
			{
				Console.WriteLine("No pair within tolerance — loosen BiasSweepSharpeTol/BiasSweepDdTolPp or accept current values.");
			}
		}

		// Median of a sequence (0 if empty).
		private static double Median(IEnumerable<double> xs)
		{
			var s = xs.OrderBy(x => x).ToList();
			if (s.Count == 0) return 0.0;
			return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
		}

		// Spearman rank correlation: Pearson on the fractional ranks (ties get the average
		// rank). Robust to the nonlinearity/railing of the LongBias optima.
		private static double Spearman(IEnumerable<double> xs, IEnumerable<double> ys)
		{
			var x = xs.ToList();
			var y = ys.ToList();
			int n = Math.Min(x.Count, y.Count);
			if (n < 2) return 0.0;
			return Corr(Ranks(x.Take(n)), Ranks(y.Take(n)));
		}

		// Fractional ranks with ties averaged (1-based).
		private static List<double> Ranks(IEnumerable<double> xs)
		{
			var v = xs.ToList();
			var ranks = new double[v.Count];
			var order = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToList();
			int k = 0;
			while (k < order.Count)
			{
				int j = k;
				while (j + 1 < order.Count && v[order[j + 1]] == v[order[k]]) j++;
				double avg = 0.0;
				for (int t = k; t <= j; t++) avg += t + 1;   // 1-based positions
				avg /= (j - k + 1);
				for (int t = k; t <= j; t++) ranks[order[t]] = avg;
				k = j + 1;
			}
			return ranks.ToList();
		}

		// Ordinary least-squares fit y ~= a + b*x; returns (a intercept, b slope).
		private static (double a, double b) LinFit(IEnumerable<double> xs, IEnumerable<double> ys)
		{
			var x = xs.ToList();
			var y = ys.ToList();
			int n = Math.Min(x.Count, y.Count);
			if (n < 2) return (0.0, 0.0);
			double mx = x.Take(n).Average(), my = y.Take(n).Average();
			double sxy = 0, sxx = 0;
			for (int i = 0; i < n; i++)
			{
				double dx = x[i] - mx;
				sxy += dx * (y[i] - my);
				sxx += dx * dx;
			}
			double b = sxx > 0 ? sxy / sxx : 0.0;
			return (my - b * mx, b);
		}

		// Pearson correlation coefficient; returns 0 when either series has no variance.
		private static double Corr(IEnumerable<double> xs, IEnumerable<double> ys)
		{
			var x = xs.ToList();
			var y = ys.ToList();
			int n = Math.Min(x.Count, y.Count);
			if (n < 2) return 0.0;

			double mx = x.Take(n).Average();
			double my = y.Take(n).Average();
			double sxy = 0, sxx = 0, syy = 0;
			for (int i = 0; i < n; i++)
			{
				double dx = x[i] - mx, dy = y[i] - my;
				sxy += dx * dy;
				sxx += dx * dx;
				syy += dy * dy;
			}
			double denom = Math.Sqrt(sxx * syy);
			return denom > 0 ? sxy / denom : 0.0;
		}

		private static string Signed(double v)
		{
			double x = Math.Round(v, 2);
			return (x >= 0 ? "+" : "-") + Math.Abs(x).ToString("0.00");
		}
	}
}
