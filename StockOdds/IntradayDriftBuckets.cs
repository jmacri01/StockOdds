using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Does ANY intraday bucket predict a DOWN move to the session close? On daily bars the answer was no across
	// nine condition-combinations -- every cell's mean forward move was positive and down-day frequency never
	// meaningfully cleared 50%. That is what has killed every bearish structure tried. This asks the same question
	// at intraday resolution, where the state machine sees far more transitions.
	//
	// THE STATISTICS ARE THE HARD PART, not the measurement. Every bar within a session shares ONE closing price,
	// so their forward returns are almost perfectly correlated and the effective sample size is the number of
	// SESSIONS, not bars. Treating 3,000 bars as 3,000 observations would shrink the standard error by ~9x and
	// manufacture significance out of nothing. So each bucket's mean is formed per session first, and the standard
	// error is taken ACROSS session means.
	//
	// 5m is capped at 60 days (~40 sessions), which cannot resolve the effects that have mattered in this line.
	// 1h reaches 730 days and is run alongside purely for power. Where they disagree, believe 1h.
	public static class IntradayDriftBuckets
	{
		public static double TailBarsExcluded = 2;   // no forward window from the last bars of a session

		private sealed record Obs(DateTime Sess, double Fwd, ShortTermState St, int Candle, double Exp, double BarFrac);

		public static async Task Run(string symbol = "SPY")
		{
			foreach (var (interval, range) in new[] { ("1h", "730d"), ("5m", "60d") })
				await One(symbol, interval, range);
		}

		private static async Task One(string symbol, string interval, string range)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 200) { Console.WriteLine($"{interval}: not enough data"); return; }
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			var pos = new Dictionary<DateTime, double>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++) pos[eng.ReturnDates[k]] = eng.Positions[k];
			var stm = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < eng.StState.Count && k < eng.ReturnDates.Count; k++) stm[eng.ReturnDates[k]] = eng.StState[k];
			var cdm = new Dictionary<DateTime, int>();
			for (int k = 0; k < eng.CandleType.Count && k < eng.ReturnDates.Count; k++) cdm[eng.ReturnDates[k]] = eng.CandleType[k];

			var obs = new List<Obs>();
			foreach (var g in bars.GroupBy(b => b.Date.Date).OrderBy(g => g.Key))
			{
				var sb = g.OrderBy(b => b.Date).ToList();
				int n = sb.Count;
				if (n < 5) continue;
				double close = sb[n - 1].Close;
				for (int i = 0; i < n - TailBarsExcluded; i++)
				{
					if (!pos.TryGetValue(sb[i].Date, out double e)) continue;
					stm.TryGetValue(sb[i].Date, out var st);
					cdm.TryGetValue(sb[i].Date, out int cd);
					if (sb[i].Close <= 0) continue;
					obs.Add(new Obs(g.Key, (close - sb[i].Close) / sb[i].Close, st, cd, e, (double)i / n));
				}
			}
			int sessions = obs.Select(o => o.Sess).Distinct().Count();
			Console.WriteLine($"\n===== {symbol} {interval}: FORWARD MOVE TO THE SESSION CLOSE, BY BUCKET =====");
			Console.WriteLine($"{obs.Count} bars across {sessions} sessions | SE is clustered BY SESSION " +
				$"(bars inside a session share one close, so they are not independent)");
			Console.WriteLine($"{"bucket",30} {"bars",7} {"sess",6} {"meanFwd%",10} {"down%",7} {"SE%",8} {"t",7}");

			void Row(string lbl, Func<Obs, bool> f)
			{
				var t = obs.Where(f).ToList();
				if (t.Count < 30) { Console.WriteLine($"{lbl,30} {t.Count,7}  (too few)"); return; }
				// cluster: one mean per session, then dispersion ACROSS sessions
				var per = t.GroupBy(x => x.Sess).Select(gg => gg.Average(x => x.Fwd)).ToList();
				double m = per.Average();
				double sd = per.Count > 1 ? Math.Sqrt(per.Sum(z => (z - m) * (z - m)) / (per.Count - 1)) : 0;
				double se = per.Count > 1 ? sd / Math.Sqrt(per.Count) : 0;
				Console.WriteLine($"{lbl,30} {t.Count,7} {per.Count,6} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * t.Count(x => x.Fwd < 0) / t.Count,7:0.0} {100 * se,8:0.0000} {(se > 0 ? m / se : 0),7:+0.00;-0.00}");
			}

			Row("ALL BARS", _ => true);
			Console.WriteLine("  by ST state:");
			foreach (var st in new[] { ShortTermState.Bull, ShortTermState.BullNeutral, ShortTermState.BearNeutral, ShortTermState.Bear })
				Row($"  ST {st}", o => o.St == st);
			Console.WriteLine("  by candle type:");
			foreach (var (l, c) in new[] { ("Bull", 1), ("Neutral", 0), ("Bear", -1) })
				Row($"  candle {l}", o => o.Candle == c);
			Console.WriteLine("  by engine exposure:");
			Row("  exposure < 0.10", o => o.Exp < 0.10);
			Row("  exposure 0.10 - 0.50", o => o.Exp >= 0.10 && o.Exp < 0.50);
			Row("  exposure >= 0.50", o => o.Exp >= 0.50);
			Console.WriteLine("  most bearish combinations available:");
			Row("  ST Bear + exposure < 0.10", o => o.St == ShortTermState.Bear && o.Exp < 0.10);
			Row("  ST Bear + Bear candle", o => o.St == ShortTermState.Bear && o.Candle == -1);
			Row("  Bear candle + exposure < 0.10", o => o.Candle == -1 && o.Exp < 0.10);
			Row("  all three bearish", o => o.St == ShortTermState.Bear && o.Candle == -1 && o.Exp < 0.10);
			Console.WriteLine("  by time of session (all states):");
			Row("  first third", o => o.BarFrac < 0.333);
			Row("  middle third", o => o.BarFrac >= 0.333 && o.BarFrac < 0.667);
			Row("  final third", o => o.BarFrac >= 0.667);
			Console.WriteLine("A bearish structure needs a bucket that is NEGATIVE and separated from zero by more");
			Console.WriteLine("than a couple of standard errors. |t| < 2 with a positive mean is the same answer as daily.");
		}
	}
}
