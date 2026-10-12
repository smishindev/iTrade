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
        Assert.Contains("transactions", tables);
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

    /// <summary>A database with one holding place and one instrument, and a buy with its fee in the ledger.</summary>
    private async Task<(string Database, Transaction Buy, Transaction Fee)> LedgerWithABuyAsync()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var db = DbSetup.Create(database);
        await db.Database.MigrateAsync();
        var place = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var voo = Etf("VOO", 136155102);
        db.HoldingPlaces.Add(HoldingPlaceRow.From(place));
        db.Instruments.Add(InstrumentRow.From(voo));
        var traded = new LocalDate(2026, 10, 12);
        var buy = new Transaction(
            TransactionId.New(), place.Id, TransactionKind.Buy, traded, traded.PlusDays(1), Money.Usd(-1203.505m),
            TransactionSource.Flex, At, voo.Id, 2.000001m, new Price(601.752499m, Currency.Usd), "T-1001");
        var fee = new Transaction(
            TransactionId.New(), place.Id, TransactionKind.Fee, traded, traded.PlusDays(1), Money.Usd(-0.350753m),
            TransactionSource.Flex, At, voo.Id, externalId: "T-1001:fee", relatedTo: buy.Id);
        db.Transactions.AddRange(TransactionRow.From(buy), TransactionRow.From(fee));
        await db.SaveChangesAsync();
        return (database, buy, fee);
    }

    [Fact]
    public async Task TransactionRoundTripsExactlyWithItsLinks()
    {
        var (database, buy, fee) = await LedgerWithABuyAsync();

        await using var db = DbSetup.Create(database);
        var rows = await db.Transactions.AsNoTracking().OrderBy(t => t.Kind).ToListAsync();

        Assert.Equal([buy, fee], rows.Select(r => r.ToDomain()));
        Assert.Equal(-1203.505m, rows[0].CashAmount);
        Assert.Equal(2.000001m, rows[0].QuantityChange);
        Assert.Equal(601.752499m, rows[0].Price);
        Assert.Equal(buy.Id, rows[1].RelatedTo);
    }

    [Fact]
    public async Task LedgerAcceptsNewEntriesButRejectsUpdateDeleteTruncate()
    {
        var (database, buy, _) = await LedgerWithABuyAsync();
        await using var conn = new NpgsqlConnection(database);
        await conn.OpenAsync();

        foreach (var sql in new[]
                 {
                     $"UPDATE transactions SET cash_amount = -1 WHERE id = '{buy.Id}'",
                     $"DELETE FROM transactions WHERE related_to = '{buy.Id}'",
                     "TRUNCATE transactions",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            Assert.Contains("record a reversal instead", error.MessageText, StringComparison.Ordinal);
        }

        await using var db = DbSetup.Create(database);
        Assert.Equal(2, await db.Transactions.CountAsync());
        Assert.Equal(-1203.505m, (await db.Transactions.SingleAsync(t => t.Id == buy.Id)).CashAmount);
    }

    [Fact]
    public async Task DatabaseRefusesASecondCopyASecondReversalAndBrokenReferences()
    {
        var (database, buy, fee) = await LedgerWithABuyAsync();

        async Task<string?> SqlStateOfAsync(Transaction entry)
        {
            await using var db = DbSetup.Create(database);
            db.Transactions.Add(TransactionRow.From(entry));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            return (error.InnerException as PostgresException)?.SqlState;
        }

        // The same external id of the same source at the same place: a second copy of a statement line.
        var copy = new Transaction(
            TransactionId.New(), buy.HoldingPlace, TransactionKind.Fee, buy.TradeDate, buy.SettlementDate, Money.Usd(-1m),
            TransactionSource.Flex, At, externalId: "T-1001");
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateOfAsync(copy));

        // One reversal is fine; a second one of the same entry is not.
        await using (var db = DbSetup.Create(database))
        {
            db.Transactions.Add(TransactionRow.From(fee.Reversal(TransactionId.New(), At, "charged twice")));
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            await SqlStateOfAsync(fee.Reversal(TransactionId.New(), At, "charged twice, again")));

        // References must exist, and what the ledger points at cannot be removed.
        var orphan = new Transaction(
            TransactionId.New(), HoldingPlaceId.New(), TransactionKind.Deposit, buy.TradeDate, buy.TradeDate, Money.Usd(10m),
            TransactionSource.Manual, At);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await SqlStateOfAsync(orphan));
        await using var conn = new NpgsqlConnection(database);
        await conn.OpenAsync();
        foreach (var sql in new[] { "DELETE FROM instruments", "DELETE FROM holding_places" })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task DatabaseItselfRefusesAnEntryThatSettlesBeforeItTradesOrChangesNothing()
    {
        var (database, buy, _) = await LedgerWithABuyAsync();
        await using var conn = new NpgsqlConnection(database);
        await conn.OpenAsync();
        const string insert =
            "INSERT INTO transactions (id, holding_place_id, kind, trade_date, settlement_date, quantity_change, " +
            "cash_amount, cash_currency, source, recorded_at) VALUES (gen_random_uuid(), '{0}', 'Deposit', {1}, 'USD', 'Manual', now())";

        foreach (var values in new[] { "'2026-10-12', '2026-10-09', 0, 10", "'2026-10-12', '2026-10-12', 0, 0" })
        {
            await using var cmd = new NpgsqlCommand(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, buy.HoldingPlace, values), conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
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
