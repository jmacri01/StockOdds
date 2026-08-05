using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockOdds
{
	// "Time in trade fell -- but isn't that time we SHOULDN'T have been holding?"
	//
	// Answers it directly instead of arguing from Sharpe. Every config below is compared to shipped
	// bar-by-bar (identical bar series -- the state machine runs upstream of the smoother, so the
	// position arrays align by index), and the P&L difference is DECOMPOSED into:
	//
	//   DROPPED bars  -- shipped held (>0.05), the variant is flat. This is the "less time holding
	//                    sideways or down" claim. If the claim is right, sum(-pos_ship * r) over these
	//                    bars is POSITIVE: the exposure that was removed was losing money.
	//   RESIZED bars  -- both hold, at different size. Not a time effect at all, a sizing effect.
	//   ADDED bars    -- the variant holds where shipped was flat.
	//
	// Also reports the raw quality of the dropped time: mean/median underlying return and up-rate on
	// exactly those bars, against the same stats over all bars, so "was that time bad?" is a number.
	public static class TitQuality
	{
		public static string[] Symbols =
			{ "^gspc", "aapl", "msft", "ko", "nok", "amd", "nvda", "tsla", "coin", "mstr", "smr", "asst", "asts", "open", "atai", "grpn", "fig", "be" };

		public static DateTime StartDate = new DateTime(2020, 1, 1);

		public static async Task Run(string interval)
		{
			var barsBySymbol = new Dictionary<string, List<OhlcBar>>();
			foreach (var sym in Symbols)
			{
				try
				{
					var b = (await YahooClient.GetBarsAsync(sym, interval)).Where(x => x.Date >= StartDate).ToList();
					if (b.Count >= 120) barsBySymbol[sym] = b;
				}
				catch { }
			}

			bool savedOn   = BankrollSimulator.KamaSmooth;
			int  savedMask = BankrollSimulator.KamaSmoothOffMask;

			List<BankrollResult> Eval(bool kamaOn, int offMask)
			{
				BankrollSimulator.KamaSmooth = kamaOn;
				BankrollSimulator.KamaSmoothOffMask = offMask;
				return barsBySymbol.OrderBy(kv => kv.Key)
					.Select(kv => BankrollSimulator.Run(kv.Value, 10_000.0)).ToList();
			}
			var syms = barsBySymbol.Keys.OrderBy(k => k).ToList();

			try
			{
				var shipped = Eval(true, 0);

				// underlying per-bar return, keyed by date, per symbol
				var retByDate = new Dictionary<string, Dictionary<DateTime, double>>();
				foreach (var (sym, bars) in barsBySymbol)
				{
					var m = new Dictionary<DateTime, double>();
					for (int i = 1; i < bars.Count; i++)
						if (bars[i - 1].Close > 0) m[bars[i].Date] = (bars[i].Close - bars[i - 1].Close) / bars[i - 1].Close;
					retByDate[sym] = m;
				}

				// HV bands, same cut points the earlier below-KAMA study used
				double[] edges = { 0, 30, 50, 75, 100, double.MaxValue };
				string[] bandName = { "<30", "30-50", "50-75", "75-100", "100+" };
				var bandOf = new int[syms.Count];
				for (int s = 0; s < syms.Count; s++)
				{
					double hv = Volatility.AnnualizedHistoricalPct(barsBySymbol[syms[s]]);
					int b = 0; while (b < edges.Length - 2 && hv >= edges[b + 1]) b++;
					bandOf[s] = b;
				}

				Console.WriteLine("\n===== IS THE LOST TIME-IN-TRADE TIME WORTH HOLDING? — BY HV BAND =====");
				Console.WriteLine($"{syms.Count} symbols | r = underlying close-to-close return");
				Console.WriteLine("dPnL terms are sum(dPos * r) in percentage points, pooled -- the arithmetic return the change added");
				for (int b = 0; b < bandName.Length; b++)
				{
					var mem = Enumerable.Range(0, syms.Count).Where(s => bandOf[s] == b)
						.Select(s => $"{syms[s]}({Volatility.AnnualizedHistoricalPct(barsBySymbol[syms[s]]):0})").ToList();
					if (mem.Count > 0) Console.WriteLine($"  HV {bandName[b],-7} n={mem.Count,-2} {string.Join(" ", mem)}");
				}

				void Analyze(string label, List<BankrollResult> variant)
				{
					int nb = bandName.Length;
					var dropped = new int[nb]; var totalBars = new int[nb];
					var dropR = new List<double>[nb];
					var dropPnl = new double[nb]; var resizePnl = new double[nb]; var addPnl = new double[nb];
					for (int b = 0; b < nb; b++) dropR[b] = new List<double>();

					for (int s = 0; s < syms.Count; s++)
					{
						int b = bandOf[s];
						var A = shipped[s]; var B = variant[s];
						var map = retByDate[syms[s]];
						int n = Math.Min(A.Positions.Count, B.Positions.Count);
						for (int i = 0; i < n; i++)
						{
							if (!map.TryGetValue(A.ReturnDates[i], out double r)) continue;
							double pa = A.Positions[i], pb = B.Positions[i];
							totalBars[b]++;
							bool inA = Math.Abs(pa) > 0.05, inB = Math.Abs(pb) > 0.05;
							double d = (pb - pa) * r * 100.0;
							if (inA && !inB) { dropped[b]++; dropR[b].Add(r); dropPnl[b] += d; }
							else if (!inA && inB) addPnl[b] += d;
							else resizePnl[b] += d;
						}
					}

					Console.WriteLine($"\n--- {label} ---");
					Console.WriteLine($"{"HV band",9} {"names",6} {"drop%",7} {"meanR",8} {"medR",8} {"up%",6} | " +
						$"{"dropPnL",9} {"resizePnL",10} {"totPnL",9}");
					for (int b = 0; b < nb; b++)
					{
						if (totalBars[b] == 0) continue;
						var dr = dropR[b];
						Console.WriteLine($"{bandName[b],9} {Enumerable.Range(0, syms.Count).Count(s => bandOf[s] == b),6} " +
							$"{100.0 * dropped[b] / totalBars[b],7:0.0} " +
							$"{(dr.Count > 0 ? dr.Average() * 100 : 0),8:+0.000;-0.000} {(dr.Count > 0 ? Median(dr) * 100 : 0),8:+0.000;-0.000} " +
							$"{(dr.Count > 0 ? 100.0 * dr.Count(x => x > 0) / dr.Count : 0),6:0.0} | " +
							$"{dropPnl[b],9:+0.0;-0.0} {resizePnl[b],10:+0.0;-0.0} {dropPnl[b] + resizePnl[b] + addPnl[b],9:+0.0;-0.0}");
					}
					var allDrop = dropR.SelectMany(x => x).ToList();
					Console.WriteLine($"{"TOTAL",9} {syms.Count,6} {100.0 * dropped.Sum() / Math.Max(1, totalBars.Sum()),7:0.0} " +
						$"{(allDrop.Count > 0 ? allDrop.Average() * 100 : 0),8:+0.000;-0.000} {(allDrop.Count > 0 ? Median(allDrop) * 100 : 0),8:+0.000;-0.000} " +
						$"{(allDrop.Count > 0 ? 100.0 * allDrop.Count(x => x > 0) / allDrop.Count : 0),6:0.0} | " +
						$"{dropPnl.Sum(),9:+0.0;-0.0} {resizePnl.Sum(),10:+0.0;-0.0} {dropPnl.Sum() + resizePnl.Sum() + addPnl.Sum(),9:+0.0;-0.0}");
				}

				Analyze("Bear off", Eval(true, 1 << 3));
				Analyze("Bull+Bear off", Eval(true, (1 << 0) | (1 << 3)));
				Analyze("ALL off", Eval(false, 0));

				// per-band reference: quality of ALL bars in that band, so "the dropped bars are bad" is
				// judged against the band's own baseline rather than the pooled one
				Console.WriteLine($"\n--- reference: ALL bars, by band ---");
				Console.WriteLine($"{"HV band",9} {"bars",8} {"meanR",8} {"medR",8} {"up%",6}");
				for (int b = 0; b < bandName.Length; b++)
				{
					var rs = new List<double>();
					for (int s = 0; s < syms.Count; s++)
					{
						if (bandOf[s] != b) continue;
						var A = shipped[s]; var map = retByDate[syms[s]];
						for (int i = 0; i < A.Positions.Count; i++)
							if (map.TryGetValue(A.ReturnDates[i], out double r)) rs.Add(r);
					}
					if (rs.Count == 0) continue;
					Console.WriteLine($"{bandName[b],9} {rs.Count,8} {rs.Average() * 100,8:+0.000;-0.000} {Median(rs) * 100,8:+0.000;-0.000} " +
						$"{100.0 * rs.Count(x => x > 0) / rs.Count,6:0.0}");
				}

			}
			finally
			{
				BankrollSimulator.KamaSmooth = savedOn;
				BankrollSimulator.KamaSmoothOffMask = savedMask;
			}
		}

		private static double Median(List<double> xs)
		{
			if (xs.Count == 0) return 0.0;
			var s = xs.OrderBy(x => x).ToList();
			int m = s.Count / 2;
			return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
		}
	}
}
