using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StockOdds
{
	// Self-computed dealer gamma from CBOE's free delayed-quote chain (no auth, ~14 MB for SPX, 32k contracts
	// carrying open_interest AND gamma). Unlike the SqueezeMetrics scalar this gives the full PER-STRIKE profile,
	// so the zero-gamma flip level and the call/put split are available -- but it is a LIVE SNAPSHOT, so a history
	// only accumulates from the day collection starts. Run this once per session close to build the series.
	//
	// Convention (stated because every GEX publisher picks a different one and the sign is not observable):
	//   dollar gamma per contract = gamma * OI * 100 * spot^2 * 0.01     (dollar delta change per 1% spot move)
	//   dealers assumed SHORT calls / LONG puts, i.e. calls contribute +, puts contribute -.
	// Flip the sign convention with CallsPositive if you prefer the other house style.
	public static class CboeGexSnapshot
	{
		public static bool CallsPositive = true;
		public static string Root => Path.GetFullPath(Universe.DataDir);
		public static string SeriesPath => Path.Combine(Root, "gex_cboe.csv");

		public static async Task Run(string cboeSymbol = "_SPX", string label = "SPX")
		{
			Directory.CreateDirectory(Path.Combine(Root, "gex_chains"));
			string url = $"https://cdn.cboe.com/api/global/delayed_quotes/options/{cboeSymbol}.json";

			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
			http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
			string body = await http.GetStringAsync(url);

			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			string stamp = root.GetProperty("timestamp").GetString() ?? "";
			var data = root.GetProperty("data");
			double spot = data.TryGetProperty("close", out var c) && c.GetDouble() > 0 ? c.GetDouble()
				: data.TryGetProperty("current_price", out var cp) ? cp.GetDouble() : 0;

			var perStrike = new SortedDictionary<double, (double Call, double Put)>();
			double totCallOi = 0, totPutOi = 0;
			int parsed = 0;

			foreach (var o in data.GetProperty("options").EnumerateArray())
			{
				string name = o.GetProperty("option").GetString() ?? "";
				// e.g. SPX260821C00200000 / SPXW260805P06000000
				var m = Regex.Match(name, @"^[A-Z]+(?<ymd>\d{6})(?<cp>[CP])(?<strike>\d{8})$");
				if (!m.Success) continue;
				double strike = double.Parse(m.Groups["strike"].Value, CultureInfo.InvariantCulture) / 1000.0;
				bool isCall = m.Groups["cp"].Value == "C";
				double gamma = o.TryGetProperty("gamma", out var g) ? g.GetDouble() : 0;
				double oi = o.TryGetProperty("open_interest", out var oiEl) ? oiEl.GetDouble() : 0;
				if (oi <= 0 || gamma == 0) continue;

				double dollarGamma = gamma * oi * 100.0 * spot * spot * 0.01;
				if (!perStrike.ContainsKey(strike)) perStrike[strike] = (0, 0);
				var cur = perStrike[strike];
				perStrike[strike] = isCall ? (cur.Call + dollarGamma, cur.Put) : (cur.Call, cur.Put + dollarGamma);
				if (isCall) totCallOi += oi; else totPutOi += oi;
				parsed++;
			}

			double sign = CallsPositive ? 1.0 : -1.0;
			double net = perStrike.Sum(kv => sign * (kv.Value.Call - kv.Value.Put));
			double callGex = perStrike.Sum(kv => kv.Value.Call), putGex = perStrike.Sum(kv => kv.Value.Put);

			// zero-gamma "flip" level: cumulative net gamma from the low strike up, first crossing of zero
			double cum = 0, flip = double.NaN;
			foreach (var kv in perStrike)
			{
				double prev = cum;
				cum += sign * (kv.Value.Call - kv.Value.Put);
				if (double.IsNaN(flip) && prev < 0 && cum >= 0) flip = kv.Key;
			}

			Console.WriteLine($"\n===== CBOE SELF-COMPUTED GAMMA: {label} =====");
			Console.WriteLine($"snapshot {stamp} | spot {spot:0.00} | {parsed:N0} contracts with OI+gamma | strikes {perStrike.Count}");
			Console.WriteLine($"call OI {totCallOi:N0} | put OI {totPutOi:N0} | put/call OI {(totCallOi > 0 ? totPutOi / totCallOi : 0):0.00}");
			Console.WriteLine($"call $gamma {callGex:0.000e+00} | put $gamma {putGex:0.000e+00} | NET {net:0.000e+00} per 1% move");
			Console.WriteLine($"zero-gamma flip level: {(double.IsNaN(flip) ? "not crossed in range" : flip.ToString("0"))}");

			// top strikes by absolute net gamma -- where the pinning pressure sits
			Console.WriteLine("\nlargest net-gamma strikes:");
			foreach (var kv in perStrike.OrderByDescending(kv => Math.Abs(sign * (kv.Value.Call - kv.Value.Put))).Take(8))
				Console.WriteLine($"   {kv.Key,8:0} {sign * (kv.Value.Call - kv.Value.Put),12:0.000e+00}" +
					$"   ({(kv.Key > spot ? "above" : "below")} spot)");

			// append one row to the accumulating series
			bool need = !File.Exists(SeriesPath);
			using (var w = new StreamWriter(SeriesPath, append: true))
			{
				if (need) w.WriteLine("snapshot,symbol,spot,net_gamma,call_gamma,put_gamma,flip,call_oi,put_oi,contracts");
				w.WriteLine($"{stamp},{label},{spot.ToString(CultureInfo.InvariantCulture)}," +
					$"{net.ToString("R", CultureInfo.InvariantCulture)},{callGex.ToString("R", CultureInfo.InvariantCulture)}," +
					$"{putGex.ToString("R", CultureInfo.InvariantCulture)},{flip.ToString(CultureInfo.InvariantCulture)}," +
					$"{totCallOi},{totPutOi},{parsed}");
			}

			// and the full per-strike profile for this date, so profiles can be revisited later
			string prof = Path.Combine(Root, "gex_chains", $"{label}_{DateTime.UtcNow:yyyyMMdd}.csv");
			using (var w = new StreamWriter(prof))
			{
				w.WriteLine("strike,call_gamma,put_gamma,net_gamma");
				foreach (var kv in perStrike)
					w.WriteLine($"{kv.Key.ToString(CultureInfo.InvariantCulture)}," +
						$"{kv.Value.Call.ToString("R", CultureInfo.InvariantCulture)}," +
						$"{kv.Value.Put.ToString("R", CultureInfo.InvariantCulture)}," +
						$"{(sign * (kv.Value.Call - kv.Value.Put)).ToString("R", CultureInfo.InvariantCulture)}");
			}
			Console.WriteLine($"\nappended to {SeriesPath}");
			Console.WriteLine($"per-strike profile -> {prof}");
			Console.WriteLine("Run once per session close to accumulate a history; nothing backfills a snapshot feed.");
		}
	}
}
