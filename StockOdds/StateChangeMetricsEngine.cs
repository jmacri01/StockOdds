using System;
using System.Collections.Generic;
using System.Linq;

namespace StockOdds
{
	public class StateChangeMetrics
	{
		public (LongTermState LT, ShortTermState ST) Bucket { get; set; }

		public int Count { get; set; }

		// realized % price change per state episode
		public double AvgPctChange { get; set; }
		public double MedianPctChange { get; set; }
		public double P10PctChange { get; set; }
		public double P25PctChange { get; set; }
		public double P75PctChange { get; set; }
		public double P90PctChange { get; set; }
		public double MinPctChange { get; set; }   // biggest drop (most negative)
		public double MaxPctChange { get; set; }   // biggest gain (most positive)
		public double StdDevPctChange { get; set; } // volatility of episode returns

		// duration / efficiency
		public double AvgDuration { get; set; }     // avg bars per episode
		public double ReturnPerBar { get; set; }    // total % return / total bars
	}

	public static class StateChangeMetricsEngine
	{
		public static List<StateChangeMetrics> Compute(List<StateEpisode> episodes)
		{
			return episodes
				.Where(e => e.IsClosed)
				.GroupBy(e => (e.LT, e.ST))
				.Select(g =>
				{
					var changes = g.Select(e => e.PctChange).OrderBy(v => v).ToList();
					double avg = changes.Average();

					double totalReturn = changes.Sum();
					int totalBars = g.Sum(e => e.Duration);

					return new StateChangeMetrics
					{
						Bucket = g.Key,
						Count = changes.Count,
						AvgPctChange = avg,
						MedianPctChange = Percentile(changes, 0.50),
						P10PctChange = Percentile(changes, 0.10),
						P25PctChange = Percentile(changes, 0.25),
						P75PctChange = Percentile(changes, 0.75),
						P90PctChange = Percentile(changes, 0.90),
						MinPctChange = changes[0],
						MaxPctChange = changes[^1],
						StdDevPctChange = StdDev(changes, avg),
						AvgDuration = g.Average(e => e.Duration),
						ReturnPerBar = totalBars > 0 ? totalReturn / totalBars : 0.0,
					};
				})
				.OrderBy(b => b.Bucket.LT)
				.ThenBy(b => b.Bucket.ST)
				.ToList();
		}

		// population standard deviation (0 when a bucket has a single episode)
		private static double StdDev(List<double> values, double mean)
		{
			if (values.Count < 2)
				return 0.0;

			double sumSq = values.Sum(v => (v - mean) * (v - mean));
			return Math.Sqrt(sumSq / values.Count);
		}

		// linear-interpolation percentile (p in [0,1]) over an ASCENDING-sorted list
		private static double Percentile(List<double> sorted, double p)
		{
			int n = sorted.Count;
			if (n == 0) return 0.0;
			if (n == 1) return sorted[0];

			double rank = p * (n - 1);
			int lo = (int)Math.Floor(rank);
			int hi = (int)Math.Ceiling(rank);

			if (lo == hi)
				return sorted[lo];

			double frac = rank - lo;
			return sorted[lo] + frac * (sorted[hi] - sorted[lo]);
		}
	}
}
