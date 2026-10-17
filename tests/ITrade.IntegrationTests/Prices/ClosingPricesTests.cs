using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ITrade.Application.Prices;
using ITrade.Domain;
using ITrade.Host;
using ITrade.Host.Auth;
using ITrade.Host.Prices;
using ITrade.Infrastructure.Persistence;
using ITrade.Infrastructure.Prices;
using ITrade.IntegrationTests.Host;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace ITrade.IntegrationTests.Prices;

/// <summary>
/// P3.3.02 on a real database and the real Host: closing prices and the sessions they belong to are stored
/// exactly and only ever added, the command loads the pipeline's export and says what it did, and `check`
/// shows how old the prices are and who is behind.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class ClosingPricesTests(PostgresFixture pg)
{
    private const string SessionsName = "closing-prices-sessions.csv";
    private const string EventsName = "closing-prices-events.csv";
    private static readonly Instant Imported = Instant.FromUtc(2026, 10, 7, 6, 0);
    private static readonly PricesNote Note = new("test", PricesNote.Imported, "{}");
    private static readonly ImportedExport Stamp = new(Instant.FromUtc(2026, 10, 7, 5, 0), new string('a', 64));

    private static LocalDate Day(int day) => new(2026, 10, day);

    private static ClosingPrice Close(Instrument instrument, int day, decimal close) =>
        new(instrument.Id, Day(day), new Price(close, instrument.Currency), "yfinance");

    /// <summary>Stores closes, revisions and the sessions of these days of October that are new.</summary>
    private static Task AddAsync(ClosingPriceStore store, ClosingPrice[] added, ClosingPrice[] revised, params int[] newSessions) =>
        store.AddAsync(
            new PricesToStore(added, revised, ClosingPrices.Calendar, [.. newSessions.Select(Day)], [], []), Imported, Note, Stamp,
            CancellationToken.None);

    private static Task AddEventsAsync(ClosingPriceStore store, FundEvent[] events, FundEvent[] revised, params int[] newSessions) =>
        store.AddAsync(
            new PricesToStore([], [], ClosingPrices.Calendar, [.. newSessions.Select(Day)], events, revised), Imported, Note, Stamp,
            CancellationToken.None);

    private static FundEvent Dividend(Instrument instrument, int day, decimal perShare) =>
        new(instrument.Id, Day(day), FundEventKind.Dividend, perShare, "yfinance");

    private static FundEvent Split(Instrument instrument, int day, decimal ratio) =>
        new(instrument.Id, Day(day), FundEventKind.Split, ratio, "yfinance");

    private sealed record World(string Database, Instrument Spy, Instrument Xlk);

    private static Instrument Fund(string ticker) => new(InstrumentId.New(), new Ticker(ticker), "ARCA", Currency.Usd, $"{ticker} fund");

    private async Task<World> NewAsync()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var db = DbSetup.Create(database);
        await db.Database.MigrateAsync();
        var (spy, xlk) = (Fund("SPY"), Fund("XLK"));
        db.Instruments.AddRange(InstrumentRow.From(spy), InstrumentRow.From(xlk));
        await db.SaveChangesAsync();
        return new World(database, spy, xlk);
    }

    /// <summary>
    /// Writes an export the way the pipeline does: the two tables, then the manifest with their SHA-256. Every
    /// ticker is fetched when the export is written.
    /// </summary>
    private static string WriteExport(
        string folder, string exportedAt, LocalDate[] sessions, string[] lines, string? splitOf = null, string[]? events = null)
    {
        events ??= [];
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Directory.CreateDirectory(folder);
        var table = Encoding.UTF8.GetBytes(ClosingPricesFile.Header + "\n" + string.Join('\n', lines) + "\n");
        var days = Encoding.UTF8.GetBytes(
            ClosingPricesFile.SessionsHeader + "\n" + string.Join('\n', sessions.Select(s => s.ToString("R", CultureInfo.InvariantCulture))) + "\n");
        var path = Path.Combine(folder, ClosingPricesFile.CsvName);
        File.WriteAllBytes(path, table);
        File.WriteAllBytes(Path.Combine(folder, SessionsName), days);
        var happened = Encoding.UTF8.GetBytes(ClosingPricesFile.EventsHeader + "\n" + string.Join('\n', events) + (events.Length > 0 ? "\n" : ""));
        File.WriteAllBytes(Path.Combine(folder, EventsName), happened);
        File.WriteAllText(Path.Combine(folder, ClosingPricesFile.ManifestName), JsonSerializer.Serialize(new
        {
            file = ClosingPricesFile.CsvName,
            sha256 = Hash(table),
            rows = lines.Length,
            source = "yfinance",
            calendar = "XNYS",
            sessions_file = SessionsName,
            sessions_sha256 = Hash(days),
            sessions = sessions.Length,
            events_file = EventsName,
            events_sha256 = Hash(happened),
            events = events.Length,
            exported_at = exportedAt,
            tickers = lines.GroupBy(l => l.Split(',')[0]).ToDictionary(g => g.Key, g => new
            {
                rows = g.Count(),
                fetched_at = exportedAt,
                currency = "USD",
                splits = g.Where(l => l.StartsWith(splitOf ?? "-", StringComparison.Ordinal)).ToDictionary(l => l.Split(',')[1], _ => 2.0),
            }),
        }));
        return path;
    }

    [Fact]
    public async Task PricesAreStoredExactlyAndTheLatestRevisionIsTheOneInUse()
    {
        var w = await NewAsync();
        await using var db = DbSetup.Create(w.Database);
        var store = new ClosingPriceStore(db);

        await AddAsync(store, [Close(w.Spy, 2, 769.64m), Close(w.Spy, 5, 774.83m), Close(w.Xlk, 2, 107.6875m)], [], 2, 5);
        await AddAsync(store, [Close(w.Spy, 6, 779.09m)], [Close(w.Spy, 5, 774.38m)], 6);
        await AddAsync(store, [], [Close(w.Spy, 5, 774.40m)]);

        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(
            [Close(w.Spy, 2, 769.64m), Close(w.Spy, 5, 774.40m), Close(w.Spy, 6, 779.09m)],
            await store.ListAsync(w.Spy.Id, CancellationToken.None));
        Assert.Equal(Close(w.Spy, 5, 774.40m), await store.FindAsync(w.Spy.Id, Day(5), CancellationToken.None));
        Assert.Equal(Close(w.Xlk, 2, 107.6875m), await store.FindAsync(w.Xlk.Id, Day(2), CancellationToken.None)); // a sixteenth, exact
        Assert.Null(await store.FindAsync(w.Xlk.Id, Day(5), CancellationToken.None));
        Assert.Equal(Close(w.Spy, 5, 774.40m), await store.LatestBeforeAsync(w.Spy.Id, Day(6), CancellationToken.None));
        Assert.Null(await store.LatestBeforeAsync(w.Spy.Id, Day(2), CancellationToken.None));
        var latest = await store.LatestPerInstrumentAsync(CancellationToken.None);
        Assert.Equal((2, Day(6), Day(2)), (latest.Count, latest[w.Spy.Id], latest[w.Xlk.Id]));
        var revisions = await db.ClosingPrices.AsNoTracking().Where(p => p.Session == Day(5)).OrderBy(p => p.Revision).ToListAsync();
        Assert.Equal([(0, 774.83m), (1, 774.38m), (2, 774.40m)], revisions.Select(p => (p.Revision, p.Close)));
        Assert.All(revisions, p => Assert.Equal((Currency.Usd, "yfinance", Imported), (p.Currency, p.Source, p.ImportedAt)));

        // The sessions are the calendar's, whoever has a price for them; another calendar has none here.
        Assert.Equal([Day(2), Day(5), Day(6)], await store.ListSessionsAsync(ClosingPrices.Calendar, CancellationToken.None));
        Assert.Equal(Day(6), await store.LatestSessionOnOrBeforeAsync(ClosingPrices.Calendar, Day(30), CancellationToken.None));
        Assert.Equal(Day(2), await store.LatestSessionOnOrBeforeAsync(ClosingPrices.Calendar, Day(4), CancellationToken.None));
        Assert.Null(await store.LatestSessionOnOrBeforeAsync(ClosingPrices.Calendar, Day(1), CancellationToken.None));
        Assert.Null(await store.LatestSessionOnOrBeforeAsync("XLON", Day(30), CancellationToken.None));
        Assert.Empty(await store.ListSessionsAsync("XLON", CancellationToken.None));

        // Each write leaves its note and the mark of its export; the latest export is the one a later one is held to.
        Assert.Equal(3, await db.AuditEvents.CountAsync());
        Assert.Equal(3, await db.PriceImports.CountAsync());
        var later = new ImportedExport(Instant.FromUtc(2026, 10, 8, 5, 0), new string('b', 64));
        await store.AddAsync(new PricesToStore([], [], ClosingPrices.Calendar, [Day(7)], [], []), Imported, Note, later, CancellationToken.None);
        await AddAsync(store, [], [], 8); // the mark of the older export, stored after it
        Assert.Equal(later, await store.LastImportAsync(CancellationToken.None));

        // A second first-close for a session is refused whole; a revision of nothing too; nothing rides along.
        var twice = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(store, [Close(w.Spy, 7, 781m), Close(w.Spy, 6, 779.10m)], []));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (twice.InnerException as PostgresException)?.SqlState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddAsync(store, [], [Close(w.Xlk, 6, 300m)]));
        var sessionTwice = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(store, [Close(w.Spy, 7, 781m)], [], 9, 6));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (sessionTwice.InnerException as PostgresException)?.SqlState);
        // A close of a day that is no session - a Saturday - takes the session that came with it down too.
        var noSession = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(store, [Close(w.Spy, 10, 781m)], [], 9));
        Assert.Equal(PostgresErrorCodes.CheckViolation, (noSession.InnerException as PostgresException)?.SqlState);
        Assert.Contains("2026-10-10 is not a session", noSession.InnerException!.Message, StringComparison.Ordinal);
        db.Settings.Add(new Setting { Key = "test", Value = "1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddAsync(store, [Close(w.Spy, 7, 781m)], []));
        db.ChangeTracker.Clear();
        Assert.Equal(6, await db.ClosingPrices.CountAsync());
        Assert.Equal(5, await db.MarketSessions.CountAsync());
        Assert.Equal(5, await db.PriceImports.CountAsync());
        Assert.Equal(5, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task DividendsAndSplitsAreStoredExactlyAndTheLatestRevisionIsTheOneInUse()
    {
        var w = await NewAsync();
        await using var db = DbSetup.Create(w.Database);
        var store = new ClosingPriceStore(db);

        // SPY pays 1.889 on the 5th; XLK pays 0.168 on the 5th and splits 2:1 on the 6th - with that day's own
        // dividend of 0.000125 a share, the smallest the table keeps.
        await AddEventsAsync(store, [Dividend(w.Spy, 5, 1.889m), Dividend(w.Xlk, 5, 0.168m), Split(w.Xlk, 6, 2m), Dividend(w.Xlk, 6, 0.000125m)], [], 5, 6);
        await AddEventsAsync(store, [], [Split(w.Xlk, 6, 3m)]);
        await AddEventsAsync(store, [], [Split(w.Xlk, 6, 0.25m)]);

        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(
            [Dividend(w.Xlk, 5, 0.168m), Dividend(w.Xlk, 6, 0.000125m), Split(w.Xlk, 6, 0.25m)],
            await store.ListEventsAsync(w.Xlk.Id, CancellationToken.None));
        Assert.Equal([Dividend(w.Spy, 5, 1.889m)], await store.ListEventsAsync(w.Spy.Id, CancellationToken.None));
        var revisions = await db.FundEvents.AsNoTracking().Where(e => e.Kind == FundEventKind.Split).OrderBy(e => e.Revision).ToListAsync();
        Assert.Equal([(0, 2m), (1, 3m), (2, 0.25m)], revisions.Select(e => (e.Revision, e.Value)));
        Assert.All(revisions, e => Assert.Equal(("yfinance", Imported), (e.Source, e.ImportedAt)));

        // The same event a second time is refused whole, a revision of none too, and one on a day that is no session.
        var twice = await Assert.ThrowsAsync<DbUpdateException>(() => AddEventsAsync(store, [Dividend(w.Spy, 6, 1m), Dividend(w.Spy, 5, 1.9m)], []));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (twice.InnerException as PostgresException)?.SqlState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddEventsAsync(store, [], [Split(w.Spy, 5, 2m)]));
        var saturday = await Assert.ThrowsAsync<DbUpdateException>(() => AddEventsAsync(store, [Dividend(w.Spy, 10, 1m)], [], 9));
        Assert.Contains("2026-10-10 is not a session", saturday.InnerException!.Message, StringComparison.Ordinal);
        Assert.Equal(6, await db.FundEvents.CountAsync());
        Assert.Equal(2, await db.MarketSessions.CountAsync());

        // The database keeps what is stored, and takes nothing that is no event.
        await using var conn = new NpgsqlConnection(w.Database);
        await conn.OpenAsync();
        const string insert = "INSERT INTO fund_events (instrument_id, day, kind, revision, value, source, imported_at) VALUES ";
        (string Sql, string SqlState)[] planted =
        [
            ("UPDATE fund_events SET value = 1 WHERE kind = 'Split'", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM fund_events", PostgresErrorCodes.InsufficientPrivilege),
            ("TRUNCATE fund_events", PostgresErrorCodes.InsufficientPrivilege),
            (insert + $"('{w.Spy.Id}', '2026-10-06', 'Dividend', 0, 0, 'x', now())", PostgresErrorCodes.CheckViolation), // nothing paid
            (insert + $"('{w.Spy.Id}', '2026-10-06', 'Bonus', 0, 1, 'x', now())", PostgresErrorCodes.CheckViolation), // no such kind
            (insert + $"('{w.Spy.Id}', '2026-10-06', 'Dividend', -1, 1, 'x', now())", PostgresErrorCodes.CheckViolation),
            (insert + $"('{w.Spy.Id}', '2026-10-05', 'Dividend', 2, 1, 'x', now())", PostgresErrorCodes.CheckViolation), // skips revision 1
            (insert + $"('{w.Spy.Id}', '2026-10-03', 'Dividend', 0, 1, 'x', now())", PostgresErrorCodes.CheckViolation), // a Saturday
            (insert + $"('{w.Spy.Id}', '2026-10-05', 'Dividend', 0, 1, 'x', now())", PostgresErrorCodes.UniqueViolation),
            (insert + $"('{Guid.NewGuid()}', '2026-10-05', 'Dividend', 0, 1, 'x', now())", PostgresErrorCodes.ForeignKeyViolation),
        ];
        foreach (var (sql, sqlState) in planted)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal((sql, sqlState), (sql, error.SqlState));
        }

        Assert.Equal(6, await db.FundEvents.CountAsync());
    }

    [Fact]
    public async Task DatabaseKeepsEveryStoredPriceAndSessionAndRefusesWhatIsNone()
    {
        var w = await NewAsync();
        await using (var db = DbSetup.Create(w.Database))
        {
            await AddAsync(new ClosingPriceStore(db), [Close(w.Spy, 5, 774.83m)], [], 5, 6);
        }

        await using var conn = new NpgsqlConnection(w.Database);
        await conn.OpenAsync();
        const string insert = "INSERT INTO closing_prices (instrument_id, session, revision, close, currency, source, imported_at) VALUES ";
        (string Sql, string SqlState)[] planted =
        [
            // Valuations were computed with these rows: they are never changed or removed.
            ("UPDATE closing_prices SET close = 1 WHERE session = '2026-10-05'", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM closing_prices", PostgresErrorCodes.InsufficientPrivilege),
            ("TRUNCATE closing_prices", PostgresErrorCodes.InsufficientPrivilege),
            (insert + $"('{w.Spy.Id}', '2026-10-06', 0, 0, 'USD', 'x', now())", PostgresErrorCodes.CheckViolation), // no price
            (insert + $"('{w.Spy.Id}', '2026-10-06', -1, 5, 'USD', 'x', now())", PostgresErrorCodes.CheckViolation),
            (insert + $"('{w.Spy.Id}', '2026-10-05', 2, 5, 'USD', 'x', now())", PostgresErrorCodes.CheckViolation), // skips revision 1
            (insert + $"('{w.Spy.Id}', '2026-10-05', 0, 5, 'USD', 'x', now())", PostgresErrorCodes.UniqueViolation),
            (insert + $"('{Guid.NewGuid()}', '2026-10-05', 0, 5, 'USD', 'x', now())", PostgresErrorCodes.ForeignKeyViolation), // of no instrument
            (insert + $"('{w.Spy.Id}', '2026-10-03', 0, 5, 'USD', 'x', now())", PostgresErrorCodes.CheckViolation), // a Saturday: no session
            ($"DELETE FROM instruments WHERE id = '{w.Spy.Id}'", PostgresErrorCodes.RestrictViolation),
            // Whether a day without a price is a gap or a closed exchange is read from these rows.
            ("UPDATE market_sessions SET session = '2026-10-04' WHERE session = '2026-10-05'", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM market_sessions WHERE session = '2026-10-06'", PostgresErrorCodes.InsufficientPrivilege),
            ("TRUNCATE market_sessions", PostgresErrorCodes.InsufficientPrivilege),
            ("INSERT INTO market_sessions (calendar, session, imported_at) VALUES ('XNYS', '2026-10-05', now())", PostgresErrorCodes.UniqueViolation),
            // Which export was imported last is what keeps an older one from undoing a correction.
            ("UPDATE price_imports SET exported_at = '2020-01-01'", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM price_imports", PostgresErrorCodes.InsufficientPrivilege),
            ("TRUNCATE price_imports", PostgresErrorCodes.InsufficientPrivilege),
            ($"INSERT INTO price_imports (id, exported_at, sha256, imported_at) VALUES ('{Guid.NewGuid()}', now(), 'ABC', now())", PostgresErrorCodes.CheckViolation),
        ];

        foreach (var (sql, sqlState) in planted)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal((sql, sqlState), (sql, error.SqlState));
        }

        // The close of a session is taken: the rule above is about the day, not about inserting by hand.
        await using (var cmd = new NpgsqlCommand(insert + $"('{w.Spy.Id}', '2026-10-06', 0, 779.09, 'USD', 'x', now())", conn))
        {
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        await using var check = DbSetup.Create(w.Database);
        Assert.Equal([774.83m, 779.09m], await check.ClosingPrices.OrderBy(p => p.Session).Select(p => p.Close).ToListAsync());
        Assert.Equal(2, await check.MarketSessions.CountAsync());
        Assert.Equal(Stamp.Sha256, (await check.PriceImports.SingleAsync()).Sha256);
    }

    [Fact]
    public async Task DatabaseWithdrawsADayOrAnEventOnlyAsAFurtherRowWithAReasonAndOnlyWhenNothingRestsOnIt()
    {
        // Sessions 5, 6 and 7; SPY closed on the 5th; XLK paid a dividend on the 7th.
        var w = await NewAsync();
        await using (var db = DbSetup.Create(w.Database))
        {
            await AddAsync(new ClosingPriceStore(db), [Close(w.Spy, 5, 774.83m)], [], 5, 6, 7);
            await AddEventsAsync(new ClosingPriceStore(db), [Dividend(w.Xlk, 7, 0.168m)], []);
        }

        await using var conn = new NpgsqlConnection(w.Database);
        await conn.OpenAsync();
        const string session = "INSERT INTO market_sessions (calendar, session, revision, withdrawn, reason, imported_at) VALUES ";
        const string fundEvent = "INSERT INTO fund_events (instrument_id, day, kind, revision, value, source, withdrawn, reason, imported_at) VALUES ";
        async Task<int> RunAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            return await cmd.ExecuteNonQueryAsync();
        }

        (string Sql, string SqlState)[] planted =
        [
            (session + "('XNYS', '2026-10-08', 0, true, 'x', now())", PostgresErrorCodes.CheckViolation), // withdrawn before it was a session
            (session + "('XNYS', '2026-10-06', 1, true, NULL, now())", PostgresErrorCodes.CheckViolation), // without a reason
            (session + "('XNYS', '2026-10-06', 1, true, '  ', now())", PostgresErrorCodes.CheckViolation),
            (session + "('XNYS', '2026-10-06', 1, false, 'x', now())", PostgresErrorCodes.CheckViolation), // a reason for nothing
            (session + "('XNYS', '2026-10-06', 2, true, 'x', now())", PostgresErrorCodes.CheckViolation), // skips revision 1
            (session + "('XNYS', '2026-10-05', 1, true, 'closed', now())", PostgresErrorCodes.CheckViolation), // SPY traded that day
            (session + "('XNYS', '2026-10-07', 1, true, 'closed', now())", PostgresErrorCodes.CheckViolation), // XLK's dividend is not withdrawn
            (fundEvent + $"('{w.Xlk.Id}', '2026-10-07', 'Dividend', 1, 0.168, 'x', true, NULL, now())", PostgresErrorCodes.CheckViolation),
            (fundEvent + $"('{w.Xlk.Id}', '2026-10-07', 'Dividend', 0, 0.168, 'x', true, 'x', now())", PostgresErrorCodes.CheckViolation),
            ("UPDATE market_sessions SET withdrawn = true, reason = 'x' WHERE session = '2026-10-06'", PostgresErrorCodes.InsufficientPrivilege),
        ];
        foreach (var (sql, sqlState) in planted)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => RunAsync(sql));
            Assert.Equal((sql, sqlState), (sql, error.SqlState));
        }

        // The event first, then its day: both as further rows, with the owner's reason.
        Assert.Equal(1, await RunAsync(fundEvent + $"('{w.Xlk.Id}', '2026-10-07', 'Dividend', 1, 0.168, 'x', true, 'not paid', now())"));
        Assert.Equal(1, await RunAsync(session + "('XNYS', '2026-10-07', 1, true, 'the exchange was closed', now())"));
        Assert.Equal(1, await RunAsync(session + "('XNYS', '2026-10-06', 1, true, 'the exchange was closed', now())"));

        // Nothing rests on a withdrawn day any more.
        var close = await Assert.ThrowsAsync<PostgresException>(() => RunAsync(
            $"INSERT INTO closing_prices (instrument_id, session, revision, close, currency, source, imported_at) VALUES ('{w.Spy.Id}', '2026-10-06', 0, 779.09, 'USD', 'x', now())"));
        var dividend = await Assert.ThrowsAsync<PostgresException>(() => RunAsync(
            fundEvent + $"('{w.Spy.Id}', '2026-10-06', 'Dividend', 0, 1.889, 'x', false, NULL, now())"));
        Assert.Equal((PostgresErrorCodes.CheckViolation, PostgresErrorCodes.CheckViolation), (close.SqlState, dividend.SqlState));
        Assert.Contains("2026-10-06 is not a session", close.MessageText, StringComparison.Ordinal);

        // A day the owner withdrew may be a session again only as the next row - the import never does that unasked.
        Assert.Equal(1, await RunAsync(session + "('XNYS', '2026-10-06', 2, false, NULL, now())"));
        Assert.Equal(1, await RunAsync($"INSERT INTO closing_prices (instrument_id, session, revision, close, currency, source, imported_at) VALUES ('{w.Spy.Id}', '2026-10-06', 0, 779.09, 'USD', 'x', now())"));
        await using var check = DbSetup.Create(w.Database);
        Assert.Equal(6, await check.MarketSessions.CountAsync());
        Assert.Equal(2, await check.FundEvents.CountAsync());
    }

    [Fact]
    public async Task CommandLoadsTheExportOnceRecordsACorrectionAndAsksAboutWhatIsUnusual()
    {
        var w = await NewAsync();
        var dataDir = RunningHost.NewDataDir();
        var folder = Path.Combine(dataDir, "derived", "closing-prices");
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 7, 14, 0)); // Wednesday morning in New York

        async Task<(int Code, string[] Output)> RunAsync(params string[] args)
        {
            // As Program does it: the command's own arguments first, the settings of the Host after them.
            var (file, acceptedCode, withdrawal, hostArgs) = ImportPricesCommand.Parse(args);
            await using var app = ITradeHost.Build(
                [.. RunningHost.Args(w.Database, dataDir), .. hostArgs], services => services.AddSingleton<IClock>(clock));
            using var output = new StringWriter();
            var code = await ImportPricesCommand.RunAsync(app, file, acceptedCode, output, withdrawal);
            return (code, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        var nothing = await RunAsync();
        Assert.Equal(1, nothing.Code);
        Assert.Contains("There is no export of closing prices", nothing.Output[0], StringComparison.Ordinal);

        WriteExport(
            folder, "2026-10-07T06:00:00+00:00", [Day(5), Day(6)],
            ["SPY,2026-10-05,774.83", "SPY,2026-10-06,779.09", "VWO,2026-10-06,55.10", "XLK,2026-10-06,304.20"]);
        var first = await RunAsync();
        Assert.Equal(0, first.Code);
        Assert.Equal(
            [
                "Closing prices (yfinance, exported 2026-10-07 06:00 UTC): 3 new; 0 already stored and unchanged; 2 new session(s) of XNYS.",
                "Latest finished session: 2026-10-06.",
                "Not in the instrument catalogue, skipped: VWO.",
            ],
            first.Output);
        var again = await RunAsync("--ITrade:HeartbeatSeconds=20"); // a setting of the Host passes through
        Assert.Equal(
            "Closing prices (yfinance, exported 2026-10-07 06:00 UTC): 0 new; 3 already stored and unchanged; 0 new session(s) of XNYS.",
            again.Output[0]);

        // A day later the vendor has corrected one close; the table is in another folder, named on the command line.
        clock.Reset(Instant.FromUtc(2026, 10, 8, 14, 0));
        var elsewhere = Path.Combine(dataDir, "elsewhere");
        var other = WriteExport(
            elsewhere, "2026-10-08T06:00:00+00:00", [Day(5), Day(6), Day(7)],
            ["SPY,2026-10-05,774.38", "SPY,2026-10-06,779.09", "SPY,2026-10-07,781.00"]);
        var corrected = await RunAsync(other);
        Assert.Equal(0, corrected.Code);
        Assert.Equal(
            [
                "Closing prices (yfinance, exported 2026-10-08 06:00 UTC): 1 new; 1 already stored and unchanged; 1 new session(s) of XNYS.",
                "Latest finished session: 2026-10-07.",
                "Revised SPY 2026-10-05: 774.83 -> 774.38 (the old price is kept as the earlier revision).",
                "No prices in the export for: XLK.",
            ],
            corrected.Output);

        // A table someone touched after it was exported is not taken - neither of the two.
        File.AppendAllText(other, "SPY,2026-10-08,790.00\n");
        var touched = await RunAsync(other);
        Assert.Equal(1, touched.Code);
        Assert.Contains("The table of closing prices is not the one its manifest vouches for", touched.Output[0], StringComparison.Ordinal);
        other = WriteExport(
            elsewhere, "2026-10-08T06:00:00+00:00", [Day(5), Day(6), Day(7)],
            ["SPY,2026-10-05,774.38", "SPY,2026-10-06,779.09", "SPY,2026-10-07,781.00"]);
        File.AppendAllText(Path.Combine(elsewhere, SessionsName), "2026-10-08\n");
        touched = await RunAsync(other);
        Assert.Equal(1, touched.Code);
        Assert.Contains("The table of sessions is not the one its manifest vouches for", touched.Output[0], StringComparison.Ordinal);

        // Yesterday's export is still in the data folder: it would put the corrected close back.
        var older = await RunAsync();
        Assert.Equal(1, older.Code);
        Assert.Contains("before the one imported last (2026-10-08 06:00 UTC)", older.Output[0], StringComparison.Ordinal);

        await using var db = DbSetup.Create(w.Database);
        Assert.Equal(5, await db.ClosingPrices.CountAsync());
        Assert.Equal(3, await db.MarketSessions.CountAsync());
        Assert.Equal(2, await db.PriceImports.CountAsync());
        Assert.Equal(
            [(PricesNote.Imported, "prices"), (PricesNote.Noted, "prices")],
            (await db.AuditEvents.OrderBy(a => a.OccurredAt).ThenBy(a => a.Kind).ToListAsync()).Select(a => (a.Kind, a.Actor)));

        // Every stored close of a fund stated differently - what an export that forgot a split would do.
        var september = Enumerable.Range(1, 25).Select(d => new LocalDate(2026, 9, d)).ToArray();
        LocalDate[] sessions = [.. september, Day(5), Day(6), Day(7)];
        string[] Xlk(Func<int, decimal> close) =>
            [.. september.Select((d, i) => string.Create(CultureInfo.InvariantCulture, $"XLK,{d:R},{close(i):0.00}"))];
        WriteExport(folder, "2026-10-08T07:00:00+00:00", sessions, Xlk(i => 300 + i));
        Assert.Equal(0, (await RunAsync()).Code);
        WriteExport(folder, "2026-10-08T08:00:00+00:00", sessions, Xlk(i => (300 + i) / 2m));
        var wholesale = await RunAsync();
        Assert.Equal(1, wholesale.Code);
        Assert.Contains("states 25 stored closing price(s) differently", wholesale.Output[0], StringComparison.Ordinal);
        const string toAccept = "To accept exactly this: dotnet run --project src/ITrade.Host -- import-prices --accept-changes ";
        Assert.StartsWith(toAccept, wholesale.Output[1], StringComparison.Ordinal);
        var code = wholesale.Output[1][toAccept.Length..];
        Assert.Equal(30, await db.ClosingPrices.CountAsync());

        Assert.Equal(2, (await RunAsync("--accept-changes")).Code); // the flag without its code
        Assert.Equal(1, (await RunAsync("--accept-changes", "00000000")).Code); // a code of something else
        Assert.Equal(30, await db.ClosingPrices.CountAsync());
        var accepted = await RunAsync("--accept-changes", code);
        Assert.Equal(0, accepted.Code);
        Assert.Equal(20, accepted.Output.Count(l => l.StartsWith("Revised XLK ", StringComparison.Ordinal))); // not all 25 on the screen
        Assert.Contains("... and 5 more revisions; all are in the audit trail.", accepted.Output);
        Assert.Contains("Behind the latest session, last close in brackets: XLK (2026-09-25). There is no price to value them with after that.", accepted.Output);
        Assert.Contains("Note: the export left out 1 stored price(s); they are kept as they are.", accepted.Output); // XLK's close of 6 October
        Assert.Contains("No prices in the export for: SPY.", accepted.Output);
        Assert.Equal(55, await db.ClosingPrices.CountAsync());
        Assert.Equal("owner", (await db.AuditEvents.OrderBy(a => a.OccurredAt).ThenBy(a => a.Id).LastAsync()).Actor);

        // A new close half as high again as the one before it, in the file the owner named: the line to accept
        // it names that file too.
        clock.Reset(Instant.FromUtc(2026, 10, 9, 14, 0));
        string[] spy = ["SPY,2026-10-05,774.38", "SPY,2026-10-06,779.09", "SPY,2026-10-07,781.00", "SPY,2026-10-08,1200.00"];
        other = WriteExport(elsewhere, "2026-10-09T06:00:00+00:00", [.. sessions, Day(8)], spy);
        var unusual = await RunAsync(other);
        Assert.Equal(1, unusual.Code);
        Assert.Contains("SPY 2026-10-08: 1200 after 781 on 2026-10-07", unusual.Output[0], StringComparison.Ordinal);
        var named = $"To accept exactly this: dotnet run --project src/ITrade.Host -- import-prices \"{other}\" --accept-changes ";
        Assert.StartsWith(named, unusual.Output[1], StringComparison.Ordinal);
        Assert.Equal(55, await db.ClosingPrices.CountAsync());
        var took = await RunAsync(other, "--accept-changes", unusual.Output[1][named.Length..]);
        Assert.Equal(0, took.Code);
        Assert.Contains("Accepted SPY 2026-10-08: 1200 after 781 on 2026-10-07.", took.Output);
        Assert.Equal(56, await db.ClosingPrices.CountAsync());

        // The same jump on the day of a split is a price like any other.
        clock.Reset(Instant.FromUtc(2026, 10, 10, 14, 0));
        WriteExport(elsewhere, "2026-10-10T06:00:00+00:00", [.. sessions, Day(8), Day(9)], [.. spy, "SPY,2026-10-09,600.00"], splitOf: "SPY,2026-10-09");
        Assert.Equal(0, (await RunAsync(other)).Code);
        Assert.Equal(57, await db.ClosingPrices.CountAsync());

        // The export's dividends and splits come with the prices; a restated one is said.
        clock.Reset(Instant.FromUtc(2026, 10, 11, 14, 0));
        WriteExport(
            elsewhere, "2026-10-11T06:00:00+00:00", [.. sessions, Day(8), Day(9)], [.. spy, "SPY,2026-10-09,600.00"], splitOf: "SPY,2026-10-09",
            events: ["SPY,2026-10-06,dividend,1.889", "SPY,2026-10-09,split,2", "VWO,2026-10-06,dividend,0.4"]);
        var withEvents = await RunAsync(other);
        Assert.Equal(1, withEvents.Code); // VWO is not among the export's tickers: the reader refuses the table
        Assert.Contains("fund events holds one of VWO, which the manifest does not name", withEvents.Output[0], StringComparison.Ordinal);
        WriteExport(
            elsewhere, "2026-10-11T06:00:00+00:00", [.. sessions, Day(8), Day(9)], [.. spy, "SPY,2026-10-09,600.00"], splitOf: "SPY,2026-10-09",
            events: ["SPY,2026-10-06,dividend,1.889", "SPY,2026-10-09,split,2"]);
        withEvents = await RunAsync(other);
        Assert.Equal(0, withEvents.Code);
        Assert.Contains("Dividends and splits of the funds: 2 new.", withEvents.Output);
        WriteExport(
            elsewhere, "2026-10-11T07:00:00+00:00", [.. sessions, Day(8), Day(9)], [.. spy, "SPY,2026-10-09,600.00"], splitOf: "SPY,2026-10-09",
            events: ["SPY,2026-10-06,dividend,1.9", "SPY,2026-10-09,split,2"]);
        var restated = await RunAsync(other);
        Assert.Equal(0, restated.Code);
        Assert.Contains("Revised SPY 2026-10-06 dividend: 1.889 -> 1.9 (the old figure is kept as the earlier revision).", restated.Output);
        Assert.Equal(3, await db.FundEvents.CountAsync());
        // The vendor withdraws the split: it stays stored, and the import says what that means.
        WriteExport(
            elsewhere, "2026-10-11T08:00:00+00:00", [.. sessions, Day(8), Day(9)], [.. spy, "SPY,2026-10-09,600.00"], splitOf: "SPY,2026-10-09",
            events: ["SPY,2026-10-06,dividend,1.9"]);
        var withdrawn = await RunAsync(other);
        Assert.Equal(0, withdrawn.Code);
        Assert.Contains(
            "Note: the export no longer states 1 stored dividend(s) or split(s): SPY 2026-10-09 split 2. They are kept and an index is still followed with them: if the source moved or withdrew them, the comparison is off by that much.",
            withdrawn.Output);
        Assert.Equal(3, await db.FundEvents.CountAsync());
        Assert.Equal(57, await db.ClosingPrices.CountAsync());
        // Only the owner withdraws it - by the code offered, in the file named, with a reason.
        var offer = $"If the fund's own announcements show they did not happen, withdraw exactly these (the owner's step): dotnet run --project src/ITrade.Host -- import-prices \"{other}\" --withdraw ";
        var offered = withdrawn.Output.Single(l => l.StartsWith(offer, StringComparison.Ordinal))[offer.Length..].Split(' ')[0];
        Assert.Equal(2, (await RunAsync(other, "--withdraw", offered)).Code); // no reason
        var gone = await RunAsync(other, "--withdraw", offered, "SPY did not split on 9 October (the fund's notices)");
        Assert.Equal(0, gone.Code);
        Assert.Contains("Withdrawn by the owner: SPY 2026-10-09 split 2 - no longer in the index (the stored row stays, followed by its withdrawal).", gone.Output);
        Assert.Equal(4, await db.FundEvents.CountAsync());
        Assert.Equal([FundEventKind.Dividend], (await new ClosingPriceStore(db).ListEventsAsync(w.Spy.Id, CancellationToken.None)).Select(e => e.Kind));
        var note = await db.AuditEvents.OrderBy(a => a.OccurredAt).ThenBy(a => a.Id).LastAsync();
        Assert.Equal((PricesNote.Withdrawn, "owner"), (note.Kind, note.Actor));
        Assert.Contains("SPY did not split on 9 October (the fund's notices)", note.Payload, StringComparison.Ordinal);
        // The vendor states the split again: in use again, as the revision after its withdrawal.
        WriteExport(
            elsewhere, "2026-10-11T09:00:00+00:00", [.. sessions, Day(8), Day(9)], [.. spy, "SPY,2026-10-09,600.00"], splitOf: "SPY,2026-10-09",
            events: ["SPY,2026-10-06,dividend,1.9", "SPY,2026-10-09,split,2"]);
        var back = await RunAsync(other);
        Assert.Equal(0, back.Code);
        Assert.Contains("Stated again after the owner withdrew it, in use again: SPY 2026-10-09 split 2.", back.Output);
        Assert.Equal([0, 1, 2], await db.FundEvents.Where(e => e.Kind == FundEventKind.Split).OrderBy(e => e.Revision).Select(e => e.Revision).ToListAsync());
        Assert.Equal(2, (await new ClosingPriceStore(db).ListEventsAsync(w.Spy.Id, CancellationToken.None)).Count);
        Assert.Equal((0, 0), await new ClosingPriceStore(db).CountWithdrawnAsync(CancellationToken.None)); // the withdrawal is history now
        Assert.Equal("owner", (await db.FundEvents.SingleAsync(e => e.Withdrawn)).Source);
        clock.Reset(Instant.FromUtc(2026, 10, 10, 14, 0));

        // The price for a date, through the Host's own services.
        await using var host = ITradeHost.Build(RunningHost.Args(w.Database, dataDir), services => services.AddSingleton<IClock>(clock));
        await using var scope = host.Services.CreateAsyncScope();
        var prices = scope.ServiceProvider.GetRequiredService<ClosingPrices>();
        var revised = await prices.OnAsync(w.Spy.Id, Day(5), CancellationToken.None);
        Assert.Equal((PriceAvailability.OfTheSession, 774.38m), (revised.Availability, revised.Price!.Close.Value));
        var sunday = await prices.OnAsync(w.Spy.Id, Day(4), CancellationToken.None);
        Assert.Equal(PriceAvailability.Unavailable, sunday.Availability); // SPY has no close before Monday the 5th
        var saturday = await prices.OnAsync(w.Spy.Id, Day(10), CancellationToken.None);
        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 600m, true), (saturday.Availability, saturday.Price!.Close.Value, saturday.IsProvisional));
        Assert.Equal(PriceAvailability.MissingForTheSession, (await prices.OnAsync(w.Xlk.Id, Day(7), CancellationToken.None)).Availability);
        Directory.Delete(dataDir, recursive: true);
    }

    [Fact]
    public async Task ASessionTheCorrectedCalendarDoesNotListIsWithdrawnByTheOwnersCodeAndIsThenAClosedDay()
    {
        // The library took Tuesday the 6th for a session; nobody has a price for it. Corrected, it does not list it.
        var w = await NewAsync();
        var dataDir = RunningHost.NewDataDir();
        var folder = Path.Combine(dataDir, "derived", "closing-prices");
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 8, 14, 0));

        async Task<(int Code, string[] Output)> RunAsync(params string[] args)
        {
            var (file, acceptedCode, withdrawal, hostArgs) = ImportPricesCommand.Parse(args);
            await using var app = ITradeHost.Build(
                [.. RunningHost.Args(w.Database, dataDir), .. hostArgs], services => services.AddSingleton<IClock>(clock));
            using var output = new StringWriter();
            var code = await ImportPricesCommand.RunAsync(app, file, acceptedCode, output, withdrawal);
            return (code, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        string[] lines = ["SPY,2026-10-05,774.83", "SPY,2026-10-07,781.00"];
        WriteExport(folder, "2026-10-08T06:00:00+00:00", [Day(5), Day(6), Day(7)], lines);
        Assert.Equal(0, (await RunAsync()).Code);
        // XLK, not in these exports, has a dividend stored on that day: it goes with the day.
        await using (var setup = DbSetup.Create(w.Database))
        {
            await AddEventsAsync(new ClosingPriceStore(setup), [Dividend(w.Xlk, 6, 0.168m)], []);
        }

        WriteExport(folder, "2026-10-08T07:00:00+00:00", [Day(5), Day(7)], lines);

        var refused = await RunAsync();

        Assert.Equal(1, refused.Code);
        Assert.StartsWith("The closing prices were not imported: 2026-10-06 is stored as session(s) of XNYS and the export does not list it. On it stored: XLK 2026-10-06 dividend 0.168. The two calendars disagree.", refused.Output[0], StringComparison.Ordinal);
        const string toWithdraw = "To withdraw exactly this (the owner's step, after checking): dotnet run --project src/ITrade.Host -- import-prices --withdraw ";
        Assert.StartsWith(toWithdraw, refused.Output[1], StringComparison.Ordinal);
        var code = refused.Output[1][toWithdraw.Length..].Split(' ')[0];
        Assert.Equal(2, (await RunAsync("--withdraw")).Code);
        Assert.Equal(2, (await RunAsync("--withdraw", code, "NYSE", "was", "closed")).Code); // a reason without quotes
        Assert.Equal(2, (await RunAsync("--withdraw", code, new string('x', ClosingPrices.LongestReason + 1))).Code);
        Assert.Equal(1, (await RunAsync("--withdraw", "00000000", "closed")).Code);

        var withdrawn = await RunAsync("--withdraw", code, "NYSE was closed (its holiday list)");

        Assert.Equal(0, withdrawn.Code);
        Assert.Contains("Withdrawn by the owner: the session 2026-10-06 of XNYS - a closed day from now on.", withdrawn.Output);
        Assert.Contains("Withdrawn by the owner: XLK 2026-10-06 dividend 0.168 - no longer in the index (the stored row stays, followed by its withdrawal).", withdrawn.Output);
        await using var db = DbSetup.Create(w.Database);
        var store = new ClosingPriceStore(db);
        Assert.Equal([Day(5), Day(7)], await store.ListSessionsAsync(ClosingPrices.Calendar, CancellationToken.None));
        Assert.Equal([new WithdrawnSession(Day(6), "NYSE was closed (its holiday list)")], await store.ListWithdrawnSessionsAsync(ClosingPrices.Calendar, CancellationToken.None));
        Assert.Equal((1, 1), await store.CountWithdrawnAsync(CancellationToken.None));
        Assert.Empty(await store.ListEventsAsync(w.Xlk.Id, CancellationToken.None));
        // No gap any more: Monday's close is carried over the closed Tuesday.
        await using var host = ITradeHost.Build(RunningHost.Args(w.Database, dataDir), services => services.AddSingleton<IClock>(clock));
        await using var scope = host.Services.CreateAsyncScope();
        var tuesday = await scope.ServiceProvider.GetRequiredService<ClosingPrices>().OnAsync(w.Spy.Id, Day(6), CancellationToken.None);
        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 774.83m), (tuesday.Availability, tuesday.Price!.Close.Value));
        Directory.Delete(dataDir, recursive: true);
    }

    [Fact]
    public async Task PricesStoredBeforeTheSessionsWereKeptGetThemWithTheNextImport()
    {
        // The working database met the sessions this way: closes were imported under the migration before.
        var database = await pg.NewEmptyDatabaseAsync();
        var spy = Fund("SPY");
        await using (var db = DbSetup.Create(database))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync("ClosingPrices");
            db.Instruments.Add(InstrumentRow.From(spy));
            db.ClosingPrices.AddRange(
                new ClosingPriceRow { InstrumentId = spy.Id, Session = Day(2), Close = 769.64m, Currency = Currency.Usd, Source = "yfinance", ImportedAt = Imported },
                new ClosingPriceRow { InstrumentId = spy.Id, Session = Day(6), Close = 779.09m, Currency = Currency.Usd, Source = "yfinance", ImportedAt = Imported });
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
        }

        var dataDir = RunningHost.NewDataDir();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 7, 14, 0));
        await using var app = ITradeHost.Build(RunningHost.Args(database, dataDir), services => services.AddSingleton<IClock>(clock));
        await using var scope = app.Services.CreateAsyncScope();
        var prices = scope.ServiceProvider.GetRequiredService<ClosingPrices>();
        Assert.Equal(PriceAvailability.OfTheSession, (await prices.OnAsync(spy.Id, Day(2), CancellationToken.None)).Availability);
        Assert.Equal(PriceAvailability.Unavailable, (await prices.OnAsync(spy.Id, Day(3), CancellationToken.None)).Availability);
        using (var said = new StringWriter())
        {
            await CheckCommand.RunAsync(RunningHost.Args(database, dataDir), said);
            Assert.Contains("  closing prices   stored without the sessions of the exchange, so only a session's own close can be used - uv run", said.ToString(), StringComparison.Ordinal);
        }

        WriteExport(
            Path.Combine(dataDir, "derived", "closing-prices"), "2026-10-07T06:00:00+00:00", [Day(1), Day(2), Day(5), Day(6)],
            ["SPY,2026-10-02,769.64", "SPY,2026-10-06,779.09"]);
        using var output = new StringWriter();
        Assert.Equal(0, await ImportPricesCommand.RunAsync(app, null, null, output));

        Assert.StartsWith(
            "Closing prices (yfinance, exported 2026-10-07 06:00 UTC): 0 new; 2 already stored and unchanged; 4 new session(s) of XNYS.",
            output.ToString(), StringComparison.Ordinal);
        var saturday = await prices.OnAsync(spy.Id, Day(3), CancellationToken.None);
        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 769.64m), (saturday.Availability, saturday.Price!.Close.Value));
        Assert.Equal(PriceAvailability.MissingForTheSession, (await prices.OnAsync(spy.Id, Day(5), CancellationToken.None)).Availability);
        Directory.Delete(dataDir, recursive: true);
    }

    [Fact]
    public async Task CheckShowsHowOldTheClosingPricesAreAndWhoIsBehind()
    {
        var w = await NewAsync();
        var dataDir = RunningHost.NewDataDir();
        var webRoot = Directory.CreateTempSubdirectory("itrade-web-").FullName;
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html>");
        string[] args = [.. RunningHost.Args(w.Database, dataDir), $"--webroot={webRoot}"];
        // `check` reads the real clock, so the prices are placed around the real today.
        var today = TradeDate.Of(SystemClock.Instance.GetCurrentInstant()).Value;

        async Task<string> CheckAsync()
        {
            using var output = new StringWriter();
            Assert.Equal(0, await CheckCommand.RunAsync(args, output)); // old prices never stop the Host from starting
            return output.ToString();
        }

        await using (var app = ITradeHost.Build(args))
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OwnerAccounts>()
                .SetPasswordAsync(Guid.NewGuid().ToString("N"), "test", CancellationToken.None);
        }

        Assert.Contains("  closing prices   none stored yet - uv run --directory python itrade ingest --universe etf_pullback_v1, then dotnet run --project src/ITrade.Host -- import-prices", await CheckAsync(), StringComparison.Ordinal);

        async Task AddAsync(int daysAgo, params Instrument[] priced)
        {
            await using var db = DbSetup.Create(w.Database);
            var session = today.PlusDays(-daysAgo);
            await new ClosingPriceStore(db).AddAsync(
                new PricesToStore(
                    [.. priced.Select(i => new ClosingPrice(i.Id, session, new Price(700m, Currency.Usd), "yfinance"))], [],
                    ClosingPrices.Calendar, [session], [], []),
                Imported, Note, Stamp, CancellationToken.None);
        }

        await AddAsync(30, w.Spy, w.Xlk);
        var old = await CheckAsync();
        Assert.Contains($"  closing prices   up to session {today.PlusDays(-30):R}, 30 day(s) old - too old to value today with:", old, StringComparison.Ordinal);
        Assert.DoesNotContain("behind", old, StringComparison.Ordinal);

        await AddAsync(2, w.Spy);
        var fresh = await CheckAsync();
        Assert.Contains($"  closing prices   up to session {today.PlusDays(-2):R}, 2 day(s) old{Environment.NewLine}", fresh, StringComparison.Ordinal);
        Assert.Contains(
            $"  closing prices   behind, last close in brackets: XLK ({today.PlusDays(-30):R}) - no price to value them with after that",
            fresh, StringComparison.Ordinal);

        // A session nobody has a price for yet is still the latest session: both are behind it.
        await AddAsync(1);
        Assert.Contains(
            $"behind, last close in brackets: SPY ({today.PlusDays(-2):R}), XLK ({today.PlusDays(-30):R})",
            await CheckAsync(), StringComparison.Ordinal);

        // The owner withdraws that session: it is no session any more, and `check` says what was withdrawn.
        await using (var db = DbSetup.Create(w.Database))
        {
            await new ClosingPriceStore(db).AddAsync(
                new PricesToStore([], [], ClosingPrices.Calendar, [], [], [], new Withdrawal([today.PlusDays(-1)], [], "the exchange was closed")),
                Imported, Note, Stamp, CancellationToken.None);
        }

        var withdrawn = await CheckAsync();
        Assert.Contains($"  closing prices   up to session {today.PlusDays(-2):R}, 2 day(s) old{Environment.NewLine}", withdrawn, StringComparison.Ordinal);
        Assert.Contains(
            $"  closing prices   withdrawn by the owner: 1 session(s) ({today.PlusDays(-1):R}), 0 dividend(s) or split(s) - closed days and events that did not happen, from then on",
            withdrawn, StringComparison.Ordinal);

        Directory.Delete(webRoot, recursive: true);
        Directory.Delete(dataDir, recursive: true);
    }
}
