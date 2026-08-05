using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ONE TRADE PER DAY, opening candle skipped:
	//   - session bar 0 (the first candle of the day) is observed, never traded
	//   - at session bar 1 enter in the direction of the LT state: LONG if Bull, SHORT if Bear
	//   - exit when the LT state flips away from the entry state, or at the close
	//   - after an exit, stay flat for the rest of that session (one trade per day)
	//   - never hold overnight
	//
	// Scored on DAILY returns (one trade per day makes the per-bar series mostly zeros, so per-bar Sharpe
	// annualised by bars/year would be meaningless). Long and short legs are reported separately because the
	// daily research says short exposure has never paid in this engine -- if the short leg is the whole loss,
	// that is a different conclusion from "the entry timing does not work".
	public static class OpeningLtTest
	{
		public static double[] CostsBps = { 0.0, 1.0, 2.0, 5.0 };
		public static bool AllowShort = true;

		public static async Task Run(string symbol, string interval, string range)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 100) { Console.WriteLine($"{symbol} {interval}: only {bars.Count} bars"); return; }

			// group into sessions, preserving order
			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= 3).ToList();

			// LT state as of each bar (state is derived from the two PRIOR bars -> no look-ahead)
			var lt = new LongTermStateEngine();
			var stateAt = new LongTermState?[bars.Count];
			for (int i = 2; i < bars.Count; i++) stateAt[i] = lt.Update(bars[i - 2], bars[i - 1]);

			var trades = new List<(DateTime Day, int Dir, double Ret, int Bars, string Exit)>();
			var dailyRet = new List<double>();
			var dailyBh = new List<double>();
			var dayTraded = new List<bool>();   // did a trade actually get taken that session?

			foreach (var s in sessions)
			{
				int first = s[0];
				if (first + 1 >= bars.Count || stateAt[first + 1] == null) continue;

				// buy-and-hold comparison over the same tradeable span (bar 0 close -> session close)
				double bhDay = bars[first].Close > 0 ? (bars[s[^1]].Close - bars[first].Close) / bars[first].Close : 0;
				dailyBh.Add(bhDay);

				var entryState = stateAt[first + 1]!.Value;
				int dir = entryState == LongTermState.Bull ? 1 : (AllowShort ? -1 : 0);
				if (dir == 0) { dailyRet.Add(0); dayTraded.Add(false); continue; }

				double gross = 0; int held = 0; string exit = "close";
				for (int k = 1; k < s.Count; k++)
				{
					int i = s[k];
					if (stateAt[i] == null) continue;
					// state flipped away from the entry read -> close the trade, stay flat the rest of the day
					if (stateAt[i]!.Value != entryState) { exit = "state"; break; }
					double r = bars[i - 1].Close > 0 ? (bars[i].Close - bars[i - 1].Close) / bars[i - 1].Close : 0;
					gross += dir * r;
					held++;
				}

				trades.Add((bars[first].Date.Date, dir, gross, held, exit));
				dailyRet.Add(gross); dayTraded.Add(true);
			}

			int nL = trades.Count(t => t.Dir > 0), nS = trades.Count(t => t.Dir < 0);
			var lo = trades.Where(t => t.Dir > 0).ToList();
			var sh = trades.Where(t => t.Dir < 0).ToList();

			Console.WriteLine($"\n===== {symbol} {interval}: ONE TRADE/DAY, SKIP FIRST CANDLE, EXIT ON LT FLIP OR CLOSE =====");
			Console.WriteLine($"{bars.Count} bars | {sessions.Count} sessions | {bars[0].Date:yyyy-MM-dd} -> {bars[^1].Date:yyyy-MM-dd} | " +
				$"short leg {(AllowShort ? "ON" : "OFF")}");
			Console.WriteLine($"trades {trades.Count} ({nL} long / {nS} short) | " +
				$"exits: {trades.Count(t => t.Exit == "state")} on LT flip, {trades.Count(t => t.Exit == "close")} at the close | " +
				$"mean bars held {(trades.Count > 0 ? trades.Average(t => t.Bars) : 0):0.0}");

			Console.WriteLine($"\n{"leg",8} {"n",5} {"mean%",8} {"median%",8} {"win%",6} {"best%",7} {"worst%",7} {"total%",8}");
			void Leg(string l, List<(DateTime Day, int Dir, double Ret, int Bars, string Exit)> t)
			{
				if (t.Count == 0) { Console.WriteLine($"{l,8} {0,5}"); return; }
				Console.WriteLine($"{l,8} {t.Count,5} {t.Average(x => x.Ret) * 100,8:+0.000;-0.000} " +
					$"{Median(t.Select(x => x.Ret).ToList()) * 100,8:+0.000;-0.000} " +
					$"{100.0 * t.Count(x => x.Ret > 0) / t.Count,6:0.0} {t.Max(x => x.Ret) * 100,7:+0.00;-0.00} " +
					$"{t.Min(x => x.Ret) * 100,7:+0.00;-0.00} {t.Sum(x => x.Ret) * 100,8:+0.0;-0.0}");
			}
			Leg("ALL", trades); Leg("long", lo); Leg("short", sh);

			Console.WriteLine($"\n{"cost",6} {"ret%",9} {"maxDD%",8} {"Sharpe",8} {"| B&H(day) ret%",16} {"maxDD%",8} {"Sharpe",8}");
			foreach (double bps in CostsBps)
			{
				// exactly one round trip on each day a trade was actually taken
				var net = dailyRet.Select((r, i) => r - (dayTraded[i] ? bps / 10000.0 : 0.0)).ToList();
				Console.WriteLine($"{bps,6:0.0} {Compound(net),9:0.00} {MaxDd(net),8:0.00} {Sharpe(net, 252),8:0.000} " +
					$"{Compound(dailyBh),16:0.00} {MaxDd(dailyBh),8:0.00} {Sharpe(dailyBh, 252),8:0.000}");
			}
		}

		private static double Median(List<double> xs)
		{ if (xs.Count == 0) return 0; var s = xs.OrderBy(x => x).ToList(); int m = s.Count / 2; return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0; }
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
