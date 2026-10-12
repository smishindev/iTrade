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
        services.AddScoped<ITransactionStore, TransactionStore>();
        services.AddScoped<TransactionLedger>();
        services.AddScoped<PortfolioBooksReader>();
        services.AddScoped<PortfolioValuation>();
        services.AddScoped<PortfolioPerformance>();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<ITradeOptions>();
            return new IndexFund(options.IndexTicker, 1m - options.DividendWithheld);
        });
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
        path = Locate(path, Directory.GetCurrentDirectory());
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

    /// <summary>
    /// A relative path is looked up in the starting folder and then in each folder above it. `dotnet run
    /// --project src/ITrade.Host` starts the Host in the project folder, while the owner types paths as seen
    /// from the repository root (config/instruments.csv). A path that is found nowhere is returned as given,
    /// so the error names what was typed.
    /// </summary>
    public static string Locate(string path, string startDirectory)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        for (var folder = new DirectoryInfo(startDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }
}
