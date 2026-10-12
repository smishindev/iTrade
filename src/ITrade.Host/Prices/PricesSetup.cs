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
/// `ITrade.Host import-prices [file] [--accept-changes CODE]`: loads the closing prices the data pipeline
/// exported (`itrade ingest --universe etf_pullback_v1`, or `itrade export-closes`) and the exchange's sessions they cover into
/// the database. Without a file it takes the export from the data folder. Running it again adds nothing. A
/// few prices the source corrected by a little are recorded as revisions and listed; many or large changes,
/// and a new price far from the one before it, stop the import until the owner accepts them by their code.
/// </summary>
internal static class ImportPricesCommand
{
    public const string Name = "import-prices";
    public const string AcceptChanges = "--accept-changes";
    private const int MostListed = 20;

    /// <summary>
    /// Splits the command's own arguments from the settings of the Host: an optional file, and after
    /// `--accept-changes` the code. A flag without its code comes back as an empty code, and
    /// <see cref="RunAsync"/> then explains the usage.
    /// </summary>
    public static (string? File, string? AcceptedCode, string[] HostArgs) Parse(string[] args)
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

        return (file, code, [.. rest]);
    }

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="file">The export's table, or null for the one in the data folder.</param>
    /// <param name="acceptedCode">The code of a finding the owner accepts; null for a plain import.</param>
    /// <param name="output">Where the summary goes.</param>
    public static async Task<int> RunAsync(WebApplication app, string? file, string? acceptedCode, TextWriter output)
    {
        if (acceptedCode is not null && string.IsNullOrWhiteSpace(acceptedCode))
        {
            await output.WriteLineAsync($"Usage: {Name} [file] {AcceptChanges} <code> - the code is shown with the finding.");
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
                .ImportAsync(export, acceptedCode, CancellationToken.None);
        }
        catch (PricesDisagreeException e)
        {
            await output.WriteLineAsync($"The closing prices were not imported: {e.Message}");
            // The same export is the one that is accepted: a file the owner named is named again.
            var named = file is null ? "" : $"\"{file}\" ";
            await output.WriteLineAsync(
                $"To accept exactly this: dotnet run --project src/ITrade.Host -- {Name} {named}{AcceptChanges} {e.Code}");
            return 1;
        }
        // An export that contradicts itself, the catalogue or what is stored; or the same prices stored by
        // another run a moment ago.
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or DbUpdateException)
        {
            await output.WriteLineAsync($"The closing prices were not imported: {e.Message}");
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
            var examples = result.EventsNotRepeated.Take(3).Select(e => Invariant(
                $"{e.Ticker} {e.Event.Day:R} {(e.Event.Kind == ITrade.Domain.FundEventKind.Split ? "split" : "dividend")} {e.Event.Value:0.######}"));
            await output.WriteLineAsync(Invariant(
                $"Note: the export no longer states {result.EventsNotRepeated.Count} stored dividend(s) or split(s), for example {string.Join("; ", examples)}. They are kept and an index is still followed with them: if the source moved or withdrew them, the comparison is off by that much."));
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

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
