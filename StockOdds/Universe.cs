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
	public sealed class UniverseName
	{
		public string Symbol = "";
		public int    Cik;
		public double Shares;        // common shares outstanding (SEC dei, most recent quarter available)
		public double MarketCap;     // Shares * last close -- filled in once bars are loaded
	}

	// Reconstructs the deployment universe the README's broad tables are scored on: US-listed COMMON STOCK
	// above a market-cap floor. Assembled from two free, no-auth sources because Yahoo's quote/screener
	// endpoints require a cookie+crumb handshake that is not reachable from here:
	//
	//   1. NASDAQ Trader symbol directory  -> every US-listed symbol, with ETF and test-issue flags
	//   2. SEC company_tickers.json        -> ticker -> CIK
	//   3. SEC XBRL frames (dei:EntityCommonStockSharesOutstanding) -> shares outstanding per CIK, one
	//      request per quarter for the WHOLE market (most recent quarter with data wins)
	//
	// Market cap is then shares * last close from the bars we already fetch. Sanity check on the
	// reconstruction: 4,620 listed-common filers with shares data, of which ~2,400 clear $500M --
	// against the README's "2,429 eligible tickers". Close enough to treat as the same universe.
	//
	// Everything is cached under data/ (gitignored) so this runs once.
	public static class Universe
	{
		public static string DataDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data");
		public static double MinMarketCap = 500e6;

		// quarters tried oldest -> newest, so the newest available value wins
		public static string[] ShareFrames = { "CY2025Q1I", "CY2025Q2I", "CY2025Q3I", "CY2025Q4I" };
		private const string Ua = "StockOdds research jmacri@protossecurity.com";

		private static string Cache(string name) => Path.Combine(Path.GetFullPath(DataDir), name);

		private static async Task<string> GetCachedAsync(string url, string file, string userAgent)
		{
			Directory.CreateDirectory(Path.GetFullPath(DataDir));
			string path = Cache(file);
			if (File.Exists(path) && new FileInfo(path).Length > 1000) return await File.ReadAllTextAsync(path);

			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
			http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", userAgent);
			string body = await http.GetStringAsync(url);
			await File.WriteAllTextAsync(path, body);
			return body;
		}

		// symbols that are listed COMMON stock: no ETFs, no test issues, plain 1-5 letter tickers
		// (filters out units/warrants/preferreds, which carry suffixes)
		private static async Task<HashSet<string>> ListedCommonAsync()
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			string nasdaq = await GetCachedAsync("https://www.nasdaqtrader.com/dynamic/SymDir/nasdaqlisted.txt", "nasdaqlisted.txt", "Mozilla/5.0");
			string other  = await GetCachedAsync("https://www.nasdaqtrader.com/dynamic/SymDir/otherlisted.txt",  "otherlisted.txt",  "Mozilla/5.0");

			// nasdaqlisted: Symbol|Security Name|Market Category|Test Issue|Financial Status|Round Lot|ETF|NextShares
			Parse(nasdaq, symIdx: 0, etfIdx: 6, testIdx: 3);
			// otherlisted: ACT Symbol|Security Name|Exchange|CQS Symbol|ETF|Round Lot|Test Issue|NASDAQ Symbol
			Parse(other, symIdx: 0, etfIdx: 4, testIdx: 6);

			void Parse(string text, int symIdx, int etfIdx, int testIdx)
			{
				var lines = text.Replace("\r", "").Split('\n');
				for (int i = 1; i < lines.Length; i++)
				{
					var p = lines[i].Split('|');
					if (p.Length < 8 || p[0].StartsWith("File Creation")) continue;
					if (p[etfIdx].Trim() == "Y" || p[testIdx].Trim() == "Y") continue;
					string s = p[symIdx].Trim();
					if (s.Length is >= 1 and <= 5 && s.All(char.IsAsciiLetterUpper)) set.Add(s);
				}
			}
			return set;
		}

		public static async Task<List<UniverseName>> BuildAsync()
		{
			string csv = Cache("universe.csv");
			if (File.Exists(csv))
			{
				var cached = new List<UniverseName>();
				foreach (var ln in (await File.ReadAllLinesAsync(csv)).Skip(1))
				{
					var p = ln.Split(',');
					if (p.Length < 3) continue;
					cached.Add(new UniverseName { Symbol = p[0], Cik = int.Parse(p[1]), Shares = double.Parse(p[2], CultureInfo.InvariantCulture) });
				}
				Console.WriteLine($"Universe: {cached.Count} candidates (cached)");
				return cached;
			}

			var listed = await ListedCommonAsync();

			// ticker -> CIK
			var tickerToCik = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			using (var doc = JsonDocument.Parse(await GetCachedAsync("https://www.sec.gov/files/company_tickers.json", "company_tickers.json", Ua)))
				foreach (var el in doc.RootElement.EnumerateObject())
					tickerToCik[el.Value.GetProperty("ticker").GetString() ?? ""] = el.Value.GetProperty("cik_str").GetInt32();

			// CIK -> shares outstanding, newest quarter wins
			var shares = new Dictionary<int, double>();
			foreach (var q in ShareFrames)
			{
				string body;
				try
				{
					body = await GetCachedAsync(
						$"https://data.sec.gov/api/xbrl/frames/dei/EntityCommonStockSharesOutstanding/shares/{q}.json",
						$"frames_{q}.json", Ua);
				}
				catch (Exception ex) { Console.WriteLine($"  frames {q}: {ex.Message}"); continue; }

				using var doc = JsonDocument.Parse(body);
				if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
				foreach (var row in data.EnumerateArray())
				{
					int cik = row.GetProperty("cik").GetInt32();
					double v = row.GetProperty("val").GetDouble();
					if (v > 0) shares[cik] = v;
				}
			}

			var outp = new List<UniverseName>();
			foreach (var s in listed.OrderBy(x => x, StringComparer.Ordinal))
				if (tickerToCik.TryGetValue(s, out int cik) && shares.TryGetValue(cik, out double sh))
					outp.Add(new UniverseName { Symbol = s, Cik = cik, Shares = sh });

			Directory.CreateDirectory(Path.GetFullPath(DataDir));
			await File.WriteAllLinesAsync(csv, new[] { "symbol,cik,shares" }
				.Concat(outp.Select(u => $"{u.Symbol},{u.Cik},{u.Shares.ToString(CultureInfo.InvariantCulture)}")));

			Console.WriteLine($"Universe: {listed.Count} listed common | {shares.Count} CIKs with shares | {outp.Count} candidates");
			return outp;
		}
	}
}
