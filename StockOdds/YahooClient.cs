using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;

namespace StockOdds
{
	public static class YahooClient
	{
		public static async Task<List<OhlcBar>> GetBarsAsync(string symbol, string interval)
		{
			// Rolling window: the last 5 years up to right now.
			var now = DateTimeOffset.UtcNow;
			long period2 = now.ToUnixTimeSeconds();
			long period1 = now.AddYears(-5).ToUnixTimeSeconds();

			var url =
				$"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
				"?events=capitalGain%7Cdiv%7Csplit" +
				"&formatted=true&includeAdjustedClose=true" +
				$"&interval={interval}" +
				$"&period1={period1}" +
				$"&period2={period2}" +
				$"&symbol={Uri.EscapeDataString(symbol)}" +
				"&userYfid=true&lang=en-US&region=US";

			var handler = new HttpClientHandler
			{
				UseCookies = true,
				CookieContainer = new CookieContainer()
			};

			using var client = new HttpClient(handler);

			var request = new HttpRequestMessage(HttpMethod.Get, url);

			// minimal headers (Yahoo is surprisingly tolerant)
			request.Headers.TryAddWithoutValidation("accept", "*/*");
			request.Headers.TryAddWithoutValidation("user-agent",
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

			var response = await client.SendAsync(request);
			response.EnsureSuccessStatusCode();

			var json = await response.Content.ReadAsStringAsync();

			using var doc = JsonDocument.Parse(json);

			var result = doc.RootElement
				.GetProperty("chart")
				.GetProperty("result")[0];

			var timestamps = result.GetProperty("timestamp");

			var quote = result
				.GetProperty("indicators")
				.GetProperty("quote")[0];

			var opens = quote.GetProperty("open");
			var highs = quote.GetProperty("high");
			var lows = quote.GetProperty("low");
			var closes = quote.GetProperty("close");
			var hasVol = quote.TryGetProperty("volume", out var volumes);

			var bars = new List<OhlcBar>();

			for (int i = 0; i < timestamps.GetArrayLength(); i++)
			{
				double? o = opens[i].ValueKind == JsonValueKind.Null ? null : opens[i].GetDouble();
				double? h = highs[i].ValueKind == JsonValueKind.Null ? null : highs[i].GetDouble();
				double? l = lows[i].ValueKind == JsonValueKind.Null ? null : lows[i].GetDouble();
				double? c = closes[i].ValueKind == JsonValueKind.Null ? null : closes[i].GetDouble();

				if (o == null || h == null || l == null || c == null)
					continue;

				long ts = timestamps[i].GetInt64();

				double vol = 0.0;
				if (hasVol && volumes[i].ValueKind != JsonValueKind.Null)
					vol = volumes[i].GetDouble();

				bars.Add(new OhlcBar
				{
					Date = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime,
					Open = o.Value,
					High = h.Value,
					Low = l.Value,
					Close = c.Value,
					Volume = vol
				});
			}

			return bars;
		}
	}
}
