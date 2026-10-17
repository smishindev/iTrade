using System.Globalization;
using System.Xml;
using ITrade.Application.Portfolio;
using ITrade.Broker.IBKR.Flex;
using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Host.Portfolio;

/// <summary>
/// `ITrade.Host reconcile [&lt;file&gt;]`: holds the ledger as it is now against IBKR's statement - of the imported
/// ones the one that reaches furthest, or the file given - and against the split days of the stored prices. Every
/// figure is shown with both numbers; every difference is named. Nothing is recorded, migrated or repaired: a missing
/// line comes in with an import, a line the ledger holds otherwise goes out with a reversal (the owner's step).
/// </summary>
internal static class ReconcileCommand
{
    public const string Name = "reconcile";

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="file">The statement, or null for the imported one that reaches furthest.</param>
    /// <param name="output">Where the report goes.</param>
    public static async Task<int> RunAsync(WebApplication app, string? file, TextWriter output)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ITradeDbContext>();
        // Changes nothing, also not the schema: an older database is named, as `check` names it.
        if ((await db.Database.GetPendingMigrationsAsync()).ToList() is { Count: > 0 } pending)
        {
            await output.WriteLineAsync($"The database lacks {pending.Count} migration(s) of this build; start the Host once (or see `check`), then reconcile.");
            return 1;
        }

        string path, shown;
        if (file is not null)
        {
            // The owner's name of a download can hold the account number: it is not repeated.
            (path, shown) = (ImportInstrumentsCommand.Locate(file, Directory.GetCurrentDirectory()), "the file given");
        }
        else if (await ImportStatementCommand.LatestKeptAsync(db) is { } kept)
        {
            (path, shown) = (Path.Combine(ImportStatementCommand.ArchiveDir(app.Services.GetRequiredService<ITradeOptions>().DataDir), kept), kept);
        }
        else
        {
            await output.WriteLineAsync($"No statement of IBKR has been imported, so there is nothing to reconcile: import one first ({ImportStatementCommand.Name} <file>).");
            return 1;
        }

        FlexStatement statement;
        try
        {
            statement = FlexStatementFile.Read(await File.ReadAllBytesAsync(path));
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or XmlException)
        {
            await output.WriteLineAsync($"Cannot read the statement ({shown}): {e.Message}");
            return 1;
        }

        FlexReconciliation reconciliation;
        IReadOnlyList<string> splits = [];
        try
        {
            reconciliation = await scope.ServiceProvider.GetRequiredService<FlexImport>()
                .ReconcileAsync(statement, await ImportStatementCommand.KnownAccountAsync(db), CancellationToken.None);
            if (await scope.ServiceProvider.GetRequiredService<IHoldingPlaceStore>()
                .FindByNameAsync(FlexImport.HoldingPlaceName, CancellationToken.None) is { } place)
            {
                // Today in New York: the prices can be ahead of the last statement, and so can a split.
                var today = TradeDate.Of(scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant()).Value;
                splits = await scope.ServiceProvider.GetRequiredService<SplitReconciliation>()
                    .AgainstTheLedgerAsync(place.Id, today, CancellationToken.None);
            }
        }
        // The books of the ledger cannot be worked out (an entry out of order, more sold than held).
        catch (InvalidOperationException e)
        {
            await output.WriteLineAsync($"The ledger could not be reconciled: {e.Message}");
            return 1;
        }

        await output.WriteLineAsync(Invariant(
            $"The ledger against IBKR's statement of {reconciliation.Account.Label}, {reconciliation.From:R} to {reconciliation.To:R} ({statement.Shape}), {shown}; what the ledger holds after {reconciliation.To:R} is not compared:"));
        foreach (var figure in reconciliation.Compared)
        {
            await output.WriteLineAsync(Invariant(
                $"{(figure.Agrees ? " " : "!")} {figure.When}, {figure.What}: ledger {Plain(figure.Ledger)}, IBKR {Plain(figure.Ibkr)}"));
        }

        foreach (var why in reconciliation.Differences.Concat(splits))
        {
            await output.WriteLineAsync($"! {why}");
        }

        if (reconciliation.Agrees)
        {
            var totals = reconciliation.Compared.Count(c => c.AllCurrencies);
            await output.WriteLineAsync(totals == 0
                ? "The ledger agrees with IBKR's statement."
                : Invariant($"The ledger agrees with IBKR's statement; {totals} figure(s) are IBKR's total of all currencies, which cannot tell the currencies apart - a statement with rows per currency checks them fully."));
        }
        else
        {
            await output.WriteLineAsync(
                $"The ledger and IBKR's statement differ; nothing was changed. A line the ledger lacks comes in with {ImportStatementCommand.Name}; " +
                $"a line it holds otherwise, or one IBKR does not state, goes out with {ReverseEntryCommand.Name} (the owner's step).");
        }

        await output.WriteLineAsync(splits.Count == 0
            ? "The ledger's splits agree with the split days of the stored prices."
            : "The ledger and the stored prices differ about a split; nothing was changed. Until the ledger holds the split, the value of that fund is wrong.");
        return reconciliation.Agrees && splits.Count == 0 ? 0 : 1;
    }

    private static string Plain(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
