using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// The FULL SHIPPED ENGINE (bucket map -> EMA -> dynamic bias -> RSI-2 trim -> HV trim -> KAMA-distance
	// smoothing -> peak-age scaler -> clamps) run on intraday bars, with three session gates layered on top:
	//   1. the first candle of each session is never traded
	//   2. no NEW entry after EntryHours into the session -- if flat at the cutoff, stay flat for the rest of
	//      the day; if already holding, the engine keeps managing the size until the forced close
	//   3. the position is closed completely ExitHoursBeforeClose before the bell, and never held overnight
	//
	// IMPORTANT CAVEAT ON SEMANTICS: every engine window is measured in BARS, not calendar time. On a 5m chart
	// the 60-bar drawdown window is ~0.8 of one session and the 150-bar bias EMA is ~2 sessions -- so this is
	// the shipped SHAPE applied at a completely different horizon, not the shipped strategy. Nothing has been
	// re-tuned; that is deliberate, since re-tuning is a separate question from "does the shape transfer".
	//
	// Costs are charged on TURNOVER (bps x |change in position|), which is the right model here because the
	// engine re-sizes continuously rather than taking discrete trades.
	public static class ShippedIntradayTest
	{
		public static double[] CostsBps = { 0.0, 0.5, 1.0, 2.0 };
		public static double EntryHours = 3.0;
		public static double ExitHoursBeforeClose = 1.0;

		private static TimeSpan BarLen(string interval) => interval switch
		{
			"5m" => TimeSpan.FromMinutes(5), "15m" => TimeSpan.FromMinutes(15),
			"30m" => TimeSpan.FromMinutes(30), "1h" => TimeSpan.FromHours(1),
			_ => TimeSpan.FromMinutes(5)
		};

		public static async Task Run(string symbol, string interval, string range)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 200) { Console.WriteLine($"{symbol} {interval}: only {bars.Count} bars"); return; }
			var bl = BarLen(interval);

			var res = BankrollSimulator.Run(bars, 10_000.0);
			if (res.Positions.Count == 0) { Console.WriteLine("engine produced no positions"); return; }

			// underlying return per scored bar, keyed by the engine's own aligned dates
			var closeBy = new Dictionary<DateTime, double>();
			for (int i = 0; i < bars.Count; i++) closeBy[bars[i].Date] = bars[i].Close;
			var rByDate = new Dictionary<DateTime, double>();
			for (int i = 1; i < bars.Count; i++)
				if (bars[i - 1].Close > 0) rByDate[bars[i].Date] = (bars[i].Close - bars[i - 1].Close) / bars[i - 1].Close;

			// session boundaries from the scored series
			var dates = res.ReturnDates;
			var sessionOf = dates.Select(d => d.Date).ToList();
			var firstStamp = new Dictionary<DateTime, TimeSpan>();
			var lastStamp = new Dictionary<DateTime, TimeSpan>();
			foreach (var d in dates)
			{
				if (!firstStamp.ContainsKey(d.Date) || d.TimeOfDay < firstStamp[d.Date]) firstStamp[d.Date] = d.TimeOfDay;
				if (!lastStamp.ContainsKey(d.Date) || d.TimeOfDay > lastStamp[d.Date]) lastStamp[d.Date] = d.TimeOfDay;
			}

			// ---- apply the session gates ----
			var pos = new List<double>(); var rets = new List<double>(); var bhIntra = new List<double>();
			bool openThisSession = false; DateTime curSession = default;

			for (int k = 0; k < res.Positions.Count && k < dates.Count; k++)
			{
				var d = dates[k];
				if (d.Date != curSession) { curSession = d.Date; openThisSession = false; }
				if (!rByDate.TryGetValue(d, out double r)) continue;

				var t = d.TimeOfDay;
				var sessEnd = lastStamp[d.Date] + bl; if (sessEnd > TimeSpan.FromHours(16)) sessEnd = TimeSpan.FromHours(16);
				var entryCutoff = firstStamp[d.Date] + TimeSpan.FromHours(EntryHours);
				var exitCutoff = sessEnd - TimeSpan.FromHours(ExitHoursBeforeClose);

				double p = res.Positions[k];
				bool firstCandle = t <= firstStamp[d.Date];             // gate 1
				bool pastExit = t + bl > exitCutoff;                     // gate 3
				bool pastEntry = t >= entryCutoff;                       // gate 2

				if (firstCandle || pastExit) p = 0;
				else if (pastEntry && !openThisSession) p = 0;           // cannot open late; already-open is managed on
				if (p != 0) openThisSession = true;
				if (pastExit) openThisSession = false;

				pos.Add(p); rets.Add(r);
				bhIntra.Add((firstCandle || pastExit) ? 0.0 : r);        // B&H restricted to the same tradeable window
			}

			// ---- daily aggregation, so Sharpe is comparable to the other intraday tests ----
			var dayKeys = new List<DateTime>(); var byDay = new Dictionary<DateTime, (double s, double b, double turn)>();
			double prevPos = 0;
			for (int i = 0; i < pos.Count; i++)
			{
				var day = dates[i].Date;
				if (!byDay.ContainsKey(day)) { byDay[day] = (0, 0, 0); dayKeys.Add(day); }
				var cur = byDay[day];
				byDay[day] = (cur.s + pos[i] * rets[i], cur.b + bhIntra[i], cur.turn + Math.Abs(pos[i] - prevPos));
				prevPos = pos[i];
			}

			double turnoverTotal = byDay.Values.Sum(v => v.turn);
			double meanExp = pos.Average(Math.Abs);
			double timeIn = 100.0 * pos.Count(p => Math.Abs(p) > 0.05) / pos.Count;

			Console.WriteLine($"\n===== {symbol} {interval}: FULL SHIPPED ENGINE + SESSION GATES =====");
			Console.WriteLine($"{bars.Count} bars | {dayKeys.Count} sessions | {bars[0].Date:yyyy-MM-dd} -> {bars[^1].Date:yyyy-MM-dd}");
			Console.WriteLine($"gates: skip first candle | no new entry after {EntryHours:0}h | flat {ExitHoursBeforeClose:0}h before close | no overnight");
			Console.WriteLine($"mean |exposure| {meanExp:0.000} | in-trade {timeIn:0.0}% of bars | " +
				$"turnover {turnoverTotal:0} units = {turnoverTotal / dayKeys.Count:0.00}/session");
			Console.WriteLine($"NOTE engine windows are in BARS: DdWindow {BankrollSimulator.DdWindow} bars = " +
				$"{BankrollSimulator.DdWindow * bl.TotalMinutes / 390.0:0.0} sessions; BiasEma {BankrollSimulator.BiasEmaPeriod} bars = " +
				$"{BankrollSimulator.BiasEmaPeriod * bl.TotalMinutes / 390.0:0.0} sessions");

			Console.WriteLine($"\n{"cost",6} {"ret%",9} {"maxDD%",8} {"Sharpe",8} | {"intraB&H%",10} {"maxDD%",8} {"Sharpe",8}");
			foreach (double bps in CostsBps)
			{
				var s = dayKeys.Select(d => byDay[d].s - byDay[d].turn * bps / 10000.0).ToList();
				var b = dayKeys.Select(d => byDay[d].b).ToList();
				Console.WriteLine($"{bps,6:0.0} {Compound(s),9:0.00} {MaxDd(s),8:0.00} {Sharpe(s, 252),8:0.000} | " +
					$"{Compound(b),10:0.00} {MaxDd(b),8:0.00} {Sharpe(b, 252),8:0.000}");
			}
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
