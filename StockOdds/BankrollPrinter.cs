using System;

namespace StockOdds
{
	public static class BankrollPrinter
	{
		public static void Print(BankrollResult r)
		{
			Console.WriteLine("\n===== BANKROLL SIMULATION =====");
			Console.WriteLine($"Initial bankroll : {r.InitialBankroll,12:C2}");
			Console.WriteLine();

			// -------- per-run ledger (exposure ramps within a run) --------
			Console.WriteLine(
				$"{"Entry",-11} {"Exit",-11} {"State",-18} {"Side",-5} {"Stake",11} " +
				$"{"Stock%",8} {"Trade%",8} {"Bankroll",14}");

			foreach (var t in r.Trades)
			{
				string state = $"{t.Bucket.LT}-{t.Bucket.ST}";
				string side = t.Direction == TradeDirection.Long ? "LONG" : "SHORT";
				string stake = t.StakeStart == t.StakeEnd
					? $"{t.StakeStart:P0}"
					: $"{t.StakeStart:P0}->{t.StakeEnd:P0}";

				Console.WriteLine(
					$"{t.EntryDate,-11:yyyy-MM-dd} {t.ExitDate,-11:yyyy-MM-dd} {state,-18} {side,-5} " +
					$"{stake,11} {Signed(t.StockPct),8} {Signed(t.TradePct),8} {t.BankrollAfter,14:C2}");
			}

			// -------- summary --------
			Console.WriteLine();
			Console.WriteLine($"Runs             : {r.Trades.Count}  " +
			                  $"(W {r.WinCount} / L {r.LossCount}, win rate {r.WinRatePct:0.0}%)");
			Console.WriteLine($"Final bankroll   : {r.FinalBankroll,12:C2}");
			Console.WriteLine($"Total return     : {Signed(r.TotalReturnPct)}%");
			Console.WriteLine($"Buy & hold       : {r.BuyHoldFinal,12:C2}  ({Signed(r.BuyHoldReturnPct)}%)");

			// -------- risk-adjusted comparison (strategy vs buy & hold) --------
			Console.WriteLine();
			Console.WriteLine($"{"",-17}   {"Strategy",12} {"Buy & Hold",12}");
			Console.WriteLine($"{"Sharpe (ann.)",-17} : {r.SharpeRatio,12:0.00} {r.BuyHoldSharpeRatio,12:0.00}");
			Console.WriteLine($"{"Max drawdown",-17} : {"-" + r.MaxDrawdownPct.ToString("0.00") + "%",12} {"-" + r.BuyHoldMaxDrawdownPct.ToString("0.00") + "%",12}");

			if (r.OpenBucket.HasValue)
			{
				string state = $"{r.OpenBucket.Value.LT}-{r.OpenBucket.Value.ST}";
				string side = r.OpenDirection == TradeDirection.Long ? "LONG" : "SHORT";
				Console.WriteLine();
				Console.WriteLine($"Open position    : {side} {r.OpenStake:P0} in {state} (unrealized)");
			}

			PrintPerState(r);
			PrintBucketStats(r);
		}

		// Per-bucket win rate and average profit. Stats come from each run's
		// DIRECTIONAL, per-unit-of-capital stock return (long wins on up moves, short
		// on down), so they describe the bucket independently of the exposure the sim
		// used. N is the sample size behind each row.
		private static void PrintBucketStats(BankrollResult r)
		{
			var buckets = BucketStatsEngine.Compute(r.Trades);

			Console.WriteLine("\n----- PER-BUCKET WIN RATE / AVG PROFIT (per unit of capital) -----");
			Console.WriteLine(
				$"{"State",-18} {"Side",-5} {"N",4} {"W/L",8} {"Win%",7} " +
				$"{"AvgWin%",9} {"AvgLoss%",9} {"AvgProfit%",11} {"Kelly%",8}");

			foreach (var b in buckets)
			{
				string state = $"{b.Bucket.LT}-{b.Bucket.ST}";
				string side = b.Direction == TradeDirection.Long ? "LONG" : "SHORT";

				Console.WriteLine(
					$"{state,-18} {side,-5} {b.Trades,4} {$"{b.Wins}/{b.Losses}",8} {b.WinRate * 100.0,7:0.0} " +
					$"{Signed(b.AvgWin),9} {Signed(b.AvgLoss),9} {Signed(b.AvgProfit),11} {b.Kelly * 100.0,8:0.0}");
			}
		}

		// Returns grouped by state, summed over every bar spent in that state.
		// "Trade%" is the effect on the TOTAL bankroll (sized by the ramped exposure
		// and signed by long/short); "Stock%" is the raw underlying move.
		private static void PrintPerState(BankrollResult r)
		{
			Console.WriteLine("\n----- RETURNS PER STATE -----");
			Console.WriteLine(
				$"{"State",-18} {"Side",-5} {"Bars",5} " +
				$"{"TotTrade%",10} {"AvgTrade%",10} {"TotStock%",10} {"AvgStock%",10}");

			foreach (var s in r.PerState)
			{
				string state = $"{s.Bucket.LT}-{s.Bucket.ST}";
				string side = s.Direction == TradeDirection.Long ? "LONG" : "SHORT";

				Console.WriteLine(
					$"{state,-18} {side,-5} {s.Bars,5} " +
					$"{Signed(s.TotTradePct),10} {Signed(s.AvgTradePct),10} " +
					$"{Signed(s.TotStockPct),10} {Signed(s.AvgStockPct),10}");
			}
		}

		private static string Signed(double v)
		{
			double x = Math.Round(v, 2);
			return (x >= 0 ? "+" : "-") + Math.Abs(x).ToString("0.00");
		}
	}
}
