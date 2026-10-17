using System.Globalization;
using System.Text.Json;
using ITrade.Application.Prices;
using ITrade.Host.Portfolio;
using ITrade.Infrastructure.Persistence;
using ITrade.Infrastructure.Prices;
using Microsoft.EntityFrameworkCore;

namespace ITrade.Host.Prices;

internal static class PricesSetup
{
    public static void AddPrices(this IServiceCollection services)
    {
        services.AddScoped<IClosingPriceStore, ClosingPriceStore>();
        services.AddScoped<ClosingPrices>();
    }
}

/// <summary>
/// `ITrade.Host import-prices [file] [--accept-changes CODE] [--withdraw CODE "WHY"]`: loads the closing prices the
/// data pipeline exported (`itrade ingest --universe etf_pullback_v1`, or `itrade export-closes`) and the exchange's
/// sessions they cover into the database. Without a file it takes the export from the data folder. Running it again
/// adds nothing. A few prices the source corrected by a little are recorded as revisions and listed; many or large
/// changes, and a new price far from the one before it, stop the import until the owner accepts them by their code.
/// A stored session the export does not list, or a stored dividend or split it no longer states, is withdrawn only
/// by the owner, by the code shown with the finding and a reason.
/// </summary>
internal static class ImportPricesCommand
{
    public const string Name = "import-prices";
    public const string AcceptChanges = "--accept-changes";
    public const string Withdraw = "--withdraw";
    private const int MostListed = 20;

    /// <summary>
    /// Splits the command's own arguments from the settings of the Host: an optional file, after
    /// `--accept-changes` the code, after `--withdraw` the code and the reason. A flag without what it takes comes
    /// back empty, and <see cref="RunAsync"/> then explains the usage.
    /// </summary>
    public static (string? File, string? AcceptedCode, OwnersWithdrawal? Withdrawal, string[] HostArgs) Parse(string[] args)
    {
        var (file, code, rest) = ParseAccept(args);
        OwnersWithdrawal? withdrawal = null;
        var flag = rest.IndexOf(Withdraw);
        if (flag >= 0)
        {
            var taken = rest.Skip(flag + 1).Take(2).TakeWhile(a => !a.StartsWith('-')).ToList();
            // A reason typed without quotes arrives as several words: only the first would be kept, so none is.
            var unquoted = taken.Count == 2 && flag + 3 < rest.Count && !rest[flag + 3].StartsWith('-');
            withdrawal = new OwnersWithdrawal(taken.ElementAtOrDefault(0) ?? "", unquoted ? "" : taken.ElementAtOrDefault(1) ?? "");
            rest.RemoveRange(flag, 1 + taken.Count);
            if (unquoted)
            {
                // The stray words go - and with them the value of a setting written as `--key value`; that does no
                // harm only because an empty reason stops the command with its usage before the Host is used.
                rest.RemoveAll(a => !a.StartsWith('-'));
            }
        }

        return (file, code, withdrawal, [.. rest]);
    }

