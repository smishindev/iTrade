using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using ITrade.Application.Portfolio;
using ITrade.Broker.IBKR.Flex;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Host.Portfolio;

/// <summary>
/// `ITrade.Host import-statement &lt;file&gt;`: records an IBKR Activity Flex statement (XML, downloaded from the
/// portal or by `fetch-statement`) in the ledger. Running it again with the same or an overlapping statement
/// adds nothing that is already there. A statement that holds anything the import cannot read with certainty, or
/// that does not agree with the ledger at its start and its end, is recorded not at all, and the reasons are
/// listed. The file is kept in the data folder under its SHA-256 before anything is recorded.
/// </summary>
internal static class ImportStatementCommand
{
    public const string Name = "import-statement";
    public const string AuditKind = "statement.imported";

    /// <summary>Where the statements that were imported are kept: `&lt;data&gt;/statements/flex`.</summary>
    public static string ArchiveDir(string dataDir) => Path.GetFullPath(Path.Combine(dataDir, "statements", "flex"));

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="file">The statement.</param>
    /// <param name="output">Where the summary goes.</param>
    public static async Task<int> RunAsync(WebApplication app, string file, TextWriter output)
    {
        var path = ImportInstrumentsCommand.Locate(file, Directory.GetCurrentDirectory());
        byte[] content;
        FlexStatement statement;
        try
        {
            content = await File.ReadAllBytesAsync(path);
            statement = FlexStatementFile.Read(content);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or XmlException)
        {
            await output.WriteLineAsync($"Cannot read the statement {file}: {e.Message}");
            return 1;
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ITradeDbContext>();
        var import = scope.ServiceProvider.GetRequiredService<FlexImport>();
        FlexImportPlan plan;
        FlexRecorded recorded;
        string archived;
        try
        {
            plan = await import.PrepareAsync(statement, await KnownAccountAsync(db), CancellationToken.None);
            if (!plan.CanBeRecorded)
            {
                await output.WriteLineAsync(Invariant($"Nothing was recorded from the statement of {plan.Account.Label}, {plan.From:R} to {plan.To:R}:"));
                foreach (var why in plan.Refusals)
                {
                    await output.WriteLineAsync($"! {why}");
                }

                if (plan.Refusals.Any(r => r.Contains("reverse the entry", StringComparison.Ordinal)))
                {
                    await output.WriteLineAsync(
                        $"If IBKR restated the line, reverse the old entry (the owner's step): dotnet run --project src/ITrade.Host -- {ReverseEntryCommand.Name} <entry id> \"<why>\" - then import again.");
                }

                return 1;
            }

            // Kept before anything is recorded: an entry of the ledger always has its statement beside it.
            var options = app.Services.GetRequiredService<ITradeOptions>();
            archived = Path.Combine(ArchiveDir(options.DataDir), Invariant($"{plan.To:uuuuMMdd}-{sha256[..12]}.xml"));
            if (!File.Exists(archived))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
                await File.WriteAllBytesAsync(archived, content);
            }

            recorded = await import.RecordAsync(plan, CancellationToken.None);
        }
        // Entries that break the ledger's own rules, the same lines recorded by another run a moment ago, or a
        // data folder that cannot be written.
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or DbUpdateException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"The statement was not recorded: {e.Message}");
            return 1;
        }

        var ledger = recorded.Ledger;
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant(),
            Actor = "cli",
            Kind = AuditKind,
            // The kept copy's name, not the file's: the owner may have named a download after the account.
            Payload = JsonSerializer.Serialize(new
            {
                source = "flex",
                kept = Path.GetFileName(archived),
                sha256,
                accountKey = plan.Account.Key,
                account = plan.Account.Label,
                from = Invariant($"{plan.From:R}"),
                to = Invariant($"{plan.To:R}"),
                generated = Invariant($"{statement.Generated:uuuu-MM-dd HH:mm:ss}"),
                entries = plan.Entries.Count,
                added = ledger.Added,
                alreadyRecorded = ledger.AlreadyRecorded,
                notRecorded = ledger.NotRecorded.Count,
                daysNotCheckedPerCurrency = plan.DaysNotChecked.Count,
                periodsCheckedOnlyAsAWhole = plan.CheckedOnlyAsAWhole.Count,
                disagreementsAfterRecording = recorded.Disagreements.Count,
            }),
        });
        await db.SaveChangesAsync();

        await output.WriteLineAsync(Invariant(
            $"Statement of {plan.Account.Label}, {plan.From:R} to {plan.To:R} ({statement.Shape}): {plan.Entries.Count} entries; {ledger.Added} recorded, {ledger.AlreadyRecorded} already in the ledger, {ledger.NotRecorded.Count} left out."));
        if (recorded.Disagreements.Count == 0)
        {
            var partly = new List<string>();
            if (plan.DaysNotChecked.Count > 0)
            {
                partly.Add(Invariant($"{plan.DaysNotChecked.Count} period(s) IBKR reported only in the base currency were not checked one by one"));
            }

            if (plan.CheckedOnlyAsAWhole.Count > 0)
            {
                partly.Add(Invariant($"{plan.CheckedOnlyAsAWhole.Count} period(s) of several days were checked only as a whole, not day by day (turn on Breakout by Day in the Flex query)"));
            }

            await output.WriteLineAsync(
                "The ledger agrees with IBKR: the cash at the start and the end of the statement, and the shares at its end" +
                (partly.Count == 0 ? "; the cash of every day too." : $"; {string.Join("; ", partly)}."));
        }
        else
        {
            await output.WriteLineAsync("After recording, the ledger does not agree with IBKR:");
            foreach (var why in recorded.Disagreements)
            {
                await output.WriteLineAsync($"! {why}");
            }
        }

        foreach (var why in ledger.NotRecorded)
        {
            await output.WriteLineAsync($"! {why}");
        }

        await output.WriteLineAsync($"Kept as {archived}.");
        return ledger.NotRecorded.Count == 0 && recorded.Disagreements.Count == 0 ? 0 : 1;
    }

    /// <summary>The account of the latest statement imported, or null before the first one.</summary>
    internal static async Task<FlexAccount?> KnownAccountAsync(ITradeDbContext db)
    {
        var last = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Kind == AuditKind)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Select(e => e.Payload)
            .FirstOrDefaultAsync();
        if (last is null)
        {
            return null;
        }

        using var payload = JsonDocument.Parse(last);
        var root = payload.RootElement;
        return root.TryGetProperty("accountKey", out var key) && root.TryGetProperty("account", out var label)
            ? new FlexAccount(key.GetString()!, label.GetString()!)
            : throw new InvalidOperationException(
                "The latest statement.imported event names no account key; the account of the earlier statements cannot be told.");
    }

    /// <summary>
    /// The name of the kept copy of the imported statement that reaches furthest (the latest last day; of two, the
    /// one imported later), or null before the first import. Not simply the last import: an older statement imported
    /// again afterwards would hide every day after its own.
    /// </summary>
    internal static async Task<string?> LatestKeptAsync(ITradeDbContext db)
    {
        var payloads = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Kind == AuditKind)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id) // a v7 id grows with time: of two at the same instant, the later one
            .Select(e => e.Payload)
            .ToListAsync();
        string? latest = null;
        var reaches = "";
        foreach (var text in payloads)
        {
            using var payload = JsonDocument.Parse(text);
            var root = payload.RootElement;
            // Dates as yyyy-MM-dd compare as text; the first of equal ones is the later import.
            if (root.TryGetProperty("kept", out var kept) && root.TryGetProperty("to", out var to)
                && string.CompareOrdinal(to.GetString(), reaches) > 0)
            {
                (latest, reaches) = (kept.GetString(), to.GetString()!);
            }
        }

        return latest;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
