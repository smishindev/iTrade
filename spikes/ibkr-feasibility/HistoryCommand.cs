using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using IBApi;

namespace ITrade.Spikes.Ibkr;

/// <summary>
/// P1.B.07: daily bars (TRADES, regular trading hours) for a few tickers → out/history-{ticker}.csv.
/// Requests are sent one at a time (pacing). `--repeat N` sends the same request N more times right away to
/// observe IBKR's pacing rule for identical requests.
/// </summary>
public static class HistoryCommand
{
    public static async Task<int> Run(SpikeConfig config, MessageLog log, string[] args)
    {
        var tickers = Requests.Text(args, "--tickers", "SPY,VWO").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var duration = Requests.Text(args, "--duration", "5 Y");
        var whatToShow = Requests.Text(args, "--what", "TRADES");
        var end = Requests.Text(args, "--end", ""); // "" = now; e.g. "20261006 16:00:00 US/Eastern"
        var repeat = (int)Requests.Option(args, "--repeat", 0);

        using var session = new IbkrSession(config, log);
        var result = await session.ConnectAsync();
        if (result != 0)
        {
            return result;
        }

        foreach (var ticker in tickers)
        {
            var (details, errors) = await Requests.ContractDetailsAsync(session, Requests.UsStock(ticker), TimeSpan.FromSeconds(15));
            if (details.Count == 0)
            {
                Console.Error.WriteLine($"No contract for {ticker}: {string.Join("; ", errors)}");
                continue;
            }

            var c = details[0].Contract;
            var contract = new Contract { ConId = c.ConId, Symbol = c.Symbol, SecType = "STK", Exchange = "SMART", PrimaryExch = c.PrimaryExch, Currency = "USD" };

            var (bars, barErrors, elapsed, span) = await Fetch(session, log, contract, end, duration, whatToShow);
            Console.WriteLine($"  {ticker}: {bars.Count} bars in {elapsed.TotalSeconds:0.0}s {span} {string.Join("; ", barErrors)}");
            if (bars.Count > 0)
            {
                var csv = new StringBuilder("date,open,high,low,close,volume,wap,count\n");
                foreach (var b in bars)
                {
                    csv.AppendLine(string.Join(',', b.Time, Requests.Csv(b.Open), Requests.Csv(b.High), Requests.Csv(b.Low), Requests.Csv(b.Close), Requests.Csv(b.Volume), Requests.Csv(b.WAP), b.Count));
                }

                var path = Requests.OutPath(config, $"history-{ticker}-{whatToShow}.csv");
                await File.WriteAllTextAsync(path, csv.ToString());
                Console.WriteLine($"     wrote {path}");
            }

            for (var n = 1; n <= repeat; n++)
            {
                var again = await Fetch(session, log, contract, end, duration, whatToShow);
                Console.WriteLine($"  {ticker} identical request #{n} right away: {again.Bars.Count} bars in {again.Elapsed.TotalSeconds:0.0}s {string.Join("; ", again.Errors.Where(e => !e.StartsWith("2188", StringComparison.Ordinal)))}");
            }
        }

        return 0;
    }

    private static async Task<(List<Bar> Bars, List<string> Errors, TimeSpan Elapsed, string Span)> Fetch(
        IbkrSession session, MessageLog log, Contract contract, string end, string duration, string whatToShow)
    {
        var id = session.NewRequestId();
        var bars = new ConcurrentQueue<Bar>();
        var errors = new ConcurrentQueue<string>();
        var span = "";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s1 = session.Callbacks.On("historicalData", a =>
        {
            if ((int)a[0]! == id)
            {
                bars.Enqueue((Bar)a[1]!);
            }
        });
        using var s2 = session.Callbacks.On("historicalDataEnd", a =>
        {
            if ((int)a[0]! == id)
            {
                span = $"[{a[1]} .. {a[2]}]";
                done.TrySetResult();
            }
        });
        using var s3 = session.Callbacks.On("error/5", a =>
        {
            if ((int)a[0]! == id)
            {
                errors.Enqueue($"{a[2]} {a[3]}");
                if (!Requests.IsWarning((int)a[2]!))
                {
                    done.TrySetResult();
                }
            }
        });

        var watch = Stopwatch.StartNew();
        log.Write("out", "reqHistoricalData", [new("reqId", id), new("conId", contract.ConId), new("end", end), new("duration", duration), new("barSize", "1 day"), new("whatToShow", whatToShow), new("useRTH", 1)]);
        session.Client.reqHistoricalData(id, contract, end, duration, "1 day", whatToShow, 1, 1, false, []);
        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(120));
        }
        catch (TimeoutException)
        {
            errors.Enqueue("timeout 120s");
            session.Client.cancelHistoricalData(id);
        }

        return ([.. bars], [.. errors], watch.Elapsed, span);
    }
}
