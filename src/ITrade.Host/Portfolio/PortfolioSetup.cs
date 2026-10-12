using System.Text.Json;
using ITrade.Application.Portfolio;
using ITrade.Infrastructure.Persistence;
using ITrade.Infrastructure.Portfolio;
using NodaTime;

namespace ITrade.Host.Portfolio;

internal static class PortfolioSetup
{
    public static void AddPortfolio(this IServiceCollection services)
    {
        services.AddScoped<IInstrumentStore, InstrumentStore>();
        services.AddScoped<IHoldingPlaceStore, HoldingPlaceStore>();
        services.AddScoped<InstrumentCatalogue>();
    }
}

/// <summary>
/// `ITrade.Host import-instruments &lt;file&gt;`: reads the instrument catalogue (config/instruments.csv) into the
/// database. Running it again with the same file changes nothing; a listing that contradicts what is stored is
/// reported and left alone.
/// </summary>
internal static class ImportInstrumentsCommand
{
    public const string Name = "import-instruments";

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="path">The catalogue file.</param>
    /// <param name="output">Where the summary goes.</param>
    public static async Task<int> RunAsync(WebApplication app, string path, TextWriter output)
    {
        IReadOnlyList<InstrumentListing> listings;
        try
        {
            listings = InstrumentCatalogueFile.ReadFile(path);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Cannot read {path}: {e.Message}");
            return 1;
        }

        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<InstrumentCatalogue>()
            .ImportAsync(listings, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<ITradeDbContext>();
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant(),
            Actor = "cli",
            Kind = "instruments.imported",
            Payload = JsonSerializer.Serialize(new
            {
                file = Path.GetFileName(path),
                listed = listings.Count,
                added = result.Added,
                updated = result.Updated,
                unchanged = result.Unchanged,
                conflicts = result.Conflicts.Count,
            }),
        });
        await db.SaveChangesAsync();

        await output.WriteLineAsync(
            $"{listings.Count} listed: {result.Added} added, {result.Updated} updated, {result.Unchanged} unchanged, " +
            $"{result.Conflicts.Count} not applied.");
        foreach (var conflict in result.Conflicts)
        {
            await output.WriteLineAsync($"! {conflict}");
        }

        return result.Conflicts.Count == 0 ? 0 : 1;
    }
}