    private static (string? File, string? AcceptedCode, List<string> Others) ParseAccept(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var rest = args.ToList();
        string? file = null;
        if (rest.Count > 0 && !rest[0].StartsWith('-'))
        {
            file = rest[0];
            rest.RemoveAt(0);
        }

        string? code = null;
        var flag = rest.IndexOf(AcceptChanges);
        if (flag >= 0)
        {
            var hasCode = flag + 1 < rest.Count && !rest[flag + 1].StartsWith('-');
            code = hasCode ? rest[flag + 1] : "";
            rest.RemoveRange(flag, hasCode ? 2 : 1);
        }

        return (file, code, rest);
    }

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="file">The export's table, or null for the one in the data folder.</param>
    /// <param name="acceptedCode">The code of a finding the owner accepts; null for a plain import.</param>
    /// <param name="output">Where the summary goes.</param>
    /// <param name="withdrawal">What the owner withdraws, by the code of the finding and a reason; null for none.</param>
    public static async Task<int> RunAsync(
        WebApplication app, string? file, string? acceptedCode, TextWriter output, OwnersWithdrawal? withdrawal = null)
    {
        if (acceptedCode is not null && string.IsNullOrWhiteSpace(acceptedCode))
        {
            await output.WriteLineAsync($"Usage: {Name} [file] {AcceptChanges} <code> - the code is shown with the finding.");
            return 2;
        }

        if (withdrawal is not null && (string.IsNullOrWhiteSpace(withdrawal.Code) || string.IsNullOrWhiteSpace(withdrawal.Reason)
            || withdrawal.Reason.Trim().Length > ClosingPrices.LongestReason))
        {
            await output.WriteLineAsync($"Usage: {Name} [file] {Withdraw} <code> \"<why>\" - the code is shown with the finding; the reason is what you checked, in your words, in quotes, at most {ClosingPrices.LongestReason} characters.");
            return 2;
        }

        var options = app.Services.GetRequiredService<ITradeOptions>();
        var path = file is null
            ? ClosingPricesFile.DefaultPath(options.DataDir)
            : ImportInstrumentsCommand.Locate(file, Directory.GetCurrentDirectory());
        ClosingPricesExport export;
        try
        {
            export = ClosingPricesFile.Read(path);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or JsonException)
        {
            await output.WriteLineAsync($"Cannot read the closing prices: {e.Message}");
            return 1;
        }

        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        ClosingPricesImport result;
        try
        {
            result = await scope.ServiceProvider.GetRequiredService<ClosingPrices>()
                .ImportAsync(export, acceptedCode, withdrawal, CancellationToken.None);
        }
        catch (PricesDisagreeException e)
        {
            await output.WriteLineAsync($"The closing prices were not imported: {e.Message}");
            // The same export is the one that is accepted: a file the owner named is named again, and what the
            // owner already gave for the other finding is kept on the line.
            var accepting = acceptedCode is null ? "" : $" {AcceptChanges} {acceptedCode.Trim()}";
            // The reason is not echoed: a quote in it would break the line as shown.
            var withdrawing = withdrawal is null ? "" : $" {Withdraw} {withdrawal.Code.Trim()} \"<what you checked>\"";
            await output.WriteLineAsync(e.IsWithdrawal
                ? $"To withdraw exactly this (the owner's step, after checking): {WithdrawLine(file, e.Code)}{accepting}"
                : $"To accept exactly this: dotnet run --project src/ITrade.Host -- {Name} {Named(file)}{AcceptChanges} {e.Code}{withdrawing}");
            return 1;
        }
        // An export that contradicts itself, the catalogue or what is stored; or the same prices stored by
        // another run a moment ago. The database says why in its own words, under EF's general message.
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or DbUpdateException)
        {
            var why = e is DbUpdateException { InnerException: { } inner } ? inner.Message : e.Message;
            await output.WriteLineAsync($"The closing prices were not imported: {why}");
            return 1;
        }

        await output.WriteLineAsync(Invariant(
            $"Closing prices ({export.Source}, exported {export.ExportedAt:uuuu-MM-dd HH:mm} UTC): {result.Added} new; {result.Unchanged} already stored and unchanged; {result.NewSessions} new session(s) of {export.Calendar}."));
        await output.WriteLineAsync(result.LatestSession is { } latest
            ? Invariant($"Latest finished session: {latest:R}.")
            : "The export covers no session.");
        if (result.EventsAdded > 0)
        {
            await output.WriteLineAsync(Invariant($"Dividends and splits of the funds: {result.EventsAdded} new."));
        }

        foreach (var change in result.EventsRevised.Take(MostListed))
        {
            var kind = change.Stored.Kind == ITrade.Domain.FundEventKind.Split ? "split" : "dividend";
            await output.WriteLineAsync(Invariant(
                $"Revised {change.Ticker} {change.Stored.Day:R} {kind}: {change.Stored.Value:0.######} -> {change.Stated.Value:0.######} (the old figure is kept as the earlier revision)."));
        }

        if (result.EventsRevised.Count > MostListed)
        {
            await output.WriteLineAsync(Invariant($"... and {result.EventsRevised.Count - MostListed} more revised dividends or splits; all are in the audit trail."));
        }

