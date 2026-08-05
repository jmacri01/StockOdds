using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StockOdds
{
	// Disk-cached bar fetch. A broad-universe run touches several thousand symbols, which is far too many
	// to re-pull from Yahoo on every experiment, so bars land in data/bars/{SYM}.csv once and are read from
	// there afterwards. Symbols that fail or come back too short get a .miss marker so a re-run doesn't keep
	// hammering them -- delete data/bars/*.miss to retry those.
	public static class BarCache
	{
		public static string Dir = Path.Combine(Path.GetFullPath(Universe.DataDir), "bars");
		public static int Concurrency = 6;          // polite; Yahoo throttles above this
		public static int MaxAttempts = 3;
		public static int MinBars = 400;            // below this a 5y window can't support a last-30% OOS slice

		private static string PathFor(string sym) => Path.Combine(Dir, sym.ToUpperInvariant() + ".csv");
		private static string MissFor(string sym) => Path.Combine(Dir, sym.ToUpperInvariant() + ".miss");

		public static List<OhlcBar>? Load(string sym)
		{
			string p = PathFor(sym);
			if (!File.Exists(p)) return null;
			var bars = new List<OhlcBar>();
			foreach (var ln in File.ReadLines(p).Skip(1))
			{
				var f = ln.Split(',');
				if (f.Length < 5) continue;
				bars.Add(new OhlcBar
				{
					Date  = DateTime.ParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
					Open  = double.Parse(f[1], CultureInfo.InvariantCulture),
					High  = double.Parse(f[2], CultureInfo.InvariantCulture),
					Low   = double.Parse(f[3], CultureInfo.InvariantCulture),
					Close = double.Parse(f[4], CultureInfo.InvariantCulture),
				});
			}
			return bars;
		}

		private static void Save(string sym, List<OhlcBar> bars)
		{
			Directory.CreateDirectory(Dir);
			using var w = new StreamWriter(PathFor(sym));
			w.WriteLine("date,open,high,low,close");
			foreach (var b in bars)
				w.WriteLine($"{b.Date:yyyy-MM-dd},{b.Open.ToString(CultureInfo.InvariantCulture)}," +
					$"{b.High.ToString(CultureInfo.InvariantCulture)},{b.Low.ToString(CultureInfo.InvariantCulture)}," +
					$"{b.Close.ToString(CultureInfo.InvariantCulture)}");
		}

		// Fetch everything not already cached. Resumable: re-running only pulls what's still missing.
		public static async Task PrimeAsync(IEnumerable<string> symbols, string interval = "1d")
		{
			Directory.CreateDirectory(Dir);
			var todo = symbols.Where(s => !File.Exists(PathFor(s)) && !File.Exists(MissFor(s))).Distinct().ToList();
			int have = symbols.Count() - todo.Count;
			Console.WriteLine($"BarCache: {have} cached, {todo.Count} to fetch (concurrency {Concurrency})");
			if (todo.Count == 0) return;

			int done = 0, ok = 0, miss = 0;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			using var gate = new SemaphoreSlim(Concurrency);

			var tasks = todo.Select(async sym =>
			{
				await gate.WaitAsync();
				try
				{
					for (int attempt = 1; attempt <= MaxAttempts; attempt++)
					{
						try
						{
							var bars = await YahooClient.GetBarsAsync(sym, interval);
							if (bars.Count >= MinBars) { Save(sym, bars); Interlocked.Increment(ref ok); }
							else { File.WriteAllText(MissFor(sym), $"short:{bars.Count}"); Interlocked.Increment(ref miss); }
							break;
						}
						catch (Exception ex)
						{
							if (attempt == MaxAttempts)
							{
								File.WriteAllText(MissFor(sym), ex.GetType().Name);
								Interlocked.Increment(ref miss);
							}
							else await Task.Delay(400 * attempt * attempt);   // backoff
						}
					}
				}
				finally
				{
					gate.Release();
					int d = Interlocked.Increment(ref done);
					if (d % 250 == 0 || d == todo.Count)
						Console.WriteLine($"  {d}/{todo.Count}  ok={ok} miss={miss}  {sw.Elapsed:hh\\:mm\\:ss}  " +
							$"({d / Math.Max(1.0, sw.Elapsed.TotalSeconds):0.0}/s)");
				}
			});

			await Task.WhenAll(tasks);
			Console.WriteLine($"BarCache: done in {sw.Elapsed:hh\\:mm\\:ss} -- {ok} fetched, {miss} unusable");
		}

		// Load every cached symbol from the list, in parallel off disk.
		public static Dictionary<string, List<OhlcBar>> LoadAll(IEnumerable<string> symbols)
		{
			var res = new ConcurrentDictionary<string, List<OhlcBar>>();
			Parallel.ForEach(symbols, sym =>
			{
				var b = Load(sym);
				if (b != null && b.Count >= MinBars) res[sym] = b;
			});
			return res.ToDictionary(kv => kv.Key, kv => kv.Value);
		}
	}
}
