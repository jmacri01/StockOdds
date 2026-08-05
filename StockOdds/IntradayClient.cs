using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockOdds
{
	// Intraday bars from the Yahoo v8 chart endpoint, which works here even though the quote/screener endpoints
	// need a cookie+crumb handshake that does not (see broad-universe notes).
	//
	// HARD LIMIT, verified against the API rather than assumed: 5m data is only served for the last 60 days.
	// Asking for an explicit older window returns
	//   "5m data not available for startTime=... The requested range must be within the last 60 days."
	// so the series CANNOT be stitched backwards. Practical ceilings for ^GSPC:
	//   5m  -> 60d   ~4,680 bars     15m -> 60d  ~1,560     30m -> 60d ~780
	//   1h  -> 730d  ~5,100 bars
	// Anything longer at 5m needs a keyed provider (Alpha Vantage monthly slices, Polygon, Databento, ...).
	public static class IntradayClient
	{
		public static string Dir = Path.Combine(Path.GetFullPath(Universe.DataDir), "intraday");

		private static string PathFor(string sym, string interval) =>
			Path.Combine(Dir, $"{sym.Replace("^", "_")}_{interval}.csv");

		public static async Task<List<OhlcBar>> GetAsync(string symbol, string interval, string range, bool refresh = false)
		{
			Directory.CreateDirectory(Dir);
			string path = PathFor(symbol, interval);
			if (!refresh && File.Exists(path)) return Load(path);

			string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
				$"?interval={interval}&range={range}&includePrePost=false";

			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
			http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
			http.DefaultRequestHeaders.TryAddWithoutValidation("accept", "*/*");

			using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
			var chart = doc.RootElement.GetProperty("chart");
			if (chart.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
				throw new Exception($"Yahoo: {err}");

			var res = chart.GetProperty("result")[0];
			var ts = res.GetProperty("timestamp");
			var q = res.GetProperty("indicators").GetProperty("quote")[0];
			var o = q.GetProperty("open"); var h = q.GetProperty("high");
			var l = q.GetProperty("low");  var c = q.GetProperty("close");

			// exchange timezone offset, so bar stamps land on the real session clock
			int gmt = res.GetProperty("meta").TryGetProperty("gmtoffset", out var g) ? g.GetInt32() : 0;

			var bars = new List<OhlcBar>();
			for (int i = 0; i < ts.GetArrayLength(); i++)
			{
				if (o[i].ValueKind == JsonValueKind.Null || h[i].ValueKind == JsonValueKind.Null ||
					l[i].ValueKind == JsonValueKind.Null || c[i].ValueKind == JsonValueKind.Null) continue;
				bars.Add(new OhlcBar
				{
					Date  = DateTimeOffset.FromUnixTimeSeconds(ts[i].GetInt64() + gmt).UtcDateTime,
					Open  = o[i].GetDouble(), High = h[i].GetDouble(),
					Low   = l[i].GetDouble(), Close = c[i].GetDouble(),
				});
			}

			using (var w = new StreamWriter(path))
			{
				w.WriteLine("datetime,open,high,low,close");
				foreach (var b in bars)
					w.WriteLine($"{b.Date:yyyy-MM-dd HH:mm},{b.Open.ToString(CultureInfo.InvariantCulture)}," +
						$"{b.High.ToString(CultureInfo.InvariantCulture)},{b.Low.ToString(CultureInfo.InvariantCulture)}," +
						$"{b.Close.ToString(CultureInfo.InvariantCulture)}");
			}
			return bars;
		}

		private static List<OhlcBar> Load(string path)
		{
			var bars = new List<OhlcBar>();
			foreach (var ln in File.ReadLines(path).Skip(1))
			{
				var f = ln.Split(',');
				if (f.Length < 5) continue;
				bars.Add(new OhlcBar
				{
					Date  = DateTime.ParseExact(f[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
					Open  = double.Parse(f[1], CultureInfo.InvariantCulture),
					High  = double.Parse(f[2], CultureInfo.InvariantCulture),
					Low   = double.Parse(f[3], CultureInfo.InvariantCulture),
					Close = double.Parse(f[4], CultureInfo.InvariantCulture),
				});
			}
			return bars;
		}
	}
}
