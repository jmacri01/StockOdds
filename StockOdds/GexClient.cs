using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace StockOdds
{
	public sealed record GexDay(DateTime Date, double Price, double Dix, double Gex);

	// SqueezeMetrics' free daily DIX/GEX series for the S&P complex. No auth, ~220 KB, 2011-05-02 to current.
	//
	// WHAT THIS IS AND IS NOT: `gex` is THEIR dealer-gamma model output, not raw data -- their sign conventions
	// and dealer-positioning assumptions are baked in, and it is one market-wide DAILY scalar. No per-strike
	// structure, no per-name, no intraday. For per-strike gamma see CboeGexSnapshot, which can be computed from
	// raw open interest but only forward from the day you start collecting.
	// Check their terms before any use beyond research.
	public static class GexClient
	{
		public const string Url = "https://squeezemetrics.com/monitor/static/DIX.csv";
		public static string CachePath => Path.Combine(Path.GetFullPath(Universe.DataDir), "dix_gex.csv");

		public static async Task<List<GexDay>> GetAsync(bool refresh = false)
		{
			Directory.CreateDirectory(Path.GetFullPath(Universe.DataDir));
			if (refresh || !File.Exists(CachePath) || new FileInfo(CachePath).Length < 1000)
			{
				using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
				http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
				await File.WriteAllTextAsync(CachePath, await http.GetStringAsync(Url));
			}

			var rows = new List<GexDay>();
			foreach (var ln in (await File.ReadAllLinesAsync(CachePath)).Skip(1))
			{
				var f = ln.Split(',');
				if (f.Length < 4) continue;
				if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
				if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double px)) continue;
				if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double dix)) continue;
				if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double gex)) continue;
				rows.Add(new GexDay(d, px, dix, gex));
			}
			return rows.OrderBy(r => r.Date).ToList();
		}

		public static async Task<Dictionary<DateTime, GexDay>> ByDateAsync(bool refresh = false) =>
			(await GetAsync(refresh)).ToDictionary(r => r.Date.Date, r => r);
	}
}
