using System;
using System.Collections.Generic;
using System.Linq;

namespace StockOdds
{
	public static class Volatility
	{
		// Annualized historical volatility, in percent — the usual "HV" figure quoted by
		// brokers/charts. Standard deviation (sample) of daily log returns, scaled by
		// sqrt(periodsPerYear) and ×100. For daily bars, periodsPerYear = 252, which puts
		// a jumpy name like ASST near ~100 and a steadier one like NOK near ~50.
		public static double AnnualizedHistoricalPct(List<OhlcBar> bars, double periodsPerYear = 252.0)
		{
			if (bars == null || bars.Count < 3)
				return 0.0;

			var logReturns = new List<double>(bars.Count - 1);
			for (int i = 1; i < bars.Count; i++)
			{
				double prev = bars[i - 1].Close;
				double cur = bars[i].Close;
				if (prev > 0 && cur > 0)
					logReturns.Add(Math.Log(cur / prev));
			}

			if (logReturns.Count < 2)
				return 0.0;

			double mean = logReturns.Average();
			double variance = logReturns.Sum(x => (x - mean) * (x - mean)) / (logReturns.Count - 1);
			double sd = Math.Sqrt(variance);

			return sd * Math.Sqrt(periodsPerYear) * 100.0;
		}
	}
}
