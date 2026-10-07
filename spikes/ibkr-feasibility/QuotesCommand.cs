using System.Collections.Concurrent;
using System.Text;
using IBApi;

namespace ITrade.Spikes.Ibkr;

/// <summary>
/// P1.B.06: which market data types arrive through the API without paid subscriptions.
/// For each reqMarketDataType (1 live, 2 frozen, 3 delayed, 4 delayed-frozen) it streams the tickers
/// briefly, then asks for a (free, non-regulatory) snapshot, and records marketDataType callbacks,
/// tick fields, the age of the last trade and the error codes. Never requests regulatory snapshots.
/// </summary>
public static class QuotesCommand
{
    private sealed class Probe(string ticker, int requestedType, string mode, DateTime sentUtc)
    {
        public string Ticker { get; } = ticker;
        public int RequestedType { get; } = requestedType;
        public string Mode { get; } = mode;
        public DateTime SentUtc { get; } = sentUtc;
        public ConcurrentDictionary<int, byte> TypesReported { get; } = new();
        public ConcurrentDictionary<string, (double Price, double FirstAfterMs)> Prices { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public string LastTradeTime { get; set; } = "";
        public string SnapshotPermissions { get; set; } = "";
        public double? SnapshotEndMs { get; set; }
    }

    public static async Task<int> Run(SpikeConfig config, MessageLog log, string[] args)
    {
        var tickers = Requests.Text(args, "--tickers", "SPY,VWO").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var types = Requests.Text(args, "--types", "1,2,3,4").Split(',').Select(int.Parse).ToArray();
        var streamSeconds = Requests.Option(args, "--seconds", 10);

        using var session = new IbkrSession(config, log);
        var result = await session.ConnectAsync();
        if (result != 0)
        {
            return result;
        }

        var contracts = new Dictionary<string, Contract>(StringComparer.Ordinal);
        foreach (var ticker in tickers)
        {
            var (details, errors) = await Requests.ContractDetailsAsync(session, Requests.UsStock(ticker), TimeSpan.FromSeconds(15));
            if (details.Count == 0)
            {
                Console.Error.WriteLine($"No contract for {ticker}: {string.Join("; ", errors)}");
                return 1;
            }

            var c = details[0].Contract;
            contracts[ticker] = new Contract { ConId = c.ConId, Symbol = c.Symbol, SecType = "STK", Exchange = "SMART", PrimaryExch = c.PrimaryExch, Currency = "USD" };
        }

        var probes = new ConcurrentDictionary<int, Probe>();
        Probe? Find(object? id) => probes.TryGetValue((int)id!, out var p) ? p : null;

        session.Callbacks.On("marketDataType", a => Find(a[0])?.TypesReported.TryAdd((int)a[1]!, 0));
        session.Callbacks.On("tickPrice", a =>
        {
            if (Find(a[0]) is { } p)
            {
                var field = TickType.getField((int)a[1]!);
                p.Prices.TryAdd(field, ((double)a[2]!, (DateTime.UtcNow - p.SentUtc).TotalMilliseconds));
            }
        });
        session.Callbacks.On("tickString", a =>
        {
            // 45 = LAST_TIMESTAMP, 88 = DELAYED_LAST_TIMESTAMP (unix seconds)
            if (Find(a[0]) is { } p && (int)a[1]! is 45 or 88 && long.TryParse((string)a[2]!, out var unix))
            {
                var t = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                p.LastTradeTime = $"{TickType.getField((int)a[1]!)}={t:yyyy-MM-dd HH:mm:ss}Z (age {(DateTime.UtcNow - t).TotalMinutes:0.0} min)";
            }
        });
        session.Callbacks.On("tickReqParams", a =>
        {
            if (Find(a[0]) is { } p)
            {
                p.SnapshotPermissions = $"minTick={a[1]} bbo={a[2]} snapshotPermissions={a[3]}";
            }
        });
        session.Callbacks.On("tickSnapshotEnd", a =>
        {
            if (Find(a[0]) is { } p)
            {
                p.SnapshotEndMs = (DateTime.UtcNow - p.SentUtc).TotalMilliseconds;
            }
        });
        session.Error += (id, code, message) =>
        {
            if (probes.TryGetValue(id, out var p))
            {
                p.Errors.Enqueue($"{code} {message}");
            }
        };

        Console.WriteLine($"US/Eastern now: {NewYorkNow():yyyy-MM-dd HH:mm} (market hours 09:30–16:00)");

        foreach (var type in types)
        {
            Console.WriteLine();
            Console.WriteLine($"== reqMarketDataType({type}) ==");
            log.Write("out", "reqMarketDataType", [new("marketDataType", type)]);
            session.Client.reqMarketDataType(type);

            foreach (var mode in new[] { "stream", "snapshot" })
            {
                var ids = new List<int>();
                foreach (var ticker in tickers)
                {
                    var id = session.NewRequestId();
                    probes[id] = new Probe(ticker, type, mode, DateTime.UtcNow);
                    ids.Add(id);
                    log.Write("out", "reqMktData", [new("reqId", id), new("ticker", ticker), new("snapshot", mode == "snapshot"), new("regulatorySnapshot", false)]);
                    session.Client.reqMktData(id, contracts[ticker], "", mode == "snapshot", false, []);
                }

                if (mode == "stream")
                {
                    await Task.Delay(TimeSpan.FromSeconds(streamSeconds));
                    foreach (var id in ids)
                    {
                        log.Write("out", "cancelMktData", [new("reqId", id)]);
                        session.Client.cancelMktData(id);
                    }
                }
                else
                {
                    // Snapshots finish with tickSnapshotEnd (≈11 s when data is missing).
                    var until = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < until && ids.Any(id => probes[id].SnapshotEndMs is null))
                    {
                        await Task.Delay(200);
                    }
                }

                foreach (var id in ids)
                {
                    Print(probes[id]);
                }
            }

            await Task.Delay(1000);
        }

