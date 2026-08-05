using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// Forward return by ST state on an intraday chart, measured to a fixed session exit.
	//
	//   sample   bars whose stamp is within the first EntryHours of the session (default 3.5 -> before 13:00 ET)
	//   forward  from THAT BAR'S CLOSE to the close ExitHoursBeforeClose before the bell (default 1 -> 15:00 ET)
	//   priming  only bars at or after the session's first ST Bull or ST Bear state -- sessions are skipped until
	//            one of the two DIRECTIONAL states has printed, so the neutral warm-up at the open is excluded
	//
	// No look-ahead: the ST state of bar i is Update(bars[i-1], bars[i]), known at bar i's close, which is also
	// the entry price for the forward return. The engine itself runs continuously across sessions (it is a
	// sequence tracker); only which bars get RECORDED is gated.
	//
	// This is a measurement, not a strategy: no costs, no position sizing, overlapping windows. Overlap matters
	// for reading significance -- every bar in a session shares the same exit price, so the observations inside
	// a session are heavily correlated and n is far larger than the effective sample. Session count is the
	// honest denominator, so it is printed alongside.
	public static class StFwdReturnStudy
	{
		public static double EntryHours = 3.5;
		// Rows with fewer than this many distinct sessions are hidden. Set to 1 to show everything -- a filter
		// here does not just tidy the output, it can SUPPRESS CONTRADICTING CELLS (the 15m Bear/Bear age 6-8 cell
		// that later refuted a candidate was invisible at the default of 10).
		public static int MinSessionsToShow = 10;
		public static double ExitHoursBeforeClose = 1.0;

		private static TimeSpan BarLen(string interval) => interval switch
		{
			"5m" => TimeSpan.FromMinutes(5), "15m" => TimeSpan.FromMinutes(15),
			"30m" => TimeSpan.FromMinutes(30), "1h" => TimeSpan.FromHours(1),
			_ => TimeSpan.FromMinutes(5)
		};

		// Same measurement, split across all 8 (LT, ST) buckets.
		// Cells get thin fast, so each row carries the number of distinct SESSIONS that fed it and a standard
		// error computed on sessions rather than bars -- observations inside a session share one exit price and
		// are near-duplicates, so bar-count n would understate the error by roughly sqrt(bars/session).
		// ltFirstOnly = keep ONLY the first bar of each LT series (the bar on which the LT state changed), so the
		// measurement isolates the TRANSITION rather than the persistent state.
		public static async Task Joint(string symbol, string interval, string range, bool ltFirstOnly = false)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 200) { Console.WriteLine($"{symbol} {interval}: only {bars.Count} bars"); return; }
			var bl = BarLen(interval);

			var stEng = new CandleStateEngine();
			var ltEng = new LongTermStateEngine();
			var st = new ShortTermState?[bars.Count];
			var lt = new LongTermState?[bars.Count];
			for (int i = 1; i < bars.Count; i++) { st[i] = stEng.Update(bars[i - 1], bars[i]); lt[i] = ltEng.Update(bars[i - 1], bars[i]); }

			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= 4).ToList();

			var cell = new Dictionary<(LongTermState, ShortTermState), List<(double Fwd, int Bars, DateTime Day)>>();
			foreach (LongTermState L in Enum.GetValues<LongTermState>())
				foreach (ShortTermState S in Enum.GetValues<ShortTermState>()) cell[(L, S)] = new();

			foreach (var s in sessions)
			{
				var start = bars[s[0]].Date.TimeOfDay;
				var sessEnd = bars[s[^1]].Date.TimeOfDay + bl;
				if (sessEnd > TimeSpan.FromHours(16)) sessEnd = TimeSpan.FromHours(16);
				var entryCutoff = start + TimeSpan.FromHours(EntryHours);
				var exitCutoff = sessEnd - TimeSpan.FromHours(ExitHoursBeforeClose);

				var exitBars = s.Where(i => bars[i].Date.TimeOfDay + bl <= exitCutoff).ToList();
				if (exitBars.Count == 0) continue;
				int exitIdx = exitBars[^1];
				double exitPx = bars[exitIdx].Close;

				bool primed = false;
				foreach (int i in s)
				{
					if (st[i] == null || lt[i] == null) continue;
					if (st[i] == ShortTermState.Bull || st[i] == ShortTermState.Bear) primed = true;
					if (!primed) continue;
					if (i >= exitIdx) break;
					if (bars[i].Date.TimeOfDay >= entryCutoff) break;
					if (bars[i].Close <= 0) continue;
					if (ltFirstOnly && (i == 0 || lt[i - 1] == null || lt[i - 1]!.Value == lt[i]!.Value)) continue;
					cell[(lt[i]!.Value, st[i]!.Value)].Add(((exitPx - bars[i].Close) / bars[i].Close, exitIdx - i, bars[i].Date.Date));
				}
			}

			Console.WriteLine($"\n===== {symbol} {interval}: FORWARD RETURN BY (LT, ST)" +
				(ltFirstOnly ? " — FIRST BAR OF EACH LT SERIES ONLY" : " — ALL 8 BUCKETS") + " =====");
			Console.WriteLine($"{sessions.Count} sessions | {bars[0].Date:yyyy-MM-dd} -> {bars[^1].Date:yyyy-MM-dd}");
			Console.WriteLine($"bars in the first {EntryHours:0.#}h | forward to {ExitHoursBeforeClose:0}h before the close | primed on first ST Bull/Bear");
			Console.WriteLine($"SE is per-SESSION (sd / sqrt(distinct sessions)) -- bar-count n overstates independence\n");
			Console.WriteLine($"{"LT",6} {"ST",13} {"n",6} {"sess",5} {"mean%",9} {"SE%",8} {"t",6} {"median%",9} {"up%",6} {"stdev%",8}");

			var all = new List<(double Fwd, int Bars, DateTime Day)>();
			foreach (LongTermState L in Enum.GetValues<LongTermState>())
			{
				foreach (ShortTermState S in Enum.GetValues<ShortTermState>())
				{
					var v = cell[(L, S)]; all.AddRange(v);
					Row(L.ToString(), S.ToString(), v);
				}
				var ltAll = Enum.GetValues<ShortTermState>().SelectMany(S => cell[(L, S)]).ToList();
				Row(L.ToString(), "-- all --", ltAll);
				Console.WriteLine();
			}
			Row("ALL", "", all);

			void Row(string a, string b, List<(double Fwd, int Bars, DateTime Day)> v)
			{
				if (v.Count == 0) { Console.WriteLine($"{a,6} {b,13} {0,6}"); return; }
				var f = v.Select(x => x.Fwd).ToList();
				int sess = v.Select(x => x.Day).Distinct().Count();
				double mean = f.Average();
				double sd = f.Count > 1 ? Math.Sqrt(f.Sum(x => (x - mean) * (x - mean)) / (f.Count - 1)) : 0;
				double se = sess > 1 ? sd / Math.Sqrt(sess) : double.NaN;
				Console.WriteLine($"{a,6} {b,13} {v.Count,6} {sess,5} {mean * 100,9:+0.000;-0.000} {se * 100,8:0.000} " +
					$"{(se > 0 ? mean / se : 0),6:+0.00;-0.00} {Median(f) * 100,9:+0.000;-0.000} " +
					$"{100.0 * f.Count(x => x > 0) / f.Count,6:0.0} {sd * 100,8:0.000}");
			}
		}

		// Forward return by (LT, ST) AND the age of the current LT sequence -- how many consecutive bars the LT
		// state has held, counted continuously (the state machine does not reset at the bell). Duration 1 is the
		// transition bar, which by construction can only be Bull/Bull or Bear/Bear (both engines share one
		// candle-type stream and the same bullCount>=2 trigger), so that row is a built-in consistency check.
		// fwdBars > 0 measures a FIXED N-bar forward return instead of running to the session exit. That removes a
		// real confound in the to-exit version: there, a 09:35 bar had ~44 bars of forward return and a 12:25 bar
		// had ~7, so horizon length varied with time-of-day -- and LT run age correlates with time-of-day inside a
		// session. A fixed horizon makes every observation comparable. The window must stay inside the session
		// (no overnight), so bars too close to the close are dropped.
		public static async Task ByDuration(string symbol, string interval, string range, int fwdBars = 0)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 200) { Console.WriteLine($"{symbol} {interval}: only {bars.Count} bars"); return; }
			var bl = BarLen(interval);

			var stEng = new CandleStateEngine(); var ltEng = new LongTermStateEngine();
			var st = new ShortTermState?[bars.Count]; var lt = new LongTermState?[bars.Count];
			var run = new int[bars.Count];
			for (int i = 1; i < bars.Count; i++)
			{
				st[i] = stEng.Update(bars[i - 1], bars[i]); lt[i] = ltEng.Update(bars[i - 1], bars[i]);
				run[i] = (i > 1 && lt[i - 1] != null && lt[i - 1]!.Value == lt[i]!.Value) ? run[i - 1] + 1 : 1;
			}

			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= 4).ToList();

			(int Lo, int Hi, string Label)[] buckets =
			{
				(1,1,"1"), (2,2,"2"), (3,3,"3"), (4,4,"4"), (5,5,"5"),
				(6,8,"6-8"), (9,14,"9-14"), (15,24,"15-24"), (25,int.MaxValue,"25+")
			};

			var cell = new Dictionary<(LongTermState, ShortTermState, int), List<(double Fwd, DateTime Day)>>();
			var runHist = new List<int>();

			foreach (var s in sessions)
			{
				var start = bars[s[0]].Date.TimeOfDay;
				var sessEnd = bars[s[^1]].Date.TimeOfDay + bl;
				if (sessEnd > TimeSpan.FromHours(16)) sessEnd = TimeSpan.FromHours(16);
				var entryCutoff = start + TimeSpan.FromHours(EntryHours);
				var exitCutoff = sessEnd - TimeSpan.FromHours(ExitHoursBeforeClose);
				var exitBars = s.Where(i => bars[i].Date.TimeOfDay + bl <= exitCutoff).ToList();
				if (exitBars.Count == 0) continue;
				int exitIdx = exitBars[^1]; double exitPx = bars[exitIdx].Close;
				int lastInSession = s[^1];

				bool primed = false;
				foreach (int i in s)
				{
					if (st[i] == null || lt[i] == null) continue;
					if (st[i] == ShortTermState.Bull || st[i] == ShortTermState.Bear) primed = true;
					if (!primed) continue;
					if (bars[i].Date.TimeOfDay >= entryCutoff) break;
					if (bars[i].Close <= 0) continue;

					double fwd;
					if (fwdBars > 0)
					{
						int j = i + fwdBars;
						if (j > lastInSession) continue;              // window must stay inside the session
						fwd = (bars[j].Close - bars[i].Close) / bars[i].Close;
					}
					else
					{
						if (i >= exitIdx) break;
						fwd = (exitPx - bars[i].Close) / bars[i].Close;
					}

					int bi = Array.FindIndex(buckets, x => run[i] >= x.Lo && run[i] <= x.Hi);
					var key = (lt[i]!.Value, st[i]!.Value, bi);
					if (!cell.ContainsKey(key)) cell[key] = new();
					cell[key].Add((fwd, bars[i].Date.Date));
					runHist.Add(run[i]);
				}
			}

			runHist.Sort();
			Console.WriteLine($"\n===== {symbol} {interval}: FORWARD RETURN BY (LT, ST) x LT-SEQUENCE AGE =====");
			Console.WriteLine($"{sessions.Count} sessions | {bars[0].Date:yyyy-MM-dd} -> {bars[^1].Date:yyyy-MM-dd} | " +
				$"LT run length over sampled bars: median {runHist[runHist.Count / 2]}, p90 {runHist[(int)(runHist.Count * 0.9)]}, max {runHist[^1]}");
			Console.WriteLine($"showing rows with >= {MinSessionsToShow} distinct session(s) -- ALWAYS read n/sess before a t\n");
			Console.WriteLine($"{"LT",6} {"ST",13} {"age",7} {"n",6} {"sess",5} {"mean%",9} {"SE%",7} {"t",6} {"median%",9} {"up%",6}");

			Console.WriteLine("bucket occupancy (all ages pooled):");
			foreach (LongTermState L0 in Enum.GetValues<LongTermState>())
				foreach (ShortTermState S0 in Enum.GetValues<ShortTermState>())
				{
					var tot = cell.Where(kv => kv.Key.Item1 == L0 && kv.Key.Item2 == S0).SelectMany(kv => kv.Value).ToList();
					Console.WriteLine($"   {L0,-5} {S0,-12} n={tot.Count,5}  sessions={tot.Select(x => x.Day).Distinct().Count(),4}");
				}
			Console.WriteLine();

			foreach (LongTermState L in Enum.GetValues<LongTermState>())
			{
				bool anyRow = false;
				foreach (ShortTermState S in Enum.GetValues<ShortTermState>())
					for (int b = 0; b < buckets.Length; b++)
					{
						if (!cell.TryGetValue((L, S, b), out var v)) continue;
						int sess = v.Select(x => x.Day).Distinct().Count();
						if (sess < MinSessionsToShow) continue;
						var f = v.Select(x => x.Fwd).ToList();
						double mean = f.Average();
						double sd = f.Count > 1 ? Math.Sqrt(f.Sum(x => (x - mean) * (x - mean)) / (f.Count - 1)) : 0;
						double se = sd / Math.Sqrt(sess);
						Console.WriteLine($"{L,6} {S,13} {buckets[b].Label,7} {v.Count,6} {sess,5} {mean * 100,9:+0.000;-0.000} " +
							$"{se * 100,7:0.000} {(se > 0 ? mean / se : 0),6:+0.00;-0.00} {Median(f) * 100,9:+0.000;-0.000} " +
							$"{100.0 * f.Count(x => x > 0) / f.Count,6:0.0}");
						anyRow = true;
					}
				if (anyRow) Console.WriteLine();
			}
		}

		public static async Task Run(string symbol, string interval, string range)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 200) { Console.WriteLine($"{symbol} {interval}: only {bars.Count} bars"); return; }
			var bl = BarLen(interval);

			// ST state as of each bar
			var eng = new CandleStateEngine();
			var st = new ShortTermState?[bars.Count];
			for (int i = 1; i < bars.Count; i++) st[i] = eng.Update(bars[i - 1], bars[i]);

			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= 4).ToList();

			var obs = new Dictionary<ShortTermState, List<(double Fwd, int Bars)>>();
			foreach (ShortTermState s in Enum.GetValues<ShortTermState>()) obs[s] = new();
			int usedSessions = 0, skippedNoPrime = 0;

			foreach (var s in sessions)
			{
				var start = bars[s[0]].Date.TimeOfDay;
				var sessEnd = bars[s[^1]].Date.TimeOfDay + bl;
				if (sessEnd > TimeSpan.FromHours(16)) sessEnd = TimeSpan.FromHours(16);
				var entryCutoff = start + TimeSpan.FromHours(EntryHours);
				var exitCutoff = sessEnd - TimeSpan.FromHours(ExitHoursBeforeClose);

				// the exit reference: the last bar whose close lands at or before the exit cutoff
				var exitBars = s.Where(i => bars[i].Date.TimeOfDay + bl <= exitCutoff).ToList();
				if (exitBars.Count == 0) continue;
				int exitIdx = exitBars[^1];
				double exitPx = bars[exitIdx].Close;

				bool primed = false; bool any = false;
				foreach (int i in s)
				{
					if (st[i] == null) continue;
					if (st[i] == ShortTermState.Bull || st[i] == ShortTermState.Bear) primed = true;
					if (!primed) continue;
					if (i >= exitIdx) break;                                   // must have room to run
					if (bars[i].Date.TimeOfDay >= entryCutoff) break;          // inside the first EntryHours only
					if (bars[i].Close <= 0) continue;

					obs[st[i]!.Value].Add(((exitPx - bars[i].Close) / bars[i].Close, exitIdx - i));
					any = true;
				}
				if (any) usedSessions++; else skippedNoPrime++;
			}

			Console.WriteLine($"\n===== {symbol} {interval}: FORWARD RETURN BY ST STATE =====");
			Console.WriteLine($"{bars.Count} bars | {sessions.Count} sessions ({usedSessions} contributed, {skippedNoPrime} had no qualifying bar)");
			Console.WriteLine($"sample: bars in the first {EntryHours:0.#}h | forward return to {ExitHoursBeforeClose:0}h before the close | " +
				$"primed on the session's first ST Bull/Bear");
			Console.WriteLine($"{bars[0].Date:yyyy-MM-dd} -> {bars[^1].Date:yyyy-MM-dd}\n");

			Console.WriteLine($"{"ST state",13} {"n",7} {"mean%",9} {"median%",9} {"up%",7} {"p25%",9} {"p75%",9} {"stdev%",8} {"meanBars",9}");
			var all = new List<(double Fwd, int Bars)>();
			foreach (ShortTermState s in Enum.GetValues<ShortTermState>())
			{
				var v = obs[s]; all.AddRange(v);
				if (v.Count == 0) { Console.WriteLine($"{s,13} {0,7}"); continue; }
				Print(s.ToString(), v);
			}
			Console.WriteLine(new string('-', 92));
			Print("ALL", all);

			// directional spread: the number that matters if this is to become a signal
			var bull = obs[ShortTermState.Bull]; var bear = obs[ShortTermState.Bear];
			if (bull.Count > 0 && bear.Count > 0)
				Console.WriteLine($"\nBull minus Bear: mean {(bull.Average(x => x.Fwd) - bear.Average(x => x.Fwd)) * 100:+0.000;-0.000}% | " +
					$"median {(Median(bull.Select(x => x.Fwd).ToList()) - Median(bear.Select(x => x.Fwd).ToList())) * 100:+0.000;-0.000}% | " +
					$"up-rate {100.0 * bull.Count(x => x.Fwd > 0) / bull.Count - 100.0 * bear.Count(x => x.Fwd > 0) / bear.Count:+0.0;-0.0}pts");

			void Print(string label, List<(double Fwd, int Bars)> v)
			{
				var f = v.Select(x => x.Fwd).OrderBy(x => x).ToList();
				double mean = f.Average();
				double sd = f.Count > 1 ? Math.Sqrt(f.Sum(x => (x - mean) * (x - mean)) / (f.Count - 1)) : 0;
				Console.WriteLine($"{label,13} {v.Count,7} {mean * 100,9:+0.000;-0.000} {Median(f) * 100,9:+0.000;-0.000} " +
					$"{100.0 * f.Count(x => x > 0) / f.Count,7:0.0} {f[(int)(f.Count * 0.25)] * 100,9:+0.000;-0.000} " +
					$"{f[(int)(f.Count * 0.75)] * 100,9:+0.000;-0.000} {sd * 100,8:0.000} {v.Average(x => x.Bars),9:0.0}");
			}
		}

		private static double Median(List<double> xs)
		{ if (xs.Count == 0) return 0; var s = xs.OrderBy(x => x).ToList(); int m = s.Count / 2; return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0; }
	}
}
