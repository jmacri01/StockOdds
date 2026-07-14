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
