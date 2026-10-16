using System.Globalization;
using System.Text.Json;
using ITrade.Application.Portfolio;
using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using NodaTime;

namespace ITrade.Host.Portfolio;

/// <summary>
/// `ITrade.Host reverse-entry &lt;id&gt; "&lt;reason&gt;"`: cancels a ledger entry and what hangs on it (the commission of
/// a trade, the tax of a dividend, the other leg of a conversion) by adding their exact opposites, through
/// <see cref="TransactionLedger"/>. Nothing is changed or removed. The owner's step — after a statement says the
/// broker restated a line the ledger holds: the next import then records the line as the broker states it now.
/// </summary>
internal static class ReverseEntryCommand
{
    public const string Name = "reverse-entry";

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="id">The id of the entry, as an import or a report names it.</param>
    /// <param name="reason">Why, in the owner's words; kept with the reversal.</param>
    /// <param name="output">Where the answer goes.</param>
    public static async Task<int> RunAsync(WebApplication app, string id, string reason, TextWriter output)
    {
        if (!Guid.TryParse(id, out var guid) || string.IsNullOrWhiteSpace(reason))
        {
            await output.WriteLineAsync($"Usage: {Name} <entry id> \"<why>\" - the id is the one the import names.");
            return 2;
        }

        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        IReadOnlyList<Transaction> reversals;
        try
        {
            reversals = await scope.ServiceProvider.GetRequiredService<TransactionLedger>()
                .ReverseAsync(new TransactionId(guid), reason.Trim(), CancellationToken.None);
        }
        catch (InvalidOperationException e)
        {
            // No such entry, already reversed, a reversal itself, or one leg of a conversion.
            await output.WriteLineAsync($"Nothing was reversed: {e.Message}");
            return 1;
        }

        var db = scope.ServiceProvider.GetRequiredService<ITradeDbContext>();
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant(),
            Actor = "cli",
            Kind = "ledger.entry-reversed",
            Payload = JsonSerializer.Serialize(new
            {
                entry = guid,
                reason = reason.Trim(),
                reversals = reversals.Select(r => new { id = r.Id.Value, reverses = r.Reverses!.Value.Value }),
            }),
        });
        await db.SaveChangesAsync();

        foreach (var reversal in reversals)
        {
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Reversed {reversal.Reverses}: {reversal.Kind} of {reversal.TradeDate:R}, now {reversal.Cash} and {reversal.QuantityChange:0.######} shares back (entry {reversal.Id})."));
        }

        await output.WriteLineAsync("Import the statement again: a line the broker restated is recorded as it states it now.");
        return 0;
    }
}
