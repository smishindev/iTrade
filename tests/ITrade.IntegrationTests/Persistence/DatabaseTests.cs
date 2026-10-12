using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace ITrade.IntegrationTests.Persistence;

/// <summary>P2.3.02–P2.3.04: migrations on a clean PostgreSQL 18, append-only audit journal, model conventions.</summary>
[Collection(PostgresTests.Name)]
public sealed class DatabaseTests(PostgresFixture pg)
{
    private static readonly Instant At = Instant.FromUtc(2026, 10, 9, 5, 0);

    [Fact]
    public async Task MigrationsCreateTheTables()
    {
        await using var db = pg.NewContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();
        Assert.Contains("settings", tables);
        Assert.Contains("audit_events", tables);
        Assert.Contains("job_runs", tables);
        Assert.DoesNotContain("jobs", tables);
        Assert.Contains("instruments", tables);
        Assert.Contains("instrument_former_tickers", tables);
        Assert.Contains("holding_places", tables);
    }

    [Fact]
    public async Task AuditJournalAcceptsInsertsButRejectsUpdateDeleteTruncate()
    {
        var id = Guid.CreateVersion7();
        await using (var db = pg.NewContext())
        {
            db.AuditEvents.Add(new AuditEvent { Id = id, OccurredAt = At, Actor = "test", Kind = "probe", Payload = "{}" });
            await db.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     $"UPDATE audit_events SET kind = 'changed' WHERE id = '{id}'",
                     $"DELETE FROM audit_events WHERE id = '{id}'",
                     "TRUNCATE audit_events",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
        }

        await using var check = pg.NewContext();
        var kept = await check.AuditEvents.SingleAsync(a => a.Id == id);
        Assert.Equal("probe", kept.Kind);
    }

    [Fact]
    public async Task InstantsRoundTripAsUtc()
    {
        await using (var db = pg.NewContext())
        {
            db.Settings.Add(new Setting { Key = "probe", Value = "{\"a\":1}", UpdatedAt = At });
            await db.SaveChangesAsync();
        }

        await using var read = pg.NewContext();
        Assert.Equal(At, (await read.Settings.SingleAsync(s => s.Key == "probe")).UpdatedAt);
    }

    private async Task<ITradeDbContext> NewMigratedDatabaseAsync()
    {
        var db = DbSetup.Create(await pg.NewEmptyDatabaseAsync());
        await db.Database.MigrateAsync();
        return db;
    }

    private static Instrument Etf(string ticker, int? conId, string exchange = "ARCA") =>
        new(InstrumentId.New(), new Ticker(ticker), exchange, Currency.Usd, $"{ticker} fund", ibkrConId: conId);

    [Fact]
    public async Task InstrumentRoundTripsWithItsValueTypes()
    {
        await using var db = await NewMigratedDatabaseAsync();
        var voo = new Instrument(
            InstrumentId.New(), new Ticker("VOO"), "ARCA", Currency.Usd, "Vanguard S&P 500 ETF",
            new Isin("US9229083632"), 136155102);
        db.Instruments.Add(InstrumentRow.From(voo));
        db.FormerTickers.Add(new FormerTickerRow
        {
            InstrumentId = voo.Id,
            Ticker = new Ticker("OLD"),
            Until = new LocalDate(2020, 1, 2),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(voo, (await db.Instruments.SingleAsync()).ToDomain());
        var former = await db.FormerTickers.SingleAsync();
        Assert.Equal((voo.Id, new Ticker("OLD"), new LocalDate(2020, 1, 2)), (former.InstrumentId, former.Ticker, former.Until));
    }

    [Fact]
    public async Task DatabaseRefusesASecondInstrumentWithTheSameContractOrTheSameTickerOnAnExchange()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var db = DbSetup.Create(database);
        await db.Database.MigrateAsync();
        db.Instruments.AddRange(InstrumentRow.From(Etf("AAA", 1001)), InstrumentRow.From(Etf("BBB", null)), InstrumentRow.From(Etf("CCC", null)));
        await db.SaveChangesAsync();

        async Task<string?> SqlStateOfAsync(Instrument duplicate)
        {
            await using var other = DbSetup.Create(database);
            other.Instruments.Add(InstrumentRow.From(duplicate));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
            return (error.InnerException as PostgresException)?.SqlState;
        }

        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateOfAsync(Etf("ZZZ", 1001)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateOfAsync(Etf("AAA", 2002)));
        // The same ticker on another exchange is another instrument.
        await using var third = DbSetup.Create(database);
        third.Instruments.Add(InstrumentRow.From(Etf("AAA", 3003, "LSEETF")));
        await third.SaveChangesAsync();
    }

    [Fact]
    public async Task DatabaseItselfRefusesAutomaticReadingOfAPlaceThatIsNotABroker()
    {
        await using var db = await NewMigratedDatabaseAsync();
        db.HoldingPlaces.Add(HoldingPlaceRow.From(
            new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker)));
        db.HoldingPlaces.Add(HoldingPlaceRow.From(
            new HoldingPlace(HoldingPlaceId.New(), "Keren hishtalmut", HoldingPlaceKind.StudyFund, IntegrationKind.Manual)));
        await db.SaveChangesAsync();

        // Past the domain type, straight into the table.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO holding_places (id, name, kind, integration) VALUES (gen_random_uuid(), 'Fund', 'StudyFund', 'Broker')"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        var duplicateName = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO holding_places (id, name, kind, integration) VALUES (gen_random_uuid(), 'IBKR', 'Broker', 'Manual')"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateName.SqlState);

        db.ChangeTracker.Clear();
        var places = await db.HoldingPlaces.OrderBy(h => h.Name).ToListAsync();
        Assert.Equal(["IBKR", "Keren hishtalmut"], places.Select(p => p.ToDomain().Name));
    }

    [Fact]
    public void ModelHasNoFloatingPointMoneyAndNoDateTime()
    {
        using var db = pg.NewContext();
        var properties = db.Model.GetEntityTypes().SelectMany(e => e.GetProperties()).ToList();
        var forbidden = new[] { typeof(double), typeof(float), typeof(DateTime), typeof(DateTimeOffset) };
        var bad = properties
            .Where(p => forbidden.Contains(Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType))
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}: {p.ClrType.Name}")
            .ToList();
        Assert.True(bad.Count == 0, string.Join(", ", bad));
        var decimals = properties.Where(p => p.GetProviderClrType() == typeof(decimal) || p.ClrType == typeof(decimal));
        Assert.All(decimals, p => Assert.NotNull(p.GetPrecision()));
    }
}
