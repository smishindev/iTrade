using ITrade.Application.Portfolio;
using ITrade.Domain;
using ITrade.Host;
using ITrade.Host.Portfolio;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Host;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.IntegrationTests.Portfolio;

/// <summary>P3.1.01 on a real database: the catalogue import is repeatable and renames are found by date.</summary>
[Collection(PostgresTests.Name)]
public sealed class InstrumentTests(PostgresFixture pg)
{
    private static readonly FakeClock Clock = new(Instant.FromUtc(2026, 10, 12, 8, 0));

    private async Task<(ITradeDbContext Db, InstrumentStore Store, InstrumentCatalogue Catalogue)> NewAsync()
    {
        var db = DbSetup.Create(await pg.NewEmptyDatabaseAsync());
        await db.Database.MigrateAsync();
        var store = new InstrumentStore(db, Clock);
        return (db, store, new InstrumentCatalogue(store));
    }

    private static InstrumentListing Listing(string ticker, string exchange, int? conId, string? name = null) =>
        new(new Ticker(ticker), exchange, Currency.Usd, name ?? $"{ticker} fund", null, conId);

    [Fact]
    public async Task TheCatalogueOfTheRepositoryImportsOnceHoweverOftenItIsRun()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var dataDir = RunningHost.NewDataDir();
        var file = Repository.PathTo("config", "instruments.csv");
        using var output = new StringWriter();

