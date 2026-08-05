using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StockOdds
{
	// Net dealer gamma for a SINGLE expiry, isolated from the rest of the surface.
	//
	// WHY THIS AND NOT TOTAL GEX: a 0-DTE spread is entered at the open and expires at that close. The gamma that
	// governs its session is the gamma of ITS OWN expiry, not the all-tenor total. And that number is look-ahead
	// free: the contracts expiring tomorrow already exist tonight, and their open interest is fixed at tonight's
	// settlement. So "net gamma of the expiry that lands on my trade date, measured at the prior close" is knowable
	// before entry -- unlike the vendor's total-GEX print, which is published after the close it describes.
	//
	// WHAT THIS HARNESS ANSWERS: would such a gate carry information, or is it always-on? A gate is only useful if
	// its sign actually varies. Per-expiry net gamma is spot-dependent (gamma peaks at the strike), so the question
	// is where that expiry's zero-gamma level sits relative to spot, and how far spot must travel to flip it. If the
	// flip level is 15% below spot, the gate is on essentially every day and gates nothing.
	//
	// Reported per expiry so the near tenors can be compared against the total. Sign convention follows
	// CboeGexSnapshot.CallsPositive (dealers long calls / short puts).
	public static class NearExpiryGex
	{
		public static bool CallsPositive = true;
		public static int  MaxExpiriesShown = 10;
		public static double[] SpotShocks = { -0.10, -0.05, -0.02, -0.01, 0.0, 0.01, 0.02, 0.05 };

		private sealed record Leg(double Strike, bool IsCall, double Gamma, double Oi, DateTime Expiry);

		public static async Task Run(string cboeSymbol = "_SPX", string label = "SPX")
		{
			string url = $"https://cdn.cboe.com/api/global/delayed_quotes/options/{cboeSymbol}.json";
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
			http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
			string body;
			try { body = await http.GetStringAsync(url); }
			catch (Exception ex) { Console.WriteLine($"CBOE fetch failed: {ex.Message}"); return; }

			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			string stamp = root.GetProperty("timestamp").GetString() ?? "";
			var data = root.GetProperty("data");
			double spot = data.TryGetProperty("close", out var c) && c.GetDouble() > 0 ? c.GetDouble()
				: data.TryGetProperty("current_price", out var cp) ? cp.GetDouble() : 0;
			if (spot <= 0) { Console.WriteLine("no spot"); return; }

			var legs = new List<Leg>();
			foreach (var o in data.GetProperty("options").EnumerateArray())
			{
				string name = o.GetProperty("option").GetString() ?? "";
				var m = Regex.Match(name, @"^[A-Z]+(?<ymd>\d{6})(?<cp>[CP])(?<strike>\d{8})$");
				if (!m.Success) continue;
				string ymd = m.Groups["ymd"].Value;
				if (!DateTime.TryParseExact(ymd, "yyMMdd", CultureInfo.InvariantCulture,
					DateTimeStyles.None, out var exp)) continue;
				double gamma = o.TryGetProperty("gamma", out var g) ? g.GetDouble() : 0;
				double oi = o.TryGetProperty("open_interest", out var oiEl) ? oiEl.GetDouble() : 0;
				if (oi <= 0 || gamma == 0) continue;
				legs.Add(new Leg(double.Parse(m.Groups["strike"].Value, CultureInfo.InvariantCulture) / 1000.0,
					m.Groups["cp"].Value == "C", gamma, oi, exp));
			}
			if (legs.Count == 0) { Console.WriteLine("no legs parsed"); return; }

			double sign = CallsPositive ? 1.0 : -1.0;
			// dollar gamma per 1% move, at the OBSERVED spot
			double DollarGamma(Leg l, double s) => l.Gamma * l.Oi * 100.0 * s * s * 0.01;
			double NetAt(IEnumerable<Leg> ls, double s) =>
				ls.Sum(l => sign * (l.IsCall ? 1 : -1) * DollarGamma(l, s));

			Console.WriteLine($"\n===== {label}: NET GAMMA BY EXPIRY (single snapshot) =====");
			Console.WriteLine($"snapshot {stamp} | spot {spot:0.00} | {legs.Count:N0} legs with OI+gamma | " +
				$"{legs.Select(l => l.Expiry).Distinct().Count()} expiries");
			Console.WriteLine("CAVEAT: gamma here is the CBOE-published per-contract gamma at the CURRENT spot. The");
			Console.WriteLine("spot-shock columns rescale it by s^2 only -- they do NOT re-solve gamma at the shocked");
			Console.WriteLine("spot, so they understate how much a move re-centres gamma onto other strikes. Treat the");
			Console.WriteLine("shock row as a lower bound on sign instability, not a forecast.");

			double totalNet = NetAt(legs, spot);
			var byExp = legs.GroupBy(l => l.Expiry).OrderBy(gp => gp.Key).ToList();

			Console.WriteLine($"\n{"expiry",12} {"cal days",9} {"legs",6} {"net $gamma",13} {"% of total",11} {"|g| share",10} {"flipLvl",9} {"flip vs spot",13}");
			DateTime today = legs.Min(l => l.Expiry);   // no Date.Now available; nearest expiry anchors "now"
			double totalAbs = byExp.Sum(gp => Math.Abs(NetAt(gp, spot)));
			foreach (var gp in byExp.Take(MaxExpiriesShown))
			{
				double net = NetAt(gp, spot);
				// zero-gamma level for THIS expiry: cumulate net gamma from the lowest strike upward
				double cum = 0, flip = double.NaN;
				foreach (var kv in gp.GroupBy(l => l.Strike).OrderBy(k => k.Key))
				{
					double prev = cum;
					cum += NetAt(kv, spot);
					if (double.IsNaN(flip) && prev < 0 && cum >= 0) flip = kv.Key;
				}
				double days = (gp.Key - today).TotalDays;
				string flipTxt = double.IsNaN(flip) ? "none" : flip.ToString("0");
				string vsSpot = double.IsNaN(flip) ? "n/a" : $"{100 * (flip - spot) / spot:+0.0;-0.0}%";
				Console.WriteLine($"{gp.Key,12:yyyy-MM-dd} {days,9:0} {gp.Count(),6} {net,13:0.000e+00} " +
					$"{(totalNet != 0 ? 100 * net / totalNet : 0),11:0.0} " +
					$"{(totalAbs > 0 ? 100 * Math.Abs(net) / totalAbs : 0),10:0.0} {flipTxt,9} {vsSpot,13}");
			}
			Console.WriteLine($"{"TOTAL",12} {"",9} {legs.Count,6} {totalNet,13:0.000e+00}");

			// Would a "near-expiry net gamma > 0" gate ever turn OFF? Shock spot and watch the sign.
			var near = byExp.First();
			Console.WriteLine($"\n----- sign stability of the NEAREST expiry ({near.Key:yyyy-MM-dd}) under spot shocks -----");
			Console.WriteLine($"{"shock",8} {"spot",10} {"net $gamma",13} {"sign",6}");
			foreach (double sh in SpotShocks)
			{
				double s = spot * (1 + sh);
				double net = NetAt(near, s);
				Console.WriteLine($"{100 * sh,7:+0;-0}% {s,10:0.00} {net,13:0.000e+00} {(net >= 0 ? "POS" : "NEG"),6}");
			}
			Console.WriteLine("\nIf the sign is POS across every shock, a `near-expiry gamma > 0` gate is always-on and");
			Console.WriteLine("gates nothing. Sign variation is a NECESSARY condition for the gate to carry information,");
			Console.WriteLine("not a sufficient one -- and one snapshot cannot establish the frequency either way.");
		}
	}
}
