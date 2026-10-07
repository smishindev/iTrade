using System.Collections.Concurrent;
using System.Globalization;
using IBApi;

namespace ITrade.Spikes.Ibkr;

/// <summary>Request/response helpers shared by the read-only commands (no orders here, ever).</summary>
public static class Requests
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Contract UsStock(string ticker, string primaryExchange = "") => new()
    {
        Symbol = ticker,
        SecType = "STK",
        Exchange = "SMART",
        Currency = "USD",
        PrimaryExch = primaryExchange,
    };

    /// <summary>reqContractDetails → all contractDetails until contractDetailsEnd, or the errors for that reqId.</summary>
    public static async Task<(List<ContractDetails> Details, List<(int Code, string Message)> Errors)> ContractDetailsAsync(
        IbkrSession session, Contract contract, TimeSpan timeout)
    {
        var id = session.NewRequestId();
        var details = new ConcurrentQueue<ContractDetails>();
        var errors = new ConcurrentQueue<(int, string)>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var s1 = session.Callbacks.On("contractDetails", a =>
        {
            if ((int)a[0]! == id)
            {
                details.Enqueue((ContractDetails)a[1]!);
            }
        });
        using var s2 = session.Callbacks.On("contractDetailsEnd", a =>
        {
            if ((int)a[0]! == id)
            {
                done.TrySetResult();
            }
        });
        using var s3 = session.Callbacks.On("error/5", a =>
        {
            if ((int)a[0]! == id)
            {
                errors.Enqueue(((int)a[2]!, (string)a[3]!));
                if (!IsWarning((int)a[2]!))
                {
                    done.TrySetResult();
                }
            }
        });

        session.Client.reqContractDetails(id, contract);
        try
        {
            await done.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            errors.Enqueue((-1, "timeout"));
        }

        return ([.. details], [.. errors]);
    }

    /// <summary>2100–2199 and 10167 are warnings: the request goes on.</summary>
    public static bool IsWarning(int code) => code is >= 2100 and < 2200 or 10167;

    public static string Csv(object? value)
    {
        var text = value switch
        {
            null => "",
            double d => d.ToString(Inv),
            decimal m => m.ToString(Inv),
            IFormattable f => f.ToString(null, Inv),
            _ => value.ToString() ?? "",
        };
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    public static double Option(string[] args, string name, double fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], Inv) : fallback;
    }

    public static string Text(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    public static string OutPath(SpikeConfig config, string fileName)
    {
        var dir = Path.Combine(Environment.CurrentDirectory, config.LogDirectory);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }
}
