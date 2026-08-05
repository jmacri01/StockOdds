using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// WAIT FOR AN LT FLIP, THEN ENTER. One trade per day.
	//   - the flip must become visible within the first EntryHours of the session (default 3 -> before 12:30 ET)
	//   - enter in the direction of the NEW state (flip to Bull = long, flip to Bear = short)
	//   - close ExitHoursBeforeClose before the bell (default 1 -> 15:00 ET)
	//   - one trade per session, never overnight
	//
	// Bar-stamp convention: a bar stamped T covers [T, T + barLen), so its CLOSE is the price at T + barLen.
	// Holding "to 15:00" therefore means holding through the bar stamped 14:55 on a 5m chart. On coarse intervals
	// the constraint is necessarily lumpy -- at 1h the last holdable bar is the one stamped 13:30 (closing 14:30),
	// and only 3 bars (09:30/10:30/11:30) can even host an entry -- which is reported rather than papered over.
	//
	// No look-ahead: stateAt[i] is derived from bars[i-2], bars[i-1], so it is known at the OPEN of bar i, and
	// entering "on bar i" captures bar i's own close-to-close move.
	public static class FlipEntryTest
	{
		public static double[] CostsBps = { 0.0, 0.5, 1.0, 2.0 };
		public static double EntryHours = 3.0;
		public static double ExitHoursBeforeClose = 1.0;
		public static bool AllowShort = true;
		public static bool ExitOnReverseFlip = false;   // spec says exit on the clock; this is the comparison arm

		private static TimeSpan BarLen(string interval) => interval switch
		{
			"5m" => TimeSpan.FromMinutes(5), "15m" => TimeSpan.FromMinutes(15),
			"30m" => TimeSpan.FromMinutes(30), "1h" => TimeSpan.FromHours(1),
			_ => TimeSpan.FromMinutes(5)
		};

		public static async Task Run(string symbol, string interval, string range)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 100) { Console.WriteLine($"{symbol} {interval}: only {bars.Count} bars"); return; }
			var bl = BarLen(interval);

			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= 4).ToList();

			var lt = new LongTermStateEngine();
			var stateAt = new LongTermState?[bars.Count];
			for (int i = 2; i < bars.Count; i++) stateAt[i] = lt.Update(bars[i - 2], bars[i - 1]);

			var trades = new List<(DateTime Day, int Dir, double Ret, int Bars, TimeSpan Entry, string Exit)>();
			var dailyRet = new List<double>(); var dailyBh = new List<double>(); var traded = new List<bool>();
			int noFlipDays = 0;

			foreach (var s in sessions)
			{
				var start = bars[s[0]].Date.TimeOfDay;
				var sessEnd = bars[s[^1]].Date.TimeOfDay + bl;
				if (sessEnd > TimeSpan.FromHours(16)) sessEnd = TimeSpan.FromHours(16);
				var entryCutoff = start + TimeSpan.FromHours(EntryHours);
				var exitCutoff  = sessEnd - TimeSpan.FromHours(ExitHoursBeforeClose);

				// buy & hold over the same tradeable span, for reference
				int bhA = s.First(i => bars[i].Date.TimeOfDay >= start);
				var holdable = s.Where(i => bars[i].Date.TimeOfDay + bl <= exitCutoff).ToList();
				dailyBh.Add(holdable.Count > 1 && bars[bhA].Close > 0
					? (bars[holdable[^1]].Close - bars[bhA].Close) / bars[bhA].Close : 0);

				// first flip visible inside the entry window
				int entryIdx = -1; LongTermState newState = LongTermState.Bull;
				foreach (int i in s)
				{
					if (i < 3 || stateAt[i] == null || stateAt[i - 1] == null) continue;
					var t = bars[i].Date.TimeOfDay;
					if (t >= entryCutoff) break;
					if (t + bl > exitCutoff) break;
					if (stateAt[i]!.Value != stateAt[i - 1]!.Value) { entryIdx = i; newState = stateAt[i]!.Value; break; }
				}
				if (entryIdx < 0) { noFlipDays++; dailyRet.Add(0); traded.Add(false); continue; }

				int dir = newState == LongTermState.Bull ? 1 : (AllowShort ? -1 : 0);
				if (dir == 0) { dailyRet.Add(0); traded.Add(false); continue; }

				double gross = 0; int held = 0; string exit = "clock";
				foreach (int i in s.Where(i => i >= entryIdx))
				{
					if (bars[i].Date.TimeOfDay + bl > exitCutoff) break;
					if (ExitOnReverseFlip && i > entryIdx && stateAt[i] != null && stateAt[i]!.Value != newState)
					{ exit = "reflip"; break; }
					double r = bars[i - 1].Close > 0 ? (bars[i].Close - bars[i - 1].Close) / bars[i - 1].Close : 0;
					gross += dir * r; held++;
				}
				trades.Add((bars[entryIdx].Date.Date, dir, gross, held, bars[entryIdx].Date.TimeOfDay, exit));
				dailyRet.Add(gross); traded.Add(true);
			}

			var lo = trades.Where(t => t.Dir > 0).ToList();
			var sh = trades.Where(t => t.Dir < 0).ToList();

			Console.WriteLine($"\n===== {symbol} {interval}: ENTER ON LT FLIP (first {EntryHours:0}h), EXIT {ExitHoursBeforeClose:0}h BEFORE CLOSE =====");
			Console.WriteLine($"{sessions.Count} sessions | {bars[0].Date:yyyy-MM-dd} -> {bars[^1].Date:yyyy-MM-dd} | " +
				$"short {(AllowShort ? "ON" : "OFF")} | reverse-flip exit {(ExitOnReverseFlip ? "ON" : "OFF")}");
			Console.WriteLine($"trades {trades.Count} ({lo.Count}L/{sh.Count}S) | no qualifying flip on {noFlipDays} sessions " +
				$"({100.0 * noFlipDays / sessions.Count:0.0}%) | mean bars held {(trades.Count > 0 ? trades.Average(t => t.Bars) : 0):0.0}" +
				(ExitOnReverseFlip ? $" | {trades.Count(t => t.Exit == "reflip")} reverse-flip exits" : ""));
			if (trades.Count > 0)
				Console.WriteLine($"entry time: median {Median(trades.Select(t => t.Entry.TotalMinutes).ToList()) / 60.0 + 0:0.00}h into session " +
					$"(earliest {trades.Min(t => t.Entry):hh\\:mm}, latest {trades.Max(t => t.Entry):hh\\:mm})");

			Console.WriteLine($"\n{"leg",8} {"n",5} {"mean%",8} {"median%",8} {"win%",6} {"best%",7} {"worst%",7} {"total%",8}");
			void Leg(string l, List<(DateTime Day, int Dir, double Ret, int Bars, TimeSpan Entry, string Exit)> t)
			{
				if (t.Count == 0) { Console.WriteLine($"{l,8} {0,5}"); return; }
				Console.WriteLine($"{l,8} {t.Count,5} {t.Average(x => x.Ret) * 100,8:+0.000;-0.000} " +
					$"{Median(t.Select(x => x.Ret).ToList()) * 100,8:+0.000;-0.000} {100.0 * t.Count(x => x.Ret > 0) / t.Count,6:0.0} " +
					$"{t.Max(x => x.Ret) * 100,7:+0.00;-0.00} {t.Min(x => x.Ret) * 100,7:+0.00;-0.00} {t.Sum(x => x.Ret) * 100,8:+0.0;-0.0}");
			}
			Leg("ALL", trades); Leg("long", lo); Leg("short", sh);

			Console.WriteLine($"\n{"cost",6} {"ret%",9} {"maxDD%",8} {"Sharpe",8} | {"B&H ret%",9} {"maxDD%",8} {"Sharpe",8}");
			foreach (double bps in CostsBps)
			{
				var net = dailyRet.Select((r, i) => r - (traded[i] ? bps / 10000.0 : 0.0)).ToList();
				Console.WriteLine($"{bps,6:0.0} {Compound(net),9:0.00} {MaxDd(net),8:0.00} {Sharpe(net, 252),8:0.000} | " +
					$"{Compound(dailyBh),9:0.00} {MaxDd(dailyBh),8:0.00} {Sharpe(dailyBh, 252),8:0.000}");
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
