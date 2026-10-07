using System.Collections.Concurrent;
using IBApi;

namespace ITrade.Spikes.Ibkr;

/// <summary>
/// P1.B.04: account summary (all standard tags), cash by currency ($LEDGER:ALL), positions, and the
/// cash/settlement keys of reqAccountUpdates. Read-only. The account number is masked on the console.
/// </summary>
public static class AccountCommand
{
    public static async Task<int> Run(SpikeConfig config, MessageLog log)
    {
        using var session = new IbkrSession(config, log);
        var result = await session.ConnectAsync();
        if (result != 0)
        {
            return result;
        }

        var timeout = TimeSpan.FromSeconds(config.ConnectTimeoutSeconds);

        Console.WriteLine();
        Console.WriteLine("== reqAccountSummary(All tags) ==");
        foreach (var row in await Summary(session, log, AccountSummaryTags.GetAllTags(), timeout))
        {
            Console.WriteLine($"  {IbkrSession.Mask(row.Account),-12} {row.Tag,-32} {row.Value,20} {row.Currency}");
        }

        Console.WriteLine();
        Console.WriteLine("== reqAccountSummary($LEDGER:ALL) — cash by currency ==");
        foreach (var row in await Summary(session, log, "$LEDGER:ALL", timeout))
        {
            Console.WriteLine($"  {IbkrSession.Mask(row.Account),-12} {row.Tag,-32} {row.Value,20} {row.Currency}");
        }

        Console.WriteLine();
        Console.WriteLine("== reqPositions ==");
        var positions = await Positions(session, log, timeout);
        Console.WriteLine(positions.Count == 0 ? "  (no positions)" : string.Join(Environment.NewLine, positions));

        foreach (var account in session.Accounts)
        {
            Console.WriteLine();
            Console.WriteLine($"== reqAccountUpdates({IbkrSession.Mask(account)}) — cash/settlement keys ==");
            var values = await AccountUpdates(session, log, account, timeout);
            Console.WriteLine($"  {values.Count} key/currency pairs received; cash-related:");
            foreach (var (key, value, currency) in values.Where(v => IsCashKey(v.Key)).OrderBy(v => v.Key, StringComparer.Ordinal).ThenBy(v => v.Currency, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {key,-32} {value,20} {currency}");
            }
        }

        return 0;
    }

    private static bool IsCashKey(string key) =>
        key.Contains("Cash", StringComparison.Ordinal) || key.Contains("Settled", StringComparison.Ordinal)
        || key is "NetLiquidationByCurrency" or "ExchangeRate" or "BuyingPower" or "AvailableFunds" or "NetLiquidation";

    private sealed record SummaryRow(string Account, string Tag, string Value, string Currency);

    private static async Task<List<SummaryRow>> Summary(IbkrSession session, MessageLog log, string tags, TimeSpan timeout)
    {
        var id = session.NewRequestId();
        var rows = new ConcurrentQueue<SummaryRow>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s1 = session.Callbacks.On("accountSummary", a =>
        {
            if ((int)a[0]! == id)
            {
                rows.Enqueue(new SummaryRow((string)a[1]!, (string)a[2]!, (string)a[3]!, (string)a[4]!));
            }
        });
        using var s2 = session.Callbacks.On("accountSummaryEnd", a =>
        {
            if ((int)a[0]! == id)
            {
                done.TrySetResult();
            }
        });

        log.Write("out", "reqAccountSummary", [new("reqId", id), new("group", "All"), new("tags", tags)]);
        session.Client.reqAccountSummary(id, "All", tags);
        await Wait(done.Task, timeout, "accountSummaryEnd");
        session.Client.cancelAccountSummary(id);
        return [.. rows];
    }

    private static async Task<List<string>> Positions(IbkrSession session, MessageLog log, TimeSpan timeout)
    {
        var rows = new ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s1 = session.Callbacks.On("position", a =>
        {
            var c = (Contract)a[1]!;
            rows.Enqueue($"  {IbkrSession.Mask((string)a[0]!),-12} {c.Symbol,-8} {c.SecType,-4} conId={c.ConId} pos={(decimal)a[2]!} avgCost={(double)a[3]!}");
        });
        using var s2 = session.Callbacks.On("positionEnd", _ => done.TrySetResult());

        log.Write("out", "reqPositions", []);
        session.Client.reqPositions();
        await Wait(done.Task, timeout, "positionEnd");
        session.Client.cancelPositions();
        return [.. rows];
    }

    private static async Task<List<(string Key, string Value, string Currency)>> AccountUpdates(
        IbkrSession session, MessageLog log, string account, TimeSpan timeout)
    {
        var rows = new ConcurrentDictionary<(string, string), string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s1 = session.Callbacks.On("updateAccountValue", a => rows[((string)a[0]!, (string)a[2]!)] = (string)a[1]!);
        using var s2 = session.Callbacks.On("accountDownloadEnd", _ => done.TrySetResult());

        log.Write("out", "reqAccountUpdates", [new("subscribe", true)]);
        session.Client.reqAccountUpdates(true, account);
        await Wait(done.Task, timeout, "accountDownloadEnd");
        log.Write("out", "reqAccountUpdates", [new("subscribe", false)]);
        session.Client.reqAccountUpdates(false, account);
        return [.. rows.Select(r => (r.Key.Item1, r.Value, r.Key.Item2))];
    }

    private static async Task Wait(Task task, TimeSpan timeout, string what)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"  (timed out waiting for {what})");
        }
    }
}