        var csv = new StringBuilder("ticker,requested_type,mode,types_reported,fields,first_tick_ms,last_trade,snapshot_end_ms,tick_req_params,errors\n");
        foreach (var p in probes.OrderBy(p => p.Key).Select(p => p.Value))
        {
            csv.AppendLine(string.Join(',',
                Requests.Csv(p.Ticker), p.RequestedType, p.Mode,
                Requests.Csv(string.Join(' ', p.TypesReported.Keys.Order())),
                Requests.Csv(string.Join(' ', p.Prices.OrderBy(x => x.Value.FirstAfterMs).Select(x => $"{x.Key}={x.Value.Price}"))),
                Requests.Csv(p.Prices.IsEmpty ? "" : p.Prices.Values.Min(v => v.FirstAfterMs).ToString("0", Requests.Inv)),
                Requests.Csv(p.LastTradeTime),
                Requests.Csv(p.SnapshotEndMs?.ToString("0", Requests.Inv)),
                Requests.Csv(p.SnapshotPermissions),
                Requests.Csv(string.Join(" | ", p.Errors))));
        }

        var path = Requests.OutPath(config, $"quotes-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.csv");
        await File.WriteAllTextAsync(path, csv.ToString());
        Console.WriteLine();
        Console.WriteLine($"Wrote {path}");
        return 0;
    }

    public static DateTime NewYorkNow()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }

    private static void Print(Probe p)
    {
        var fields = string.Join(' ', p.Prices.OrderBy(x => x.Value.FirstAfterMs).Select(x => $"{x.Key}={x.Value.Price}"));
        var first = p.Prices.IsEmpty ? "-" : $"{p.Prices.Values.Min(v => v.FirstAfterMs):0}ms";
        Console.WriteLine($"  {p.Ticker,-4} {p.Mode,-8} types=[{string.Join(',', p.TypesReported.Keys.Order())}] first={first} {fields}");
        if (p.LastTradeTime.Length > 0)
        {
            Console.WriteLine($"       last trade: {p.LastTradeTime}");
        }

        if (p.SnapshotEndMs is { } end)
        {
            Console.WriteLine($"       tickSnapshotEnd after {end:0}ms");
        }

        foreach (var e in p.Errors)
        {
            Console.WriteLine($"       error {e}");
        }
    }
}
