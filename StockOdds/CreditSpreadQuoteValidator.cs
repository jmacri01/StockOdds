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
	// Validates the 1-day put-credit-spread backtest against REAL quotes.
	//
	// The tail test showed the whole result turns on pricing accuracy: a 20% shortfall in credit takes Sharpe
	// 2.58 -> 0.88 and a 40% shortfall gives ruin. A flat-IV Gaussian with no bid/ask cannot resolve that. This
	// pulls CBOE's free delayed chain (which carries bid, ask, iv, delta and OI per contract), prices the EXACT
	// structure the backtest trades, and records three things per run:
	//
	//   1. MARKET CREDIT vs MODEL CREDIT -- the ratio that the haircut sweep was guessing at. > 1 means the
	//      backtest was CONSERVATIVE (real skew pays more than flat-IV Gaussian); < 1 means the edge is smaller
	//      than modelled and the haircut stress was the realistic case.
	//   2. REALISED VRP -- market IV at the short strike divided by trailing HV(60). The backtest assumes 1.10.
	//      This measures it instead of assuming it, which is the single most load-bearing input.
	//   3. Both a MID-based and a CROSS-THE-SPREAD credit, so execution cost is separated from mispricing.
	//
	// Accumulates one row per run into data/credit_spread_quotes.csv. Run near the close so the time-to-expiry
	// matches the backtest's close-to-close convention.
	public static class CreditSpreadQuoteValidator
	{
		public static double AssumedVrp = 1.10;
		public static int    HvWindow = 60;
		public static double ShortDelta = 0.50;
		public static string SeriesPath => Path.Combine(Path.GetFullPath(Universe.DataDir), "credit_spread_quotes.csv");

		private sealed record Q(double Strike, double Bid, double Ask, double Iv, double Delta, double Oi, DateTime Exp);

		public static async Task Run(string underlying = "SPY", string cboeSymbol = "SPY")
		{
			// ---- today's engine target from daily bars ----
			var bars = await YahooClient.GetBarsAsync(underlying, "1d", 5);
			var eng = BankrollSimulator.Run(bars, 10_000.0);
			double target = eng.OpenStake;             // the exposure we'd carry into the next bar
			double spotBars = bars[^1].Close;

			// trailing HV(60) as of the last bar
			var lr = new List<double>();
			for (int j = Math.Max(1, bars.Count - HvWindow); j < bars.Count; j++)
				if (bars[j - 1].Close > 0) lr.Add(Math.Log(bars[j].Close / bars[j - 1].Close));
			double m0 = lr.Average();
			double hv = Math.Sqrt(lr.Sum(x => (x - m0) * (x - m0)) / (lr.Count - 1)) * Math.Sqrt(252.0);

			// ---- CBOE chain ----
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
			http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
			using var doc = JsonDocument.Parse(await http.GetStringAsync(
				$"https://cdn.cboe.com/api/global/delayed_quotes/options/{cboeSymbol}.json"));
			var data = doc.RootElement.GetProperty("data");
			string stamp = doc.RootElement.GetProperty("timestamp").GetString() ?? "";
			DateTime snap = DateTime.TryParse(stamp, out var sp) ? sp : DateTime.UtcNow;
			double spot = data.TryGetProperty("current_price", out var cpEl) && cpEl.GetDouble() > 0
				? cpEl.GetDouble() : spotBars;

			var puts = new List<Q>();
			foreach (var o in data.GetProperty("options").EnumerateArray())
			{
				var mm = Regex.Match(o.GetProperty("option").GetString() ?? "", @"^[A-Z]+(?<ymd>\d{6})P(?<k>\d{8})$");
				if (!mm.Success) continue;
				var ymd = mm.Groups["ymd"].Value;
				var exp = new DateTime(2000 + int.Parse(ymd[..2]), int.Parse(ymd[2..4]), int.Parse(ymd[4..6]));
				puts.Add(new Q(
					double.Parse(mm.Groups["k"].Value, CultureInfo.InvariantCulture) / 1000.0,
					o.TryGetProperty("bid", out var b) ? b.GetDouble() : 0,
					o.TryGetProperty("ask", out var a) ? a.GetDouble() : 0,
					o.TryGetProperty("iv", out var iv) ? iv.GetDouble() : 0,
					o.TryGetProperty("delta", out var dl) ? dl.GetDouble() : 0,
					o.TryGetProperty("open_interest", out var oi) ? oi.GetDouble() : 0,
					exp));
			}

			// the expiry ONE trading day out -- matches the backtest's close-to-close horizon
			var expiries = puts.Select(p => p.Exp).Distinct().Where(e => e.Date > snap.Date).OrderBy(e => e).ToList();
			if (expiries.Count == 0) { Console.WriteLine("no forward expiries in chain"); return; }
			var useExp = expiries.First();
			var chain = puts.Where(p => p.Exp == useExp && p.Bid > 0 && p.Ask > 0).OrderBy(p => p.Strike).ToList();
			if (chain.Count < 20) { Console.WriteLine($"expiry {useExp:yyyy-MM-dd} has only {chain.Count} quoted puts"); return; }

			double fracToday = Math.Max(0, (new TimeSpan(16, 0, 0) - snap.TimeOfDay).TotalHours) / 6.5;
			int bdays = 0;
			for (var d = snap.Date.AddDays(1); d <= useExp.Date; d = d.AddDays(1))
				if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) bdays++;
			// bdays counts FULL trading days from tomorrow through the expiry date; fracToday is the remainder of
			// today's session. Time to a close-expiry is therefore bdays + fracToday (same-day expiry -> bdays = 0
			// -> T = fracToday). Getting this wrong by one day understates model credit and inflates the ratio.
			double T = Math.Max(1e-5, (bdays + fracToday) / 252.0);

			Console.WriteLine($"\n===== CREDIT-SPREAD QUOTE VALIDATION: {underlying} =====");
			Console.WriteLine($"snapshot {stamp} | spot {spot:0.00} | engine target {target:0.000} | trailing HV(60) {100 * hv:0.0}%");
			Console.WriteLine($"expiry {useExp:yyyy-MM-dd} | {chain.Count} quoted puts | T = {T * 252:0.00} trading days ({T:0.00000} yr)");

			// pick strikes by the CHAIN's own delta (magnitude), not a model
			Q PickByDelta(double mag)
			{
				return chain.OrderBy(p => Math.Abs(Math.Abs(p.Delta) - mag)).First();
			}
			var shortLeg = PickByDelta(ShortDelta);
			double protDelta = ShortDelta - target;
			bool naked = protDelta <= 1e-4;
			Q? longLeg = naked ? null : PickByDelta(protDelta);

			double Mid(Q q) => 0.5 * (q.Bid + q.Ask);
			double marketMid = Mid(shortLeg) - (longLeg is null ? 0 : Mid(longLeg));
			double marketCross = shortLeg.Bid - (longLeg is null ? 0 : longLeg.Ask);   // sell at bid, buy at ask

			double ivModel = hv * AssumedVrp;
			double modelCredit = Put(spot, shortLeg.Strike, ivModel, T) -
								 (longLeg is null ? 0 : Put(spot, longLeg.Strike, ivModel, T));

			double realisedVrp = hv > 0 ? shortLeg.Iv / hv : double.NaN;

			Console.WriteLine($"\nshort leg: K {shortLeg.Strike,8:0.00}  delta {shortLeg.Delta,6:0.000}  bid {shortLeg.Bid,7:0.00}  ask {shortLeg.Ask,7:0.00}  iv {shortLeg.Iv,6:0.000}  OI {shortLeg.Oi,8:N0}");
			if (longLeg is null)
				Console.WriteLine($" long leg: NONE -- target {target:0.000} > {ShortDelta:0.00}, structure is a NAKED short put");
			else
				Console.WriteLine($" long leg: K {longLeg.Strike,8:0.00}  delta {longLeg.Delta,6:0.000}  bid {longLeg.Bid,7:0.00}  ask {longLeg.Ask,7:0.00}  iv {longLeg.Iv,6:0.000}  OI {longLeg.Oi,8:N0}");

			Console.WriteLine($"\n{"credit basis",22} {"per share",11} {"% of spot",11} {"vs model",10}");
			Console.WriteLine($"{"MODEL (HV x " + AssumedVrp.ToString("0.00") + ")",22} {modelCredit,11:0.000} {100 * modelCredit / spot,11:0.0000} {"--",10}");
			Console.WriteLine($"{"MARKET mid",22} {marketMid,11:0.000} {100 * marketMid / spot,11:0.0000} {(modelCredit > 0 ? marketMid / modelCredit : 0),10:0.00}x");
			Console.WriteLine($"{"MARKET cross-spread",22} {marketCross,11:0.000} {100 * marketCross / spot,11:0.0000} {(modelCredit > 0 ? marketCross / modelCredit : 0),10:0.00}x");

			Console.WriteLine($"\nREALISED VRP at the short strike: market IV {100 * shortLeg.Iv:0.0}% / HV(60) {100 * hv:0.0}% = {realisedVrp:0.000}" +
				$"   (backtest assumes {AssumedVrp:0.00})");
			Console.WriteLine($"execution drag mid -> cross: {(marketMid > 0 ? 100 * (1 - marketCross / marketMid) : 0):0.0}% of the credit");

			bool need = !File.Exists(SeriesPath);
			using (var w = new StreamWriter(SeriesPath, append: true))
			{
				if (need) w.WriteLine("snapshot,underlying,spot,target,hv60,expiry,T_days,short_k,short_delta,short_iv,short_bid,short_ask," +
					"long_k,long_delta,long_bid,long_ask,model_credit,market_mid,market_cross,realised_vrp");
				w.WriteLine(string.Join(",", new[]
				{
					stamp, underlying, spot.ToString("R", CultureInfo.InvariantCulture), target.ToString("R", CultureInfo.InvariantCulture),
					hv.ToString("R", CultureInfo.InvariantCulture), useExp.ToString("yyyy-MM-dd"), (T * 252).ToString("0.000", CultureInfo.InvariantCulture),
					shortLeg.Strike.ToString(CultureInfo.InvariantCulture), shortLeg.Delta.ToString(CultureInfo.InvariantCulture),
					shortLeg.Iv.ToString(CultureInfo.InvariantCulture), shortLeg.Bid.ToString(CultureInfo.InvariantCulture),
					shortLeg.Ask.ToString(CultureInfo.InvariantCulture),
					longLeg?.Strike.ToString(CultureInfo.InvariantCulture) ?? "", longLeg?.Delta.ToString(CultureInfo.InvariantCulture) ?? "",
					longLeg?.Bid.ToString(CultureInfo.InvariantCulture) ?? "", longLeg?.Ask.ToString(CultureInfo.InvariantCulture) ?? "",
					modelCredit.ToString("R", CultureInfo.InvariantCulture), marketMid.ToString("R", CultureInfo.InvariantCulture),
					marketCross.ToString("R", CultureInfo.InvariantCulture), realisedVrp.ToString("R", CultureInfo.InvariantCulture),
				}));
			}

			// keep the near-dated put chain so the profile can be re-examined later
			string chainPath = Path.Combine(Path.GetFullPath(Universe.DataDir), "gex_chains",
				$"{underlying}_puts_{useExp:yyyyMMdd}_{snap:yyyyMMdd}.csv");
			Directory.CreateDirectory(Path.GetDirectoryName(chainPath)!);
			using (var w = new StreamWriter(chainPath))
			{
				w.WriteLine("strike,bid,ask,iv,delta,open_interest");
				foreach (var p in chain)
					w.WriteLine($"{p.Strike.ToString(CultureInfo.InvariantCulture)},{p.Bid.ToString(CultureInfo.InvariantCulture)}," +
						$"{p.Ask.ToString(CultureInfo.InvariantCulture)},{p.Iv.ToString(CultureInfo.InvariantCulture)}," +
						$"{p.Delta.ToString(CultureInfo.InvariantCulture)},{p.Oi.ToString(CultureInfo.InvariantCulture)}");
			}
			Console.WriteLine($"\nappended to {SeriesPath}");
			Console.WriteLine($"put chain -> {chainPath}");
			Console.WriteLine("Run daily near the close. Once ~30 rows accumulate, the market/model ratio and realised VRP");
			Console.WriteLine("replace the haircut GUESS in CreditSpreadTailTest with a measurement.");
		}

		private static double Nd(double x) => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));
		private static double Erf(double x)
		{
			double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
			double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
			return x >= 0 ? y : -y;
		}
		private static double Put(double S, double K, double iv, double T)
		{
			if (T <= 0 || iv <= 0) return Math.Max(0, K - S);
			double v = iv * Math.Sqrt(T);
			double d1 = (Math.Log(S / K) + 0.5 * iv * iv * T) / v;
			return K * Nd(v - d1) - S * Nd(-d1);
		}
	}
}
