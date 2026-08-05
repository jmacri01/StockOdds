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
			=> await GetBarsAsync(symbol, interval, 5);

		// yearsBack is parameterised because the 5-year window was a SELF-IMPOSED default, not a Yahoo limit:
		// the daily endpoint serves 20+ years (SPY returns 5,430 bars back to 2005-01-03). That matters for any
		// tail test -- a 5-year window starting 2021 contains no crash at all, while 2005+ contains 2008,
		// March 2020 and 18 days worse than -5%.
		public static async Task<List<OhlcBar>> GetBarsAsync(string symbol, string interval, int yearsBack)
		{
			var now = DateTimeOffset.UtcNow;
			long period2 = now.ToUnixTimeSeconds();
			long period1 = now.AddYears(-Math.Abs(yearsBack)).ToUnixTimeSeconds();

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

				bars.Add(new OhlcBar
				{
					Date = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime,
					Open = o.Value,
					High = h.Value,
					Low = l.Value,
					Close = c.Value
				});
			}

			return bars;
		}
	}
}
