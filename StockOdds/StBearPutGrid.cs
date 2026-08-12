using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// ST Bear is currently SKIPPED because the shipped 0.35/0.15 put spread earned almost nothing there (IR ~0.11)
	// while carrying heavy drawdown. That is a verdict on ONE strike pair, not on the state. This sweeps the whole
	// short/long delta grid on ST Bear sessions to ask whether any pair makes the state worth trading again.
	//
	// THE BAR IS NOT ZERO. A cell that is merely positive still hurts if it is worse than the book it joins:
	// adding low-quality trades dilutes risk-adjusted return even while adding compounding. So the grid is scored
	// two ways -- ST Bear alone, and then the FULL BOOK with ST Bear re-admitted at its own best strikes, against
	// the shipped book that skips it. Only the second answers the question that matters.
	//
	// Full 21-year history rather than the gamma-data window, because this is a question about the state machine,
	// not about gamma, and the extra years are the only defence against reading one regime.
	public static class StBearPutGrid
	{
		public static double VolRiskPremium = 1.10;
		public static int    HvWindow = 60;
		public static double Risk = 0.10;
		public static double TargetLo = 0.10;
		public static double ShipShort = 0.35, ShipLong = 0.15;
		public static double[] Shorts = { 0.20, 0.25, 0.30, 0.35, 0.40, 0.50 };
		public static double[] Longs  = { 0.03, 0.05, 0.10, 0.15, 0.20 };

		private sealed record Day(DateTime D, double S, double ST, double Iv, bool IsBear, ShortTermState St);

		public static async Task Run(string symbol = "SPY")
		{
			FiveperecentBandTest.UseCalendar(symbol);
			var bars = await YahooClient.GetBarsAsync(symbol, "1d", 21);
			var eng = BankrollSimulator.Run(bars, 10_000.0);

			var pos = new Dictionary<DateTime, double>();
			for (int k = 0; k < eng.Positions.Count && k < eng.ReturnDates.Count; k++)
				pos[eng.ReturnDates[k].Date] = eng.Positions[k];
			var stm = new Dictionary<DateTime, ShortTermState>();
			for (int k = 0; k < eng.StState.Count && k < eng.ReturnDates.Count; k++)
				stm[eng.ReturnDates[k].Date] = eng.StState[k];
			var hv = new Dictionary<DateTime, double>();
			for (int i = 1; i < bars.Count; i++)
			{
				int j0 = Math.Max(1, i - (HvWindow - 1));
				var lr = new List<double>();
				for (int j = j0; j <= i; j++)
					if (bars[j - 1].Close > 0 && bars[j].Close > 0) lr.Add(Math.Log(bars[j].Close / bars[j - 1].Close));
				if (lr.Count >= 10)
				{
					double m = lr.Average();
					hv[bars[i].Date.Date] = Math.Max(0.05, Math.Sqrt(lr.Sum(x => (x - m) * (x - m)) / (lr.Count - 1)) * Math.Sqrt(252.0));
				}
			}

			double T = 1.0 / 252.0;
			var days = new List<Day>();
			for (int i = 1; i + 1 < bars.Count; i++)
			{
				var dSig = bars[i].Date.Date; var dTr = bars[i + 1].Date.Date;
				if (!hv.TryGetValue(dSig, out double h)) continue;
				if (!pos.TryGetValue(dSig, out double tg) || tg < TargetLo) continue;
				if (!FiveperecentBandTest.HasSameDayExpiry(dTr)) continue;
				double S = bars[i + 1].Open, ST = bars[i + 1].Close;
				if (S <= 0 || ST <= 0) continue;
				stm.TryGetValue(dSig, out var st);
				days.Add(new Day(dTr, S, ST, h * VolRiskPremium, st == ShortTermState.Bear, st));
			}

			// P&L per unit of max loss for one strike pair on one day; NaN when the pair is unusable
			double R(Day d, double sh, double lg)
			{
				double kS = StrikeForPutDelta(d.S, d.Iv, T, sh);
				double kL = StrikeForPutDelta(d.S, d.Iv, T, lg);
				double cr = Put(d.S, kS, d.Iv, T) - Put(d.S, kL, d.Iv, T);
				double ml = (kS - kL) - cr;
				if (cr <= 1e-9 || ml <= 1e-9) return double.NaN;
				return (cr + (-Math.Max(0, kS - d.ST) + Math.Max(0, kL - d.ST))) / ml;
			}
			(double m, double ir, double dd, double t, int n) Stat(IEnumerable<double> src)
			{
				var r = src.Where(x => !double.IsNaN(x)).Select(x => Risk * x).ToList();
				if (r.Count < 30) return (0, double.NaN, 0, 0, r.Count);
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				return (m, sd > 0 ? m / sd : 0, dd, sd > 0 ? m / (sd / Math.Sqrt(r.Count)) : 0, r.Count);
			}

			var bear = days.Where(x => x.IsBear).ToList();
			var rest = days.Where(x => !x.IsBear).ToList();
			var shipRest = Stat(rest.Select(x => R(x, ShipShort, ShipLong)));
			var shipBear = Stat(bear.Select(x => R(x, ShipShort, ShipLong)));

			// ---- HEAD TO HEAD ON THE SHIPPED DAY-SET -----------------------------------------------------
			// Specific strike pairs on the days the config actually trades (exposure >= 0.10, ST Bear skipped),
			// full 21 years. A pair is not just a delta: moving BOTH legs changes the net delta, the width, the
			// credit as a share of max loss, and therefore the SIZE a fixed 10%-risk budget buys. All four are
			// printed, because a pair that looks better per trade usually just carries less exposure.
			Console.WriteLine($"\n===== {symbol}: STRIKE PAIRS ON THE SHIPPED DAY-SET (21y, {100 * Risk:0.#}% risk) =====");
			// Windows matter more than strikes here. The same config prints ~228% CAGR over 21 years and ~970%
			// over 2022-03+, purely because the recent stretch is short and bull-heavy and compounds ~160 times a
			// year. Every window is shown so the level is never mistaken for a property of the strike pair.
			var winFrom = new[]
			{
				("21y  (full)", new DateTime(2000, 1, 1)),
				("2022-03+ (UW gamma era)", new DateTime(2022, 3, 30)),
				("2023-09+ (1h intraday era)", new DateTime(2023, 9, 7)),
			};
			var ship = days.Where(x => !x.IsBear).ToList();
			Console.WriteLine($"{ship.Count} sessions | pair = short/long delta");
			Console.WriteLine($"{"pair",14} {"netD",6} {"width%S",9} {"cr%ml",7} {"impDelta%",10} {"mean/tr%",10} " +
				$"{"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10}");
			void Pair(string tag, double sh, double lg)
			{
				var rows = ship.Select(d => new { d, R = R(d, sh, lg) }).Where(x => !double.IsNaN(x.R)).ToList();
				if (rows.Count < 50) { Console.WriteLine($"{tag,14} (too few)"); return; }
				var r = rows.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (rows.Last().d.D - rows.First().d.D).TotalDays / 365.25);
				double wsum = 0, csum = 0, dsum = 0;
				foreach (var x in rows)
				{
					double kS = StrikeForPutDelta(x.d.S, x.d.Iv, T, sh), kL = StrikeForPutDelta(x.d.S, x.d.Iv, T, lg);
					double cr = Put(x.d.S, kS, x.d.Iv, T) - Put(x.d.S, kL, x.d.Iv, T);
					double ml = (kS - kL) - cr;
					wsum += (kS - kL) / x.d.S; csum += cr / ml; dsum += (sh - lg) * x.d.S / ml;
				}
				int nn = rows.Count;
				Console.WriteLine($"{tag,14} {sh - lg,6:0.00} {100 * wsum / nn,9:0.00} {100 * csum / nn,7:0.0} " +
					$"{100 * Risk * dsum / nn,10:0.0} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0}");
			}
			var shipAll = ship;
			foreach (var (wLbl, wFrom) in winFrom)
			{
				ship = shipAll.Where(x => x.D >= wFrom).ToList();
				if (ship.Count < 100) continue;
				Console.WriteLine($"  -- {wLbl}: {ship.Count} sessions, {ship.First().D:yyyy-MM} -> {ship.Last().D:yyyy-MM} --");
				Pair("0.35/0.15 SHIP", 0.35, 0.15);
				Pair("0.20/0.10", 0.20, 0.10);
				Pair("0.25/0.15", 0.25, 0.15);
				Pair("0.30/0.15", 0.30, 0.15);
				Pair("0.20/0.05", 0.20, 0.05);
			}
			ship = shipAll;
			// ---- RE-ADMIT ST BEAR WITH A SAFER SPREAD ----------------------------------------------------
			// ST Bear is skipped because 0.35/0.15 earns almost nothing there. A further-OTM 0.20/0.10 is the
			// obvious "trade it, but carefully" answer: half the net delta, a third of the credit share, and a
			// 92%+ win rate on the normal day-set. Whether that rescues the state is a separate question from
			// whether the pair is good in general, so both readings are run -- swap the WHOLE book, or use the
			// safer pair ONLY on ST Bear days and keep shipped elsewhere.
			Console.WriteLine($"\n===== {symbol}: RE-ADMITTING ST BEAR WITH A SAFER SPREAD (21y, {100 * Risk:0.#}% risk) =====");
			Console.WriteLine($"{"book",42} {"trades",7} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10}");
			void BookPair(string lbl, Func<Day, bool> keep, Func<Day, (double sh, double lg)> pick)
			{
				var rows = days.Where(keep).Select(d => { var (sh, lg) = pick(d); return new { d, R = R(d, sh, lg) }; })
					.Where(x => !double.IsNaN(x.R)).OrderBy(x => x.d.D).ToList();
				if (rows.Count < 100) { Console.WriteLine($"{lbl,42} {rows.Count,7}  (too few)"); return; }
				var r = rows.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (rows.Last().d.D - rows.First().d.D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,42} {r.Count,7} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0}");
			}
			BookPair("0.35/0.15, SKIP ST Bear  [SHIPPED]", d => !d.IsBear, _ => (0.35, 0.15));
			BookPair("0.35/0.15 everywhere (no skip)", _ => true, _ => (0.35, 0.15));
			BookPair("0.35/0.15 normal + 0.20/0.10 on Bear", _ => true, d => d.IsBear ? (0.20, 0.10) : (0.35, 0.15));
			BookPair("0.35/0.15 normal + 0.20/0.05 on Bear", _ => true, d => d.IsBear ? (0.20, 0.05) : (0.35, 0.15));
			BookPair("0.20/0.10 everywhere (no skip)", _ => true, _ => (0.20, 0.10));
			BookPair("0.20/0.10 everywhere, SKIP ST Bear", d => !d.IsBear, _ => (0.20, 0.10));
			Console.WriteLine("  and the ST Bear slice alone, for reference:");
			BookPair("  ST Bear only @ 0.35/0.15", d => d.IsBear, _ => (0.35, 0.15));
			BookPair("  ST Bear only @ 0.20/0.10", d => d.IsBear, _ => (0.20, 0.10));

			// ---- DOES EACH ST STATE WANT A DIFFERENT STRIKE PAIR? ----------------------------------------
			// The earlier per-state test only moved the NET delta with the wing pinned at 0.15; this moves both
			// legs. Per-state conditioning has failed every time it has been tried here, so the grid is NOT the
			// test -- the best of ~16 cells per state is a maximum over noise. Split-half agreement and the
			// full-book decision underneath are what decide it.
			Console.WriteLine($"\n===== {symbol}: STRIKE PAIR BY ST STATE (21y, IR per trade) =====");
			var stKeys = new[] { ShortTermState.Bull, ShortTermState.BullNeutral,
			                     ShortTermState.BearNeutral, ShortTermState.Bear };
			double[] sSh = { 0.20, 0.25, 0.30, 0.35, 0.40 };
			double[] sLg = { 0.05, 0.10, 0.15, 0.20 };
			var ordAll = days.OrderBy(x => x.D).ToList();
			DateTime pmid = ordAll[ordAll.Count / 2].D;
			var bestPair = new Dictionary<ShortTermState, (double sh, double lg)>();
			foreach (var stk in stKeys)
			{
				var g = days.Where(x => x.St == stk).ToList();
				Console.WriteLine($"  -- {stk}: {g.Count} sessions --");
				Console.Write($"{"short/long",14}");
				foreach (double lg in sLg) Console.Write($" {lg,8:0.00}");
				Console.WriteLine();
				double bIr = double.NegativeInfinity; double bs = 0.35, bl = 0.15;
				foreach (double sh in sSh)
				{
					Console.Write($"{sh,14:0.00}");
					foreach (double lg in sLg)
					{
						if (lg >= sh) { Console.Write($" {"-",8}"); continue; }
						var q = Stat(g.Select(x => R(x, sh, lg)));
						if (double.IsNaN(q.ir)) { Console.Write($" {"(few)",8}"); continue; }
						if (q.ir > bIr) { bIr = q.ir; bs = sh; bl = lg; }
						Console.Write($" {q.ir,8:0.000}");
					}
					Console.WriteLine();
				}
				string Half(Func<Day, bool> h)
				{
					double b2 = double.NegativeInfinity; string tag = "-";
					foreach (double sh in sSh) foreach (double lg in sLg)
					{
						if (lg >= sh) continue;
						var q2 = Stat(g.Where(h).Select(x => R(x, sh, lg)));
						if (!double.IsNaN(q2.ir) && q2.ir > b2) { b2 = q2.ir; tag = $"{sh:0.00}/{lg:0.00}"; }
					}
					return tag;
				}
				bestPair[stk] = (bs, bl);
				Console.WriteLine($"      best {bs:0.00}/{bl:0.00} (IR {bIr:0.000})   1st half {Half(x => x.D < pmid)}   2nd half {Half(x => x.D >= pmid)}");
			}
			Console.WriteLine($"\n--- FULL BOOK: flat shipped pair vs per-state pairs (ST Bear still skipped) ---");
			Console.WriteLine($"{"book",42} {"trades",7} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10}");
			BookPair("flat 0.35/0.15 [SHIPPED]", d => !d.IsBear, _ => (0.35, 0.15));
			BookPair("per-state best pair (in-sample)", d => !d.IsBear, d => bestPair[d.St]);

			// ---- 0.40/0.05 AS A FLAT PAIR ----------------------------------------------------------------
			// 0.40/0.05 was ST Bull's in-sample optimum. Adopting it everywhere is a different proposition from
			// using it conditionally: net delta 0.35 rather than 0.20, a much wider spread, and a far bigger
			// credit share -- so it is a leverage change as much as a strike change. Split-half is shown because
			// the pair was SELECTED on this data and a fitted pair should be expected to decay out of sample.
			Console.WriteLine($"\n===== {symbol}: 0.40/0.05 AS THE FLAT PAIR (ST Bear skipped) =====");
			Console.WriteLine($"{"pair / window",34} {"trades",7} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10} {"worst%",8}");
			var shipDays = days.Where(x => !x.IsBear).OrderBy(x => x.D).ToList();
			DateTime hmid = shipDays[shipDays.Count / 2].D;
			void Flat(string tag, double sh, double lg, Func<Day, bool> win)
			{
				var rows = shipDays.Where(win).Select(d => new { d, R = R(d, sh, lg) })
					.Where(x => !double.IsNaN(x.R)).ToList();
				if (rows.Count < 80) { Console.WriteLine($"{tag,34} {rows.Count,7}  (too few)"); return; }
				var r = rows.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (rows.Last().d.D - rows.First().d.D).TotalDays / 365.25);
				Console.WriteLine($"{tag,34} {r.Count,7} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0} {100 * r.Min(),8:0.00}");
			}
			Flat("0.35/0.15 SHIPPED  21y", 0.35, 0.15, _ => true);
			Flat("0.40/0.05          21y", 0.40, 0.05, _ => true);
			Flat("0.40/0.10          21y", 0.40, 0.10, _ => true);
			Flat("0.35/0.05          21y", 0.35, 0.05, _ => true);
			// Pushing the SHORT leg to 0.50 puts it at the money -- roughly a coin flip on breach every session.
			// Net delta rises to 0.35 while the wing stays put, so this is the high-delta end of the sweep that
			// already rolled over; carried with neighbours so the shape is visible rather than a single point.
			Flat("0.45/0.15          21y", 0.45, 0.15, _ => true);
			Flat("0.50/0.15          21y", 0.50, 0.15, _ => true);
			Flat("0.50/0.20          21y", 0.50, 0.20, _ => true);
			Flat("0.50/0.10          21y", 0.50, 0.10, _ => true);
			Flat("0.50/0.30          21y", 0.50, 0.30, _ => true);
			Console.WriteLine("  split-half (the pair was chosen on this data, so decay is the thing to watch):");
			Flat("  0.35/0.15 SHIPPED  1st half", 0.35, 0.15, x => x.D < hmid);
			Flat("  0.40/0.05          1st half", 0.40, 0.05, x => x.D < hmid);
			Flat("  0.35/0.15 SHIPPED  2nd half", 0.35, 0.15, x => x.D >= hmid);
			Flat("  0.40/0.05          2nd half", 0.40, 0.05, x => x.D >= hmid);
			Flat("  0.50/0.15          1st half", 0.50, 0.15, x => x.D < hmid);
			Flat("  0.50/0.15          2nd half", 0.50, 0.15, x => x.D >= hmid);

			Console.WriteLine("cr%ml is credit as a share of max loss -- how much of the spread you are paid up front.");
			Console.WriteLine("IR is comparable ACROSS windows; CAGR is not -- compare CAGR only within a block.");

			Console.WriteLine($"\n===== {symbol}: PUT-SPREAD DELTA GRID ON ST BEAR SESSIONS =====");
			Console.WriteLine($"{days.Count} sessions total, {bear.Count} ST Bear ({100.0 * bear.Count / days.Count:0.0}%) | " +
				$"{days.First().D:yyyy-MM} -> {days.Last().D:yyyy-MM}");
			Console.WriteLine($"shipped {ShipShort:0.00}/{ShipLong:0.00}:  non-Bear book IR {shipRest.ir:0.000} (n {shipRest.n})   " +
				$"ST Bear IR {shipBear.ir:0.000}, mean {100 * shipBear.m:+0.0000;-0.0000}%, maxDD {shipBear.dd:0.0} (n {shipBear.n})");

			Console.WriteLine($"\nST BEAR ONLY -- IR by strike pair (rows SHORT delta, cols LONG delta)");
			Console.Write($"{"short\\long",12}");
			foreach (double l in Longs) Console.Write($" {l,8:0.00}");
			Console.WriteLine();
			(double sh, double lg, double ir, double m, double dd, double t, int n) best =
				(0, 0, double.NegativeInfinity, 0, 0, 0, 0);
			int cells = 0;
			foreach (double sh in Shorts)
			{
				Console.Write($"{sh,12:0.00}");
				foreach (double lg in Longs)
				{
					if (lg >= sh) { Console.Write($" {"-",8}"); continue; }
					var st = Stat(bear.Select(x => R(x, sh, lg)));
					if (double.IsNaN(st.ir)) { Console.Write($" {"(few)",8}"); continue; }
					cells++;
					if (st.ir > best.ir) best = (sh, lg, st.ir, st.m, st.dd, st.t, st.n);
					Console.Write($" {st.ir,8:+0.000;-0.000}");
				}
				Console.WriteLine();
			}
			Console.WriteLine($"\nbest ST Bear cell: short {best.sh:0.00} / long {best.lg:0.00} -> IR {best.ir:0.000}, " +
				$"mean {100 * best.m:+0.0000;-0.0000}%, maxDD {best.dd:0.0}, n {best.n}, t {best.t:+0.00;-0.00} " +
				$"({cells} cells searched)");

			// The decision: does re-admitting ST Bear at its own best strikes beat skipping it?
			Console.WriteLine($"\n--- FULL BOOK: skip ST Bear, vs re-admit it at its best strikes ---");
			Console.WriteLine($"{"book",44} {"trades",7} {"mean/tr%",10} {"win%",7} {"IR",8} {"maxDD%",8} {"CAGR%",10}");
			void Book(string lbl, IEnumerable<(DateTime D, double R)> src)
			{
				var l = src.Where(x => !double.IsNaN(x.R)).OrderBy(x => x.D).ToList();
				var r = l.Select(x => Risk * x.R).ToList();
				double m = r.Average();
				double sd = Math.Sqrt(r.Sum(z => (z - m) * (z - m)) / (r.Count - 1));
				double e = 1, pk = 1, dd = 0;
				foreach (var x in r) { e *= 1 + x; if (e <= 0) { e = 0; break; } if (e > pk) pk = e; double q = (pk - e) / pk * 100; if (q > dd) dd = q; }
				double yrs = Math.Max(1.0, (l.Last().D - l.First().D).TotalDays / 365.25);
				Console.WriteLine($"{lbl,44} {r.Count,7} {100 * m,10:+0.0000;-0.0000} " +
					$"{100.0 * r.Count(z => z > 0) / r.Count,7:0.0} {(sd > 0 ? m / sd : 0),8:0.000} {dd,8:0.00} " +
					$"{(e > 0 ? (Math.Pow(e, 1 / yrs) - 1) * 100 : -100),10:0.0}");
			}
			var restRows = rest.Select(x => (x.D, R: R(x, ShipShort, ShipLong)));
			Book("skip ST Bear [SHIPPED]", restRows);
			Book($"re-admit ST Bear at {ShipShort:0.00}/{ShipLong:0.00}",
				restRows.Concat(bear.Select(x => (x.D, R: R(x, ShipShort, ShipLong)))));
			Book($"re-admit ST Bear at its BEST {best.sh:0.00}/{best.lg:0.00}",
				restRows.Concat(bear.Select(x => (x.D, R: R(x, best.sh, best.lg)))));
			Console.WriteLine("The best-cell row is optimistic by construction -- those strikes were chosen ON this data.");
		}

		private static double Nd(double x) => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));
		private static double Erf(double x)
		{
			double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
			double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
			return x >= 0 ? y : -y;
		}
		private static double Put(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return Math.Max(0, K - S);
			double v = iv * Math.Sqrt(T);
			double d1 = (Math.Log(S / K) + 0.5 * iv * iv * T) / v;
			return K * Nd(v - d1) - S * Nd(-d1);
		}
		private static double PutDeltaMag(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return S < K ? 1 : 0;
			double v = iv * Math.Sqrt(T);
			return Nd(-((Math.Log(S / K) + 0.5 * iv * iv * T) / v));
		}
		private static double StrikeForPutDelta(double S, double iv, double T, double mag)
		{
			double lo = S * 0.02, hi = S * 3.0;
			for (int i = 0; i < 90; i++) { double mid = 0.5 * (lo + hi); if (PutDeltaMag(S, mid, iv, T) < mag) lo = mid; else hi = mid; }
			return 0.5 * (lo + hi);
		}
	}
}
