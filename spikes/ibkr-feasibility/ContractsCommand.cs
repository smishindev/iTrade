using System.Text;
using IBApi;

namespace ITrade.Spikes.Ibkr;

/// <summary>
/// P1.B.05: contract details for every candidate of the universe file → out/contracts.csv, and a
/// comparison of IBKR's primary exchange with the file's `listing`. Read-only.
/// </summary>
public static class ContractsCommand
{
    public static async Task<int> Run(SpikeConfig config, MessageLog log, string[] args)
    {
        var universePath = Requests.Text(args, "--universe", "");
        if (universePath.Length == 0)
        {
            universePath = Universe.FindDefault();
        }

        var candidates = Universe.Load(universePath);
        Console.WriteLine($"Universe: {universePath} ({candidates.Count} candidates)");

        using var session = new IbkrSession(config, log);
        var result = await session.ConnectAsync();
        if (result != 0)
        {
            return result;
        }

        var csv = new StringBuilder();
        csv.AppendLine("ticker,listing_file,primary_exchange,listing_match,con_id,matches,min_tick,min_size,size_increment,suggested_size_increment,min_algo_size,fractional_hint,long_name,stock_type,isin,time_zone,market_rule_ids,valid_exchanges,key_order_types,order_types_count,trading_hours_sample,liquid_hours_sample,error");
        var mismatches = new List<string>();
        var timeout = TimeSpan.FromSeconds(config.ConnectTimeoutSeconds);

        foreach (var candidate in candidates)
        {
            // No primaryExch in the request: we want IBKR's own answer, then compare with the file.
            var (details, errors) = await Requests.ContractDetailsAsync(session, Requests.UsStock(candidate.Ticker), timeout);
            var error = string.Join("; ", errors.Select(e => $"{e.Code} {e.Message}"));
            if (details.Count == 0)
            {
                Console.WriteLine($"  {candidate.Ticker,-6} NO DETAILS {error}");
                string[] empty = [Requests.Csv(candidate.Ticker), Requests.Csv(candidate.Listing), "", "", "", "0", .. Enumerable.Repeat("", 16), Requests.Csv(error)];
                csv.AppendLine(string.Join(',', empty));
                mismatches.Add($"{candidate.Ticker}: no contract details ({error})");
                continue;
            }

            var d = details[0];
            var c = d.Contract;
            var match = string.Equals(c.PrimaryExch, candidate.Listing, StringComparison.OrdinalIgnoreCase);
            if (!match)
            {
                mismatches.Add($"{candidate.Ticker}: file listing={candidate.Listing}, IBKR primaryExchange={c.PrimaryExch}");
            }

            var fractional = FractionalHint(d);
            Console.WriteLine(
                $"  {candidate.Ticker,-6} conId={c.ConId,-10} prim={c.PrimaryExch,-8} file={candidate.Listing,-7}{(match ? "" : " MISMATCH")} " +
                $"minTick={d.MinTick} minSize={d.MinSize} sizeInc={d.SizeIncrement} suggInc={d.SuggestedSizeIncrement} -> {fractional}" +
                (details.Count > 1 ? $" ({details.Count} matches!)" : ""));

            csv.AppendLine(string.Join(',',
                Requests.Csv(candidate.Ticker),
                Requests.Csv(candidate.Listing),
                Requests.Csv(c.PrimaryExch),
                match ? "yes" : "NO",
                Requests.Csv(c.ConId),
                Requests.Csv(details.Count),
                Requests.Csv(d.MinTick),
                Requests.Csv(d.MinSize),
                Requests.Csv(d.SizeIncrement),
                Requests.Csv(d.SuggestedSizeIncrement),
                Requests.Csv(d.MinAlgoSize),
                Requests.Csv(fractional),
                Requests.Csv(d.LongName),
                Requests.Csv(d.StockType),
                Requests.Csv(d.SecIdList?.FirstOrDefault(t => t.Tag == "ISIN")?.Value),
                Requests.Csv(d.TimeZoneId),
                Requests.Csv(string.Join(',', (d.MarketRuleIds ?? "").Split(',').Distinct())),
                Requests.Csv(d.ValidExchanges),
                Requests.Csv(KeyOrderTypes(d.OrderTypes)),
                Requests.Csv(d.OrderTypes?.Split(',').Length ?? 0),
                Requests.Csv(FirstSessions(d.TradingHours)),
                Requests.Csv(FirstSessions(d.LiquidHours)),
                Requests.Csv(error)));
        }

        var path = Requests.OutPath(config, "contracts.csv");
        await File.WriteAllTextAsync(path, csv.ToString());
        Console.WriteLine();
        Console.WriteLine($"Wrote {path}");
        Console.WriteLine(mismatches.Count == 0 ? "primaryExchange matches `listing` for every candidate." : "Mismatches vs universe file:");
        foreach (var m in mismatches)
        {
            Console.WriteLine($"  {m}");
        }

        return 0;
    }

    /// <summary>What the size fields say about fractional quantities (to be confirmed by real orders in P1.B.10).</summary>
    private static string FractionalHint(ContractDetails d) =>
        d.SizeIncrement is > 0m and < 1m || d.MinSize is > 0m and < 1m
            ? "fractional-sizes"
            : d.SizeIncrement >= 1m ? "whole-shares-only" : "unknown";

    /// <summary>Order types the strategy needs (spec: LMT/OPG entry, STP/GTC stop, MKT/OPG exit in OCA), plus CASHQTY.</summary>
    private static string KeyOrderTypes(string? orderTypes)
    {
        var have = (orderTypes ?? "").Split(',').ToHashSet(StringComparer.Ordinal);
        return string.Join(' ', new[] { "LMT", "MKT", "STP", "OPG", "GTC", "OCA", "LOC", "MOC", "CASHQTY" }.Select(t => have.Contains(t) ? t : "-" + t));
    }

    /// <summary>First two day entries of a "yyyyMMdd:HHmm-yyyyMMdd:HHmm;..." schedule.</summary>
    private static string FirstSessions(string? hours) =>
        string.IsNullOrEmpty(hours) ? "" : string.Join(';', hours.Split(';').Take(2));
}
