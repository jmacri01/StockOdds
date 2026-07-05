using System;
using System.Collections.Generic;
using System.Linq;

namespace StockOdds
{
	// Per-bucket win rate and average profit. Computed from each run's DIRECTIONAL,
	// per-unit-of-capital stock return (a long bucket wins when price rises, a short
	// bucket wins when price falls), so the figures describe the bucket itself and
	// are independent of the exposure the simulator happened to use.
	public class BucketStat
	{
		public (LongTermState LT, ShortTermState ST) Bucket { get; set; }
		public TradeDirection Direction { get; set; }

		public int Trades { get; set; }
		public int Wins { get; set; }
		public int Losses { get; set; }

		public double WinRate { get; set; }   // fraction of runs that were profitable [0..1]
		public double AvgWin { get; set; }     // mean winning return, %
		public double AvgLoss { get; set; }    // mean losing return magnitude, %
		public double AvgProfit { get; set; }  // mean return across ALL runs, %

		// Kelly fraction, per the supplied form:
		//   lossFrac = AvgLoss / (AvgLoss + AvgWin)
		//   Kelly    = (WinRate - lossFrac) / (1 - lossFrac)
		// (algebraically the classic f = p - (1-p)/b with b = AvgWin/AvgLoss)
		public double Kelly { get; set; }
	}

	public static class BucketStatsEngine
	{
		public static List<BucketStat> Compute(List<BankrollTrade> trades)
		{
			return trades
				.GroupBy(t => (t.Bucket.LT, t.Bucket.ST))
				.Select(g =>
				{
					var dir = g.First().Direction;

					// directional return per unit of capital (%), one per run
					var rets = g
						.Select(t => (dir == TradeDirection.Long ? 1.0 : -1.0) * t.StockPct)
						.ToList();

					var wins = rets.Where(x => x > 0).ToList();
					var losses = rets.Where(x => x < 0).Select(Math.Abs).ToList();
					int n = rets.Count;

					double winRate = n > 0 ? (double)wins.Count / n : 0.0;
					double avgWin = wins.Count > 0 ? wins.Average() : 0.0;
					double avgLoss = losses.Count > 0 ? losses.Average() : 0.0;

					// Kelly = (WinRate - lossFrac) / (1 - lossFrac), lossFrac = AvgLoss/(AvgLoss+AvgWin)
					double lossFrac = (avgWin + avgLoss) > 0 ? avgLoss / (avgWin + avgLoss) : 0.0;
					double kelly = (1.0 - lossFrac) != 0.0 ? (winRate - lossFrac) / (1.0 - lossFrac) : 0.0;

					return new BucketStat
					{
						Bucket = g.Key,
						Direction = dir,
						Trades = n,
						Wins = wins.Count,
						Losses = losses.Count,
						WinRate = winRate,
						AvgWin = avgWin,
						AvgLoss = avgLoss,
						AvgProfit = n > 0 ? rets.Average() : 0.0,
						Kelly = kelly,
					};
				})
				.OrderBy(b => b.Bucket.LT)
				.ThenBy(b => b.Bucket.ST)
				.ToList();
		}
	}
}
