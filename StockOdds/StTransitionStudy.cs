using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ST-state TRANSITIONS: forward return on the bar where the ST state changes, split by LT state.
	//
	// The naming in CandleStateEngine inverts the intuition. "BullNeutral" means "was BULL, now neutralised" --
	// it is set only when a single bear candle interrupts currentState == Bull. Likewise "BearNeutral" is set
	// only when a single bull candle interrupts currentState == Bear. So:
	//     BullNeutral ALWAYS follows Bull     (never Bear)
	//     BearNeutral ALWAYS follows Bear     (never Bull)
	// A full from->to census is printed first so this is demonstrated from the data rather than asserted from
	// reading the code.
	//
	// Same sampling as the other intraday studies: first EntryHours of the session, primed on the session's
	// first directional ST state, fixed N-bar forward return that must stay inside the session.
	public static class StTransitionStudy
	{
		public static double EntryHours = 3.5;
		public static int FwdBars = 5;

		// CROSS-INSTRUMENT test of the one cell that was sign-consistent across 5m/15m/1h:
		//   Bear -> BearNeutral (a bear run interrupted) while LT == Bull   -> expect UP
		// The mirror is carried alongside because the "interruption resolves toward the LT state" hypothesis
		// predicts both, and the mirror already failed to hold across timeframes:
		//   Bull -> BullNeutral (a bull run interrupted) while LT == Bear   -> expect DOWN
		// VOO is the discovery sample: shown for reference, excluded from the pooled verdict. SPY is excluded
		// entirely (~99% correlated with VOO, it would re-measure the same series).
		public static async Task Cross(string interval, string range, params string[] symbols)
		{
			foreach (bool bearInterrupted in new[] { true, false })
			{
				string title = bearInterrupted
					? "Bear->BearNeutral while LT Bull  (expect UP)"
					: "Bull->BullNeutral while LT Bear  (expect DOWN)";
				Console.WriteLine($"\n===== CROSS-INSTRUMENT {interval}: {title} =====");
				Console.WriteLine($"{"symbol",8} {"sess",6} {"n",5} {"mean%",9} {"SE%",8} {"t",7} {"up%",7} {"ctrl%",8} {"excess%",9}");

				var pooled = new List<double>(); int pos = 0, votable = 0;
				foreach (var sym in symbols)
				{
					List<OhlcBar> bars;
					try { bars = await IntradayClient.GetAsync(sym, interval, range); }
					catch (Exception ex) { Console.WriteLine($"{sym,8}  fetch failed: {ex.Message}"); continue; }
					if (bars.Count < 200) continue;

					var (ev, ctrl, sess) = Events(bars, bearInterrupted);
					if (ev.Count < 3) { Console.WriteLine($"{sym,8} {sess,6} {ev.Count,5}  (too few)"); continue; }

					double mean = ev.Average();
					double sd = Math.Sqrt(ev.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, ev.Count - 1));
					double se = sd / Math.Sqrt(ev.Count);
					Console.WriteLine($"{sym,8} {sess,6} {ev.Count,5} {mean * 100,9:+0.000;-0.000} {se * 100,8:0.000} " +
						$"{mean / se,7:+0.00;-0.00} {100.0 * ev.Count(x => x > 0) / ev.Count,7:0.0} " +
						$"{ctrl * 100,8:+0.000;-0.000} {(mean - ctrl) * 100,9:+0.000;-0.000}");

					if (!sym.Equals("VOO", StringComparison.OrdinalIgnoreCase))
					{
						pooled.AddRange(ev.Select(x => x - ctrl));
						votable++;
						bool right = bearInterrupted ? mean > ctrl : mean < ctrl;
						if (right) pos++;
					}
				}

				if (pooled.Count > 2)
				{
					double pm = pooled.Average();
					double psd = Math.Sqrt(pooled.Sum(x => (x - pm) * (x - pm)) / (pooled.Count - 1));
					double pse = psd / Math.Sqrt(pooled.Count);
					Console.WriteLine($"POOLED excess ex-VOO: {pm * 100:+0.000;-0.000}% (SE {pse * 100:0.000}, t {pm / pse:+0.00}), n {pooled.Count}");
					Console.WriteLine($"instruments with the PREDICTED sign: {pos}/{votable}");
				}
			}
		}

		private static (List<double> Ev, double Ctrl, int Sessions) Events(List<OhlcBar> bars, bool bearInterrupted)
		{
			var stEng = new CandleStateEngine(); var ltEng = new LongTermStateEngine();
			var st = new ShortTermState?[bars.Count]; var lt = new LongTermState?[bars.Count];
			for (int i = 1; i < bars.Count; i++) { st[i] = stEng.Update(bars[i - 1], bars[i]); lt[i] = ltEng.Update(bars[i - 1], bars[i]); }

			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= FwdBars + 2).ToList();

			var ev = new List<double>(); var ctrl = new List<double>();
			var want = bearInterrupted ? ShortTermState.BearNeutral : ShortTermState.BullNeutral;
			var from = bearInterrupted ? ShortTermState.Bear : ShortTermState.Bull;
			var wantLt = bearInterrupted ? LongTermState.Bull : LongTermState.Bear;

			foreach (var s in sessions)
			{
				var cutoff = bars[s[0]].Date.TimeOfDay + TimeSpan.FromHours(EntryHours);
				int last = s[^1], busy = -1; bool primed = false;
				foreach (int i in s)
				{
					if (i < 2 || st[i] == null || st[i - 1] == null || lt[i] == null) continue;
					if (st[i] == ShortTermState.Bull || st[i] == ShortTermState.Bear) primed = true;
					if (!primed) continue;
					if (bars[i].Date.TimeOfDay >= cutoff) break;
					int j = i + FwdBars;
					if (j > last || bars[i].Close <= 0) continue;
					double fwd = (bars[j].Close - bars[i].Close) / bars[i].Close;
					ctrl.Add(fwd);
					if (i <= busy) continue;
					if (st[i]!.Value != want || st[i - 1]!.Value != from || lt[i]!.Value != wantLt) continue;
					ev.Add(fwd); busy = j;
				}
			}
			return (ev, ctrl.Count > 0 ? ctrl.Average() : 0, sessions.Count);
		}

		public static async Task Run(string symbol, string interval, string range)
		{
			var bars = await IntradayClient.GetAsync(symbol, interval, range);
			if (bars.Count < 200) { Console.WriteLine($"{symbol} {interval}: only {bars.Count} bars"); return; }

			var stEng = new CandleStateEngine(); var ltEng = new LongTermStateEngine();
			var st = new ShortTermState?[bars.Count]; var lt = new LongTermState?[bars.Count];
			for (int i = 1; i < bars.Count; i++) { st[i] = stEng.Update(bars[i - 1], bars[i]); lt[i] = ltEng.Update(bars[i - 1], bars[i]); }

			// ---- census of every observed from -> to transition, over the WHOLE series (no sampling filters) ----
			var census = new Dictionary<(ShortTermState From, ShortTermState To), int>();
			for (int i = 2; i < bars.Count; i++)
			{
				if (st[i] == null || st[i - 1] == null) continue;
				if (st[i]!.Value == st[i - 1]!.Value) continue;
				var k = (st[i - 1]!.Value, st[i]!.Value);
				census[k] = census.GetValueOrDefault(k) + 1;
			}

			Console.WriteLine($"\n===== {symbol} {interval}: ST TRANSITION CENSUS (whole series, no filters) =====");
			foreach (ShortTermState f in Enum.GetValues<ShortTermState>())
			{
				var outs = Enum.GetValues<ShortTermState>()
					.Select(t => (t, n: census.GetValueOrDefault((f, t))))
					.Where(x => x.n > 0).ToList();
				Console.WriteLine($"  from {f,-12} -> " + (outs.Count == 0 ? "(never)" :
					string.Join(",  ", outs.Select(x => $"{x.t} {x.n}"))));
			}
			var impossible = new[]
			{
				(ShortTermState.Bear, ShortTermState.BullNeutral, "BullNeutral after Bear"),
				(ShortTermState.Bull, ShortTermState.BearNeutral, "BearNeutral after Bull"),
			};
			foreach (var (f, t, label) in impossible)
				Console.WriteLine($"  >> {label,-26}: {census.GetValueOrDefault((f, t))} occurrences");

			// ---- forward return on the transitions that DO exist, split by LT ----
			var sessions = bars.Select((b, i) => (b, i)).GroupBy(x => x.b.Date.Date)
				.Select(g => g.Select(x => x.i).ToList()).Where(g => g.Count >= FwdBars + 2).ToList();

			var rows = new Dictionary<(string Label, LongTermState Lt), List<(double Fwd, DateTime Day)>>();
			void Add(string label, LongTermState L, double fwd, DateTime day)
			{
				var k = (label, L);
				if (!rows.ContainsKey(k)) rows[k] = new();
				rows[k].Add((fwd, day));
			}

			foreach (var s in sessions)
			{
				var entryCutoff = bars[s[0]].Date.TimeOfDay + TimeSpan.FromHours(EntryHours);
				int lastInSession = s[^1]; bool primed = false;
				foreach (int i in s)
				{
					if (st[i] == null || lt[i] == null || i < 2 || st[i - 1] == null) continue;
					if (st[i] == ShortTermState.Bull || st[i] == ShortTermState.Bear) primed = true;
					if (!primed) continue;
					if (bars[i].Date.TimeOfDay >= entryCutoff) break;
					int j = i + FwdBars;
					if (j > lastInSession || bars[i].Close <= 0) continue;
					double fwd = (bars[j].Close - bars[i].Close) / bars[i].Close;

					bool changed = st[i]!.Value != st[i - 1]!.Value;
					var cur = st[i]!.Value; var prv = st[i - 1]!.Value;

					if (changed && cur == ShortTermState.BullNeutral)
						Add($"Bull->BullNeutral (bull interrupted)", lt[i]!.Value, fwd, bars[i].Date.Date);
					if (changed && cur == ShortTermState.BearNeutral)
						Add($"Bear->BearNeutral (bear interrupted)", lt[i]!.Value, fwd, bars[i].Date.Date);
					// all bars sitting in a neutral state, transition or not, for contrast
					if (cur == ShortTermState.BullNeutral)
						Add("  ...all BullNeutral bars", lt[i]!.Value, fwd, bars[i].Date.Date);
					if (cur == ShortTermState.BearNeutral)
						Add("  ...all BearNeutral bars", lt[i]!.Value, fwd, bars[i].Date.Date);
				}
			}

			Console.WriteLine($"\nforward {FwdBars} bars, first {EntryHours:0.#}h of session, in-session only");
			Console.WriteLine($"{"event",38} {"LT",6} {"n",6} {"sess",5} {"mean%",9} {"SE%",8} {"t",7} {"median%",9} {"up%",6}");
			foreach (var label in new[] { "Bull->BullNeutral (bull interrupted)", "  ...all BullNeutral bars",
										  "Bear->BearNeutral (bear interrupted)", "  ...all BearNeutral bars" })
			{
				foreach (LongTermState L in Enum.GetValues<LongTermState>())
				{
					if (!rows.TryGetValue((label, L), out var v) || v.Count == 0)
					{ Console.WriteLine($"{label,38} {L,6} {0,6}"); continue; }
					var f = v.Select(x => x.Fwd).ToList();
					int sess = v.Select(x => x.Day).Distinct().Count();
					double mean = f.Average();
					double sd = f.Count > 1 ? Math.Sqrt(f.Sum(x => (x - mean) * (x - mean)) / (f.Count - 1)) : 0;
					double se = sess > 1 ? sd / Math.Sqrt(sess) : double.NaN;
					Console.WriteLine($"{label,38} {L,6} {v.Count,6} {sess,5} {mean * 100,9:+0.000;-0.000} {se * 100,8:0.000} " +
						$"{(se > 0 ? mean / se : 0),7:+0.00;-0.00} {Median(f) * 100,9:+0.000;-0.000} " +
						$"{100.0 * f.Count(x => x > 0) / f.Count,6:0.0}");
				}
				Console.WriteLine();
			}
		}

		private static double Median(List<double> xs)
		{ var s = xs.OrderBy(x => x).ToList(); int m = s.Count / 2; return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0; }
	}
}