        if (result.EventsNotRepeated.Count > 0)
        {
            // Every one the code would withdraw is named; more than that many is no correction, and has no code.
            var shown = result.EventsNotRepeated.Count <= ClosingPrices.MostEventsWithdrawnAtOnce ? result.EventsNotRepeated : result.EventsNotRepeated.Take(3).ToList();
            var more = result.EventsNotRepeated.Count - shown.Count;
            await output.WriteLineAsync(Invariant(
                $"Note: the export no longer states {result.EventsNotRepeated.Count} stored dividend(s) or split(s): {string.Join("; ", shown.Select(e => Describe(e.Ticker, e.Event)))}{(more > 0 ? $" and {more} more" : "")}. They are kept and an index is still followed with them: if the source moved or withdrew them, the comparison is off by that much."));
            if (result.WithdrawCode is { } offered)
            {
                await output.WriteLineAsync(
                    $"If the fund's own announcements show they did not happen, withdraw exactly these (the owner's step): {WithdrawLine(file, offered)}");
            }
            else if (more > 0)
            {
                await output.WriteLineAsync(Invariant(
                    $"That is more than a correction (at most {ClosingPrices.MostEventsWithdrawnAtOnce} are withdrawn at once): the export is probably incomplete - fetch and export again before deciding anything."));
            }
        }

        foreach (var day in result.SessionsWithdrawn ?? [])
        {
            await output.WriteLineAsync(Invariant($"Withdrawn by the owner: the session {day:R} of {export.Calendar} - a closed day from now on."));
        }

        foreach (var (ticker, e) in result.EventsWithdrawn ?? [])
        {
            await output.WriteLineAsync($"Withdrawn by the owner: {Describe(ticker, e)} - no longer in the index (the stored row stays, followed by its withdrawal).");
        }

        foreach (var (ticker, e) in result.EventsBack ?? [])
        {
            await output.WriteLineAsync($"Stated again after the owner withdrew it, in use again: {Describe(ticker, e)}.");
        }

        foreach (var change in result.Revised.Take(MostListed))
        {
            await output.WriteLineAsync(Invariant(
                $"Revised {change.Ticker} {change.Stored.Session:R}: {change.Stored.Close.Value:0.######} -> {change.Stated.Close.Value:0.######} (the old price is kept as the earlier revision)."));
        }

        if (result.Revised.Count > MostListed)
        {
            await output.WriteLineAsync(Invariant($"... and {result.Revised.Count - MostListed} more revisions; all are in the audit trail."));
        }

        foreach (var close in result.Unusual.Take(MostListed))
        {
            await output.WriteLineAsync(Invariant(
                $"Accepted {close.Ticker} {close.Close.Session:R}: {close.Close.Close.Value:0.######} after {close.Before.Close.Value:0.######} on {close.Before.Session:R}."));
        }

        if (result.Unusual.Count > MostListed)
        {
            await output.WriteLineAsync(Invariant($"... and {result.Unusual.Count - MostListed} more accepted prices; all are in the audit trail."));
        }

        if (result.Behind.Count > 0)
        {
            var behind = result.Behind.Select(b => Invariant($"{b.Ticker} ({b.Last:R})"));
            await output.WriteLineAsync(
                $"Behind the latest session, last close in brackets: {string.Join(", ", behind)}. There is no price to value them with after that.");
        }

        if (result.NotRepeated > 0)
        {
            await output.WriteLineAsync(Invariant($"Note: the export left out {result.NotRepeated} stored price(s); they are kept as they are."));
        }

        if (result.UnknownTickers.Count > 0)
        {
            await output.WriteLineAsync($"Not in the instrument catalogue, skipped: {string.Join(", ", result.UnknownTickers)}.");
        }

        if (result.WithoutPrices.Count > 0)
        {
            await output.WriteLineAsync($"No prices in the export for: {string.Join(", ", result.WithoutPrices)}.");
        }

        return 0;
    }

    /// <summary>A file the owner named is named again: the same export is the one the code is of.</summary>
    private static string Named(string? file) => file is null ? "" : $"\"{file}\" ";

    private static string WithdrawLine(string? file, string code) =>
        $"dotnet run --project src/ITrade.Host -- {Name} {Named(file)}{Withdraw} {code} \"<what you checked>\"";

    private static string Describe(string ticker, ITrade.Domain.FundEvent e) => Invariant(
        $"{ticker} {e.Day:R} {(e.Kind == ITrade.Domain.FundEventKind.Split ? "split" : "dividend")} {e.Value:0.######}");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