        for (var run = 0; run < 2; run++)
        {
            await using var app = ITradeHost.Build(RunningHost.Args(database, dataDir));
            Assert.Equal(0, await ImportInstrumentsCommand.RunAsync(app, file, output));
        }

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("29 listed: 29 added, 0 updated, 0 unchanged, 0 not applied.", lines[0]);
        Assert.Equal("29 listed: 0 added, 0 updated, 29 unchanged, 0 not applied.", lines[1]);
        await using var db = DbSetup.Create(database);
        var instruments = await db.Instruments.AsNoTracking().ToListAsync();
        Assert.Equal(29, instruments.Count);
        Assert.All(instruments, i =>
        {
            Assert.NotNull(i.Isin);
            Assert.NotNull(i.IbkrConId);
            Assert.Equal(Currency.Usd, i.Currency);
        });
        var spy = instruments.Single(i => i.Ticker == new Ticker("SPY")).ToDomain();
        Assert.Equal(("ARCA", "US78462F1030", 756733), (spy.Exchange, spy.Isin?.Value, spy.IbkrConId));
        Assert.Equal(2, await db.AuditEvents.CountAsync(a => a.Kind == "instruments.imported"));
        Directory.Delete(dataDir, recursive: true);
    }

    [Fact]
    public async Task FileWithAnErrorImportsNothing()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var dataDir = RunningHost.NewDataDir();
        var file = Path.Combine(dataDir, "broken.csv");
        await File.WriteAllLinesAsync(file,
        [
            "ticker,exchange,currency,name,isin,ibkr_con_id",
            "SPY,ARCA,USD,SPDR S&P 500 ETF Trust,US78462F1030,756733",
            "QQQ,NASDAQ,USD,Invesco QQQ Trust,US46090E1039,320227571", // wrong check digit
        ]);
        using var output = new StringWriter();

        await using (var app = ITradeHost.Build(RunningHost.Args(database, dataDir)))
        {
            Assert.Equal(1, await ImportInstrumentsCommand.RunAsync(app, file, output));
            Assert.Equal(1, await ImportInstrumentsCommand.RunAsync(app, Path.Combine(dataDir, "missing.csv"), output));
        }

        Assert.Contains("Line 3", output.ToString(), StringComparison.Ordinal);
        await using var db = DbSetup.Create(database);
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync()); // nothing was even started
        Directory.Delete(dataDir, recursive: true);
    }

    /// <summary>
    /// `dotnet run --project src/ITrade.Host -- import-instruments config/instruments.csv` runs in the project
    /// folder, not where the owner typed the command (found on 2026-10-12: the documented command failed).
    /// </summary>
    [Fact]
    public void RelativePathIsFoundFromAFolderBelowTheOneItWasTypedIn()
    {
        var root = Directory.CreateTempSubdirectory("itrade-locate-").FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "src", "ITrade.Host")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var file = Path.Combine(root, "config", "instruments.csv");
        File.WriteAllText(file, "ticker,exchange,currency,name,isin,ibkr_con_id");
        var relative = Path.Combine("config", "instruments.csv");

        Assert.Equal(file, ImportInstrumentsCommand.Locate(relative, project));
        Assert.Equal(file, ImportInstrumentsCommand.Locate(relative, root));
        Assert.Equal(file, ImportInstrumentsCommand.Locate(file, project));
        Assert.Equal(Path.Combine("config", "missing.csv"), ImportInstrumentsCommand.Locate(Path.Combine("config", "missing.csv"), project));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task InstrumentIsFoundUnderTheTickerItHadOnTheDate()
    {
        var (db, store, catalogue) = await NewAsync();
        await using var _ = db;
        await catalogue.ImportAsync([Listing("FB", "NASDAQ", 107113386, "Meta Platforms")], CancellationToken.None);
        var id = (await store.FindByTickerAsync(new Ticker("FB"), "nasdaq", CancellationToken.None))!.Id;
        var renamed = new LocalDate(2022, 6, 9);
        var again = new LocalDate(2025, 2, 3);

        await catalogue.RenameAsync(id, new Ticker("META"), renamed, CancellationToken.None);
        await catalogue.RenameAsync(id, new Ticker("MVRS"), again, CancellationToken.None);

        async Task<InstrumentId?> OnAsync(string ticker, LocalDate day) =>
            (await store.FindByTickerOnAsync(new Ticker(ticker), "NASDAQ", day, CancellationToken.None))?.Id;

        Assert.Equal(id, await OnAsync("FB", new LocalDate(2012, 5, 18)));
        Assert.Equal(id, await OnAsync("FB", renamed.PlusDays(-1)));
        Assert.Null(await OnAsync("FB", renamed));
        Assert.Null(await OnAsync("META", renamed.PlusDays(-1)));
        Assert.Equal(id, await OnAsync("META", renamed));
        Assert.Equal(id, await OnAsync("META", again.PlusDays(-1)));
        Assert.Null(await OnAsync("META", again));
        Assert.Equal(id, await OnAsync("MVRS", again));
        Assert.Null(await store.FindByTickerOnAsync(new Ticker("FB"), "ARCA", new LocalDate(2012, 5, 18), CancellationToken.None));

        var history = await store.TickersOfAsync(id, CancellationToken.None);
        Assert.Equal(new Ticker("MVRS"), history!.Current);
        Assert.Equal([new FormerTicker(new Ticker("FB"), renamed), new FormerTicker(new Ticker("META"), again)], history.Former);
        Assert.Equal(new Ticker("MVRS"), (await store.FindByConIdAsync(107113386, CancellationToken.None))!.Ticker);
        Assert.Equal(2, await db.AuditEvents.CountAsync(a => a.Kind == "instrument.renamed"));
    }

    [Fact]
    public async Task TickerGivenUpByOneFundAndTakenByAnotherBelongsToEachInItsOwnTime()
    {
        var (db, store, catalogue) = await NewAsync();
        await using var _ = db;
        await catalogue.ImportAsync([Listing("OLD", "ARCA", 1, "First fund")], CancellationToken.None);
        var first = (await store.FindByTickerAsync(new Ticker("OLD"), "ARCA", CancellationToken.None))!.Id;
        var released = new LocalDate(2020, 1, 2);
        await catalogue.RenameAsync(first, new Ticker("NEW"), released, CancellationToken.None);
        // Later another fund starts trading under the ticker the first one gave up.
        var result = await catalogue.ImportAsync([Listing("OLD", "ARCA", 2, "Second fund")], CancellationToken.None);
        var second = (await store.FindByTickerAsync(new Ticker("OLD"), "ARCA", CancellationToken.None))!.Id;

        Assert.Equal((1, 0), (result.Added, result.Conflicts.Count));
        Assert.NotEqual(first, second);
        Assert.Equal(first, (await store.FindByTickerOnAsync(new Ticker("OLD"), "ARCA", released.PlusDays(-1), CancellationToken.None))!.Id);
        Assert.Equal(second, (await store.FindByTickerOnAsync(new Ticker("OLD"), "ARCA", released, CancellationToken.None))!.Id);
        Assert.Equal(first, (await store.FindByTickerOnAsync(new Ticker("NEW"), "ARCA", released, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task RenameThatBreaksTheRulesChangesNothing()
    {
        var (db, store, catalogue) = await NewAsync();
        await using var _ = db;
        await catalogue.ImportAsync([Listing("AAA", "ARCA", 1), Listing("BBB", "ARCA", 2)], CancellationToken.None);
        var aaa = (await store.FindByTickerAsync(new Ticker("AAA"), "ARCA", CancellationToken.None))!.Id;
        var day = new LocalDate(2026, 3, 2);
        await catalogue.RenameAsync(aaa, new Ticker("AAB"), day, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalogue.RenameAsync(aaa, new Ticker("BBB"), day.PlusDays(10), CancellationToken.None)); // taken
        await Assert.ThrowsAsync<ArgumentException>(() =>
            catalogue.RenameAsync(aaa, new Ticker("AAC"), day, CancellationToken.None)); // not after the previous one
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RenameAsync(aaa, new Ticker("AAC"), day.PlusDays(-5), CancellationToken.None)); // the store checks too

        db.ChangeTracker.Clear();
        var history = await store.TickersOfAsync(aaa, CancellationToken.None);
        Assert.Equal(new Ticker("AAB"), history!.Current);
        Assert.Equal([new FormerTicker(new Ticker("AAA"), day)], history.Former);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Kind == "instrument.renamed"));
    }

    [Fact]
    public async Task HoldingPlacesAreKeptByName()
    {
        var (db, _, _) = await NewAsync();
        await using var disposal = db;
        var places = new HoldingPlaceStore(db);
        var ibkr = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var keren = new HoldingPlace(HoldingPlaceId.New(), "Keren hishtalmut", HoldingPlaceKind.StudyFund, IntegrationKind.Manual);

        await places.AddAsync(keren, CancellationToken.None);
        await places.AddAsync(ibkr, CancellationToken.None);

        Assert.Equal([ibkr, keren], await places.ListAsync(CancellationToken.None));
        Assert.Equal(keren, await places.FindByNameAsync(" Keren hishtalmut ", CancellationToken.None));
        Assert.Null(await places.FindByNameAsync("Another", CancellationToken.None));
    }
}
