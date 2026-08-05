using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// The simplest possible use of the LT state machine on an intraday chart: LONG while LT Bull, FLAT while
	// LT Bear. No smoothing, no bias, no RSI trim, no scaler -- none of the daily engine's layers.
	//
	// LongTermStateEngine is SCALE-FREE (a bull candle is just close > prior high; anchors trail the swing),
	// so it ports to 5m with nothing to re-tune. It does flip far more often at this resolution, which is why
	// trade count and cost sensitivity are reported rather than a single headline number.
	//
	// No look-ahead: state is computed from (bars[i-2], bars[i-1]) and the resulting position is held over the
	// move from bars[i-1].Close into bars[i].Close -- the same convention as the daily engine.
	public static class LtIntradayTest
	{
		public static string Symbol   = "^GSPC";
		public static string Interval = "5m";
		public static string Range    = "60d";

		// round-trip cost in basis points of notional, applied on every position change
		public static double[] CostsBps = { 0.0, 1.0, 2.0, 5.0 };

		// Is 5m simply too fast for a swing-structure detector? Ladder the timeframe. 1h also carries 730 days
		// of history rather than 60, so it doubles as the fix for the sample-size problem.
		public static async Task Ladder(string symbol = "^GSPC")
		{
			Symbol = symbol;
			foreach (var (iv, rg) in new[] { ("5m", "60d"), ("15m", "60d"), ("30m", "60d"), ("1h", "730d"), ("1d", "5y") })
			{
				Interval = iv; Range = rg;
				try { await Run(); } catch (Exception ex) { Console.WriteLine($"{iv}: {ex.Message}"); }
			}
		}

		public static async Task Run()
		{
			var bars = await IntradayClient.GetAsync(Symbol, Interval, Range);
			if (bars.Count < 100) { Console.WriteLine($"only {bars.Count} bars"); return; }

			int sessions = bars.Select(b => b.Date.Date).Distinct().Count();
			double barsPerDay = (double)bars.Count / sessions;
			double periodsPerYear = barsPerDay * 252.0;

			Console.WriteLine($"\n===== LT-ONLY STRATEGY ON {Symbol} {Interval} =====");
			Console.WriteLine($"{bars.Count} bars | {sessions} sessions | {barsPerDay:0.0} bars/session | " +
				$"{bars[0].Date:yyyy-MM-dd} -> {bars[^1].Date:yyyy-MM-dd}");
			Console.WriteLine($"annualising Sharpe with {periodsPerYear:N0} periods/yr");
			Console.WriteLine("rule: LONG while LT Bull, FLAT while LT Bear. No other layer.\n");

			// ---- walk the state machine ----
			var lt = new LongTermStateEngine();
			var pos = new List<double>();      // position held into each scored bar
			var ret = new List<double>();      // underlying return of that bar
			var overnight = new List<bool>();  // is this the gap from one session into the next?
			var dates = new List<DateTime>();

			for (int i = 2; i < bars.Count; i++)
			{
				var s = lt.Update(bars[i - 2], bars[i - 1]);
				double r = bars[i - 1].Close > 0 ? (bars[i].Close - bars[i - 1].Close) / bars[i - 1].Close : 0.0;
				pos.Add(s == LongTermState.Bull ? 1.0 : 0.0);
				ret.Add(r);
				overnight.Add(bars[i].Date.Date != bars[i - 1].Date.Date);
				dates.Add(bars[i].Date);
			}

			int flips = 0;
			for (int i = 1; i < pos.Count; i++) if (pos[i] != pos[i - 1]) flips++;
			double timeIn = 100.0 * pos.Count(p => p > 0) / pos.Count;

			Console.WriteLine($"{"variant",22} {"cost",6} {"ret%",9} {"B&H%",9} {"maxDD%",8} {"bhDD%",8} " +
				$"{"Sharpe",8} {"bhShp",8} {"trades",7} {"timeIn%",8}");

			void Score(string label, Func<int, double> position)
			{
				var p = Enumerable.Range(0, pos.Count).Select(position).ToList();
				int tr = 0;
				for (int i = 1; i < p.Count; i++) if (Math.Abs(p[i] - p[i - 1]) > 1e-9) tr++;

				foreach (double bps in CostsBps)
				{
					var r = new List<double>();
					for (int i = 0; i < p.Count; i++)
					{
						double turn = i == 0 ? Math.Abs(p[0]) : Math.Abs(p[i] - p[i - 1]);
						r.Add(p[i] * ret[i] - turn * bps / 10000.0);
					}
					Console.WriteLine($"{(bps == CostsBps[0] ? label : ""),22} {bps,6:0.0} {Compound(r),9:0.00} " +
						$"{Compound(ret),9:0.00} {MaxDd(r),8:0.00} {MaxDd(ret),8:0.00} " +
						$"{Sharpe(r, periodsPerYear),8:0.000} {Sharpe(ret, periodsPerYear),8:0.000} " +
						$"{(bps == CostsBps[0] ? tr.ToString() : ""),7} " +
						$"{(bps == CostsBps[0] ? (100.0 * p.Count(x => x > 0) / p.Count).ToString("0.0") : ""),8}");
				}
				Console.WriteLine();
			}

			Score("LT long/flat", i => pos[i]);
			// SPX prints only in the cash session, so holding "through" a bar boundary that spans the close
			// captures the overnight gap. That is a materially different bet from an intraday rule, so it is
			// separated out rather than buried in the headline.
			Score("...flat overnight", i => overnight[i] ? 0.0 : pos[i]);

			// how much of the edge is the overnight gap alone?
			double gapPnl = Enumerable.Range(0, pos.Count).Where(i => overnight[i]).Sum(i => pos[i] * ret[i]) * 100;
			double dayPnl = Enumerable.Range(0, pos.Count).Where(i => !overnight[i]).Sum(i => pos[i] * ret[i]) * 100;
			int gapBars = overnight.Count(x => x);
			Console.WriteLine($"P&L split (arithmetic): overnight gaps {gapPnl:+0.00;-0.00} pts over {gapBars} bars | " +
				$"intraday {dayPnl:+0.00;-0.00} pts over {pos.Count - gapBars} bars");
			Console.WriteLine($"state flips: {flips} over {sessions} sessions = {(double)flips / sessions:0.0}/session");
		}

		private static double Compound(List<double> r) { double e = 1; foreach (var x in r) e *= 1 + x; return (e - 1) * 100; }
		private static double MaxDd(List<double> r)
		{ double e = 1, p = 1, d = 0; foreach (var x in r) { e *= 1 + x; if (e > p) p = e; double q = (p - e) / p; if (q > d) d = q; } return d * 100; }
		private static double Sharpe(List<double> r, double ppy)
		{
			if (r.Count < 2) return 0;
			double m = r.Average(), v = r.Sum(x => (x - m) * (x - m)) / (r.Count - 1), sd = Math.Sqrt(v);
			return sd > 0 ? m / sd * Math.Sqrt(ppy) : 0;
		}
	}
}
