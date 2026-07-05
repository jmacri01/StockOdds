using System;
using System.Collections.Generic;

namespace StockOdds
{
	public static class StateChangePrinter
	{
		public static void Print(List<StateChangeMetrics> buckets)
		{
			Console.WriteLine(
				$"{"State",-18} {"N",3} {"Avg%",8} {"Med%",8} {"P10%",8} {"P25%",8} {"P75%",8} {"P90%",8} " +
				$"{"Min%",8} {"Max%",8} {"Std%",7} {"AvgDur",7} {"Ret/Bar",8}");

			foreach (var b in buckets)
			{
				string state = $"{b.Bucket.LT}-{b.Bucket.ST}";

				Console.WriteLine(
					$"{state,-18} {b.Count,3} " +
					$"{Signed(b.AvgPctChange),8} " +
					$"{Signed(b.MedianPctChange),8} " +
					$"{Signed(b.P10PctChange),8} " +
					$"{Signed(b.P25PctChange),8} " +
					$"{Signed(b.P75PctChange),8} " +
					$"{Signed(b.P90PctChange),8} " +
					$"{Signed(b.MinPctChange),8} " +
					$"{Signed(b.MaxPctChange),8} " +
					$"{b.StdDevPctChange,7:0.00} " +
					$"{b.AvgDuration,7:0.0} " +
					$"{Signed(b.ReturnPerBar),8}");
			}
		}

		// explicit leading sign, snapping values that round to zero to "+0.00"
		private static string Signed(double v)
		{
			double r = Math.Round(v, 2);
			return (r >= 0 ? "+" : "-") + Math.Abs(r).ToString("0.00");
		}
	}
}
