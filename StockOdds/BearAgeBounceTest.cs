using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Direct test of the one intraday cell that survived every filter: (LT Bear, ST Bear) with the LT run aged
	// 6-8 bars showed a POSITIVE 5-bar forward return at both 1h (+0.165%, t 2.00, 62.8% up) and 5m (+0.057%,
	// t 1.71, 62.1% up) -- a bounce after a persistent bear run, so the trade is LONG.
	//
	// Rule: when LT == Bear, ST == Bear and the LT run age is in [MinAge, MaxAge], buy at that bar's close and
	// hold exactly HoldBars. Non-overlapping (no new entry until the current one closes), inside the session
	// only, and inside the same first-3.5h sampling window the measurement used.
	//
	// THE CONTROL THAT MATTERS: the unconditional mean 5-bar forward return over the same sampled population.
	// The 1h window is a rising market, so any 5-bar hold has a positive expectation; the signal only means
	// something if it beats that baseline. Split-half is reported for the same reason -- one number over three
	// years can hide a effect that lived in one stretch.
	public static class BearAgeBounceTest
	{
		public static int MinAge = 6, MaxAge = 8, HoldBars = 5;
		public static double EntryHours = 3.5;
		public static double[] CostsBps = { 0.0, 0.5, 1.0, 2.0 };

		private static TimeSpan BarLen(string interval) => interval switch
		{
			"5m" => TimeSpan.FromMinutes(5), "15m" => TimeSpan.FromMinutes(15),
			"30m" => TimeSpan.FromMinutes(30), "1h" => TimeSpan.FromHours(1), _ => TimeSpan.FromMinutes(5)
		};

		// CROSS-INSTRUMENT test: the same rule on genuinely different underlyings. VOO is the discovery sample and
		// is shown for reference only -- it cannot vote. SPY is deliberately excluded (~99% correlated with VOO,
		// it would re-measure the same series rather than test the effect).
		public static async Task Cross(string interval, string range, params string[] symbols)
		{
			Console.WriteLine($"\n===== CROSS-INSTRUMENT: LT-BEAR AGE {MinAge}-{MaxAge}, LONG {HoldBars} BARS, {interval} =====");
			Console.WriteLine($"{"symbol",8} {"sess",6} {"n",5} {"mean%",9} {"SE%",8} {"t",7} {"up%",7} {"ctrl%",8} {"excess%",9} {"h1",8} {"h2",8}");

			var pooled = new List<double>(); int posCount = 0, votable = 0;
			foreach (var sym in symbols)
			{
				List<OhlcBar> bars;
				try { bars = await IntradayClient.GetAsync(sym, interval, range); }
				catch (Exception ex) { Console.WriteLine($"{sym,8}  fetch failed: {ex.Message}"); continue; }
				if (bars.Count < 200) { Console.WriteLine($"{sym,8}  only {bars.Count} bars"); continue; }

				var r = Evaluate(bars, interval);
				if (r.Trades.Count < 5) { Console.WriteLine($"{sym,8} {r.Sessions,6} {r.Trades.Count,5}  (too few trades)"); continue; }

				var t = r.Trades.Select(x => x.Ret).ToList();
				double mean = t.Average();
				double sd = Math.Sqrt(t.Sum(x => (x - mean) * (x - mean)) / (t.Count - 1));
				double se = sd / Math.Sqrt(t.Count);
				var ordered = r.Trades.OrderBy(x => x.Day).ToList();
				double h1 = ordered.Take(ordered.Count / 2).Average(x => x.Ret);
                double h2 = ordered.Skip(ordered.Count / 2).Average(x => x.Ret);

				Console.WriteLine($"{sym,8} {r.Sessions,6} {t.Count,5} {mean * 100,9:+0.000;-0.000} {se * 100,8:0.000} " +
					$"{mean / se,7:+0.00;-0.00} {100.0 * t.Count(x => x > 0) / t.Count,7:0.0} {r.Control * 100,8:+0.000;-0.000} " +
					$"{(mean - r.Control) * 100,9:+0.000;-0.000} {h1 * 100,8:+0.00;-0.00} {h2 * 100,8:+0.00;-0.00}");

				if (!sym.Equals("VOO", StringComparison.OrdinalIgnoreCase))
				{
					pooled.AddRange(t.Select(x => x - r.Control));
					votable++; if (mean > r.Control) posCount++;
				}
			}

			if (pooled.Count > 2)
			{
				double pm = pooled.Average();
				double psd = Math.Sqrt(pooled.Sum(x => (x - pm) * (x - pm)) / (pooled.Count - 1));
				double pse = psd / Math.Sqrt(pooled.Count);
				Console.WriteLine($"\nPOOLED excess over control, EXCLUDING the VOO discovery sample: " +
					$"{pm * 100:+0.000;-0.000}% (SE {pse * 100:0.000}, t {pm / pse:+0.00}), n {pooled.Count} trades");
				Console.WriteLine($"instruments beating their own control: {posCount}/{votable}");
			}
		}

		private sealed record Eval(List<(DateTime Day, double Ret)> Trades, double Control, int Sessions);

		private static Eval Evaluate(List<OhlcBar> bars, string interval)
		{
			var stEng = new CandleStateEngine(); var ltEng = new LongTermStateEngine();
			var st = new ShortTermState?[bars.Count]; var lt = new LongTermState?[bars.Count];
			var run = new int[bars.Count];
			for (int i = 1; i < bars.Count; i++)
			{
				st[i] = stEng.Update(bars[i - 1], bars[i]); lt[i] = ltEng.Update(bars[i - 1], bars[i]);
				run[i] = (i > 1 && lt[i - 1] != null && lt[i - 1]!.Value == lt[i]!.Value) ? run[i - 1] + 1 : 1;
			}
			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= HoldBars + 2).ToList();

			var trades = new List<(DateTime, double)>(); var baseline = new List<double>();
			foreach (var s in sessions)
			{
				var entryCutoff = bars[s[0]].Date.TimeOfDay + TimeSpan.FromHours(EntryHours);
				int lastInSession = s[^1]; int busyUntil = -1; bool primed = false;
				foreach (int i in s)
				{
					if (st[i] == null || lt[i] == null) continue;
					if (st[i] == ShortTermState.Bull || st[i] == ShortTermState.Bear) primed = true;
					if (!primed) continue;
					if (bars[i].Date.TimeOfDay >= entryCutoff) break;
					int j = i + HoldBars;
					if (j > lastInSession || bars[i].Close <= 0) continue;
					double fwd = (bars[j].Close - bars[i].Close) / bars[i].Close;
					baseline.Add(fwd);
					if (i <= busyUntil) continue;
					if (lt[i] != LongTermState.Bear || st[i] != ShortTermState.Bear) continue;
					if (run[i] < MinAge || run[i] > MaxAge) continue;
					trades.Add((bars[i].Date.Date, fwd)); busyUntil = j;
				}
			}
			return new Eval(trades, baseline.Count > 0 ? baseline.Average() : 0, sessions.Count);
		}

		public static async Task Run(string symbol, string interval, string range)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 200) { Console.WriteLine($"{symbol} {interval}: only {bars.Count} bars"); return; }

			var stEng = new CandleStateEngine(); var ltEng = new LongTermStateEngine();
			var st = new ShortTermState?[bars.Count]; var lt = new LongTermState?[bars.Count];
			var run = new int[bars.Count];
			for (int i = 1; i < bars.Count; i++)
			{
				st[i] = stEng.Update(bars[i - 1], bars[i]); lt[i] = ltEng.Update(bars[i - 1], bars[i]);
				run[i] = (i > 1 && lt[i - 1] != null && lt[i - 1]!.Value == lt[i]!.Value) ? run[i - 1] + 1 : 1;
			}

			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= HoldBars + 2).ToList();

			var trades = new List<(DateTime Day, double Ret)>();
			var baseline = new List<double>();     // unconditional 5-bar forward over the same sampled bars

			foreach (var s in sessions)
			{
				var start = bars[s[0]].Date.TimeOfDay;
				var entryCutoff = start + TimeSpan.FromHours(EntryHours);
				int lastInSession = s[^1];
				int busyUntil = -1;
				bool primed = false;

				foreach (int i in s)
				{
					if (st[i] == null || lt[i] == null) continue;
					if (st[i] == ShortTermState.Bull || st[i] == ShortTermState.Bear) primed = true;
					if (!primed) continue;
					if (bars[i].Date.TimeOfDay >= entryCutoff) break;
					int j = i + HoldBars;
					if (j > lastInSession || bars[i].Close <= 0) continue;

					double fwd = (bars[j].Close - bars[i].Close) / bars[i].Close;
					baseline.Add(fwd);

					if (i <= busyUntil) continue;                        // non-overlapping
					if (lt[i] != LongTermState.Bear || st[i] != ShortTermState.Bear) continue;
					if (run[i] < MinAge || run[i] > MaxAge) continue;

					trades.Add((bars[i].Date.Date, fwd));
					busyUntil = j;
				}
			}

			Console.WriteLine($"\n===== {symbol} {interval}: LT-BEAR AGE {MinAge}-{MaxAge}, LONG {HoldBars} BARS =====");
			Console.WriteLine($"{sessions.Count} sessions | {bars[0].Date:yyyy-MM-dd} -> {bars[^1].Date:yyyy-MM-dd} | non-overlapping, in-session only");
			if (trades.Count == 0) { Console.WriteLine("no trades"); return; }

			double bMean = baseline.Average();
			double bUp = 100.0 * baseline.Count(x => x > 0) / baseline.Count;
			Console.WriteLine($"CONTROL — unconditional {HoldBars}-bar forward over the same sampled bars: " +
				$"mean {bMean * 100:+0.000;-0.000}%, up {bUp:0.0}%, n {baseline.Count}");

			void Stats(string label, List<(DateTime Day, double Ret)> t)
			{
				if (t.Count < 5) { Console.WriteLine($"{label,14} {t.Count,6}  (too few)"); return; }
				var r = t.Select(x => x.Ret).ToList();
				double mean = r.Average();
				double sd = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / (r.Count - 1));
				double se = sd / Math.Sqrt(r.Count);
				Console.WriteLine($"{label,14} {t.Count,6} {mean * 100,9:+0.000;-0.000} {se * 100,8:0.000} " +
					$"{mean / se,7:+0.00;-0.00} {(mean - bMean) / se,9:+0.00;-0.00} {Median(r) * 100,9:+0.000;-0.000} " +
					$"{100.0 * r.Count(x => x > 0) / r.Count,7:0.0} {r.Sum() * 100,9:+0.0;-0.0}");
			}

			Console.WriteLine($"\n{"split",14} {"n",6} {"mean%",9} {"SE%",8} {"t vs 0",7} {"t vs ctrl",9} {"median%",9} {"up%",7} {"total%",9}");
			Stats("ALL", trades);
			var mid = trades.OrderBy(t => t.Day).Skip(trades.Count / 2).First().Day;
			Stats("first half", trades.Where(t => t.Day < mid).ToList());
			Stats("second half", trades.Where(t => t.Day >= mid).ToList());

			Console.WriteLine($"\n{"cost",6} {"net mean%",10} {"total%",9} {"vs control",11}");
			foreach (double bps in CostsBps)
			{
				double net = trades.Average(t => t.Ret) - bps / 10000.0;
				Console.WriteLine($"{bps,6:0.0} {net * 100,10:+0.000;-0.000} {net * trades.Count * 100,9:+0.0;-0.0} " +
					$"{(net - bMean) * 100,11:+0.000;-0.000}");
			}
		}

		private static double Median(List<double> xs)
		{ var s = xs.OrderBy(x => x).ToList(); int m = s.Count / 2; return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0; }
	}
}
