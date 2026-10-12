using ITrade.Application.Fx;
using ITrade.Application.Jobs;
using ITrade.Domain;
using ITrade.Host;
using ITrade.Host.Auth;
using ITrade.Host.Fx;
using ITrade.Host.Jobs;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Host;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace ITrade.IntegrationTests.Fx;

/// <summary>
/// P3.3.01 on a real database and the real Host: exchange rates are stored exactly and only ever added, the
/// daily job and the command load them, a change by the source waits for the owner, and a stuck job is seen by
/// `check` and released by `retry-job`. The source is a stand-in: no test reaches the Bank of Israel.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class FxRatesTests(PostgresFixture pg)
{
    private const string Boi = "BOI representative";

    private static readonly Instant Fetched = Instant.FromUtc(2026, 10, 12, 16, 0);

    private static FxRate Rate(LocalDate date, decimal rate) => new(Currency.Usd, Currency.Ils, rate, date, Boi);

    private static LocalDate Day(int day) => new(2026, 10, day);

    private static async Task<string> MigratedAsync(PostgresFixture pg)
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var db = DbSetup.Create(database);
        await db.Database.MigrateAsync();
        return database;
    }

    private static readonly FxRatesNote Note = new("test", FxRatesNote.Updated, "{}");

    private static Task AddAsync(FxRateStore store, params FxRate[] rates) =>
        store.AddAsync(rates, [], null, Fetched, Note, CancellationToken.None);

    [Fact]
    public async Task RatesAreStoredExactlyOnceAndFoundByDate()
    {
        var database = await MigratedAsync(pg);
        await using var db = DbSetup.Create(database);
        var store = new FxRateStore(db);

        await AddAsync(store, Rate(new LocalDate(2005, 1, 3), 4.356m), Rate(Day(8), 3.084m), Rate(Day(9), 3.055001m));

        Assert.Empty(db.ChangeTracker.Entries());
        var all = await store.ListAsync(Currency.Usd, Currency.Ils, Boi, FxRates.HistoryStart, Day(12), CancellationToken.None);
        Assert.Equal([Rate(new LocalDate(2005, 1, 3), 4.356m), Rate(Day(8), 3.084m), Rate(Day(9), 3.055001m)], all);
        Assert.Equal([Rate(Day(9), 3.055001m)], await store.ListAsync(Currency.Usd, Currency.Ils, Boi, Day(9), Day(9), CancellationToken.None));
        Assert.Equal(Rate(Day(9), 3.055001m), await store.LatestOnOrBeforeAsync(Currency.Usd, Currency.Ils, Boi, Day(11), CancellationToken.None));
        Assert.Equal(Rate(Day(8), 3.084m), await store.LatestOnOrBeforeAsync(Currency.Usd, Currency.Ils, Boi, Day(8), CancellationToken.None));
        Assert.Null(await store.LatestOnOrBeforeAsync(Currency.Usd, Currency.Ils, Boi, new LocalDate(2004, 12, 31), CancellationToken.None));
        // Another pair or another source is another series.
        Assert.Null(await store.LatestOnOrBeforeAsync(Currency.Ils, Currency.Usd, Boi, Day(11), CancellationToken.None));
        Assert.Null(await store.LatestOnOrBeforeAsync(Currency.Usd, Currency.Ils, "somebody else", Day(11), CancellationToken.None));
        Assert.Equal(Fetched, (await db.FxRates.AsNoTracking().FirstAsync()).FetchedAt);

        // A second first-reading for the same day is refused whole - the new day in the same batch is not stored either.
        var twice = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(store, Rate(Day(12), 3.041m), Rate(Day(9), 3.056m)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (twice.InnerException as PostgresException)?.SqlState);
        Assert.Equal(3, await db.FxRates.CountAsync());
        Assert.Empty(db.ChangeTracker.Entries());

        // A rate finer than the table keeps would be rounded on the way in: it is refused before it gets there.
        await Assert.ThrowsAsync<ArgumentException>(() => AddAsync(store, Rate(Day(12), 3.0410001m)));
        // Rates are written on their own.
        db.Settings.Add(new Setting { Key = "test", Value = "1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddAsync(store, Rate(Day(12), 3.041m)));
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.FxRates.CountAsync());
    }

    [Fact]
    public async Task AcceptedRateIsRecordedWithItsReasonNextToTheRateItReplacesAndIsTheOneInUse()
    {
        var database = await MigratedAsync(pg);
        await using var db = DbSetup.Create(database);
        var store = new FxRateStore(db);
        await AddAsync(store, Rate(Day(8), 3.084m), Rate(Day(9), 3.055m));

        // One acceptance: the 8th corrected, the 13th a first rate that had to be accepted; the 12th needed nobody.
        await store.AddAsync([Rate(Day(12), 3.041m)], [Rate(Day(8), 3.085m), Rate(Day(13), 2.75m)], "checked on the bank's page", Fetched, Note, CancellationToken.None);
        await store.AddAsync([], [Rate(Day(8), 3.086m)], "and once more", Fetched, Note, CancellationToken.None);

        Assert.Equal(
            [Rate(Day(8), 3.086m), Rate(Day(9), 3.055m), Rate(Day(12), 3.041m), Rate(Day(13), 2.75m)],
            await store.ListAsync(Currency.Usd, Currency.Ils, Boi, Day(1), Day(31), CancellationToken.None));
        Assert.Equal(Rate(Day(8), 3.086m), await store.LatestOnOrBeforeAsync(Currency.Usd, Currency.Ils, Boi, Day(8), CancellationToken.None));
        var rows = await db.FxRates.AsNoTracking().OrderBy(r => r.RateDate).ThenBy(r => r.Revision).ToListAsync();
        Assert.Equal(
            [
                (Day(8), 0, 3.084m, null),
                (Day(8), 1, 3.085m, "checked on the bank's page"),
                (Day(8), 2, 3.086m, "and once more"),
                (Day(9), 0, 3.055m, null),
                (Day(12), 0, 3.041m, null),
                (Day(13), 0, 2.75m, "checked on the bank's page"),
            ],
            rows.Select(r => (r.RateDate, r.Revision, r.Rate, r.Reason)));

        // Nothing is accepted without a reason, or with one too long to keep.
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync([], [Rate(Day(9), 3.056m)], " ", Fetched, Note, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AddAsync([], [Rate(Day(9), 3.056m)], new string('x', FxRateStore.MaxReasonLength + 1), Fetched, Note, CancellationToken.None));
        Assert.Equal(6, await db.FxRates.CountAsync());
        // The note about an update is written in the same step as its rates: three writes went through, no more.
        Assert.Equal(3, await db.AuditEvents.CountAsync());
        // And a note with no rates to add is still written.
        await store.AddAsync([], [], null, Fetched, new FxRatesNote("fx-rates", FxRatesNote.Noted, "{\"remarks\":[\"x\"]}"), CancellationToken.None);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Kind == FxRatesNote.Noted && a.Actor == "fx-rates"));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task DatabaseKeepsEveryStoredRateAndRefusesARateThatIsNone()
    {
        var database = await MigratedAsync(pg);
        await using (var db = DbSetup.Create(database))
        {
            await AddAsync(new FxRateStore(db), Rate(Day(8), 3.084m), Rate(Day(9), 3.055m));
        }

        await using var conn = new NpgsqlConnection(database);
        await conn.OpenAsync();
        const string insert = "INSERT INTO fx_rates (base_currency, quote_currency, rate_date, source, revision, rate, fetched_at, reason) VALUES ";
        (string Sql, string SqlState)[] planted =
        [
            // Amounts in shekels were computed with these rows: they are never changed or removed.
            ("UPDATE fx_rates SET rate = 3.5 WHERE rate_date = '2026-10-08'", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM fx_rates WHERE rate_date = '2026-10-08'", PostgresErrorCodes.InsufficientPrivilege),
            ("TRUNCATE fx_rates", PostgresErrorCodes.InsufficientPrivilege),
            (insert + "('USD', 'ILS', '2026-10-13', 'x', 0, 0, now(), NULL)", PostgresErrorCodes.CheckViolation), // no rate
            (insert + "('USD', 'USD', '2026-10-13', 'x', 0, 1, now(), NULL)", PostgresErrorCodes.CheckViolation), // one currency
            (insert + "('USD', 'ILS', '2026-10-13', 'x', 0, 3.05, now(), '  ')", PostgresErrorCodes.CheckViolation), // a reason that says nothing
            (insert + $"('USD', 'ILS', '2026-10-08', '{Boi}', 1, 3.085, now(), NULL)", PostgresErrorCodes.CheckViolation), // a revision without one
            (insert + $"('USD', 'ILS', '2026-10-08', '{Boi}', 2, 3.085, now(), 'skips a revision')", PostgresErrorCodes.CheckViolation),
            (insert + $"('USD', 'ILS', '2026-10-08', '{Boi}', 0, 3.085, now(), NULL)", PostgresErrorCodes.UniqueViolation),
        ];

        foreach (var (sql, sqlState) in planted)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal((sql, sqlState), (sql, error.SqlState));
        }

        await using var check = DbSetup.Create(database);
        Assert.Equal([3.084m, 3.055m], await check.FxRates.OrderBy(r => r.RateDate).Select(r => r.Rate).ToListAsync());
    }

    [Fact]
    public async Task DailyJobOfTheRunningHostLoadsTheRatesAndIsOffUnlessAsked()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 12, 16, 0)); // Monday, 12:00 in New York: the job is due
        var source = new StandInSource(Rate(Day(8), 3.084m), Rate(Day(9), 3.055m));

        await using (var host = await RunningHost.StartAsync(
            database,
            services =>
            {
                services.AddSingleton<IClock>(clock);
                services.AddScoped<IFxRateSource>(_ => source);
            },
            null,
            "--ITrade:UpdateFxRates=true"))
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            await using var db = DbSetup.Create(database);
            while (await db.JobRuns.CountAsync(r => r.Status == JobRunStatus.Succeeded) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            var run = Assert.Single(await db.JobRuns.AsNoTracking().ToListAsync());
            Assert.Equal((FxRatesJob.JobName, new TradeDate(Day(12)), JobRunStatus.Succeeded), (run.Name, run.TradeDate, run.Status));
            Assert.Equal([3.084m, 3.055m], await db.FxRates.OrderBy(r => r.RateDate).Select(r => r.Rate).ToListAsync());

            await using var scope = host.App.Services.CreateAsyncScope();
            var rates = scope.ServiceProvider.GetRequiredService<FxRates>();
            var saturday = await rates.OnAsync(Day(10), CancellationToken.None);
            // Friday's rate, carried one day - and provisional: Monday's rate is not stored yet.
            Assert.Equal((3.055m, 1, true), (saturday.Rate.Rate, saturday.DaysCarried, saturday.IsProvisional));
            await Assert.ThrowsAsync<InvalidOperationException>(() => rates.OnAsync(Day(13), CancellationToken.None)); // tomorrow
        }

        // As every other test runs the Host: no job that would go out to the network.
        await using var quiet = await RunningHost.StartAsync(await pg.NewEmptyDatabaseAsync());
        await using var quietScope = quiet.App.Services.CreateAsyncScope();
        Assert.Empty(quietScope.ServiceProvider.GetServices<IDailyJob>());
        Assert.False(quiet.App.Services.GetRequiredService<ITradeOptions>().UpdateFxRates);
    }

    [Fact]
    public async Task SourceThatIsAwayLeavesTheJobWaitingNotBlockedAndTheRealSchedulerRunsItAgain()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 12, 16, 0));
        var source = new StandInSource(Rate(Day(9), 3.055m))
        {
            Fails = new TemporaryFailureException("The Bank of Israel's rates service could not be reached.", new HttpRequestException("No such host is known.")),
        };

        await using var host = await RunningHost.StartAsync(
            database,
            services =>
            {
                services.AddSingleton<IClock>(clock);
                services.AddScoped<IFxRateSource>(_ => source);
            },
            null,
            "--ITrade:UpdateFxRates=true");
        var scheduler = host.App.Services.GetRequiredService<DailyJobScheduler>();
        await using var db = DbSetup.Create(database);

        // Ten hours without the internet: twice the attempts after which a real fault would block the date.
        for (var hour = 0; hour < 2 * DailyJobScheduler.MaxAttempts; hour++)
        {
            await scheduler.RunDueAsync(CancellationToken.None);
            clock.Advance(DailyJobScheduler.RetryDelay);
        }

        var waiting = await db.JobRuns.AsNoTracking().SingleAsync();
        Assert.Equal((JobRunStatus.Failed, 1), (waiting.Status, waiting.Attempts));
        Assert.Equal("The Bank of Israel's rates service could not be reached. (HttpRequestException: No such host is known.)", waiting.Error);

        source.Fails = null; // the network is back: the next pass loads the rates, with nobody's help
        var result = Assert.Single(await scheduler.RunDueAsync(CancellationToken.None));
        Assert.Equal(JobRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, await db.FxRates.CountAsync());
    }

    [Fact]
    public async Task CommandLoadsTheRatesAndAChangeByTheSourceWaitsForTheOwner()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var dataDir = RunningHost.NewDataDir();
        var source = new StandInSource(Rate(Day(8), 3.084m), Rate(Day(9), 3.055m));
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 12, 16, 0));

        async Task<(int Code, string[] Output)> RunAsync(params string[] args)
        {
            // As Program does it: the command's own arguments first, the settings of the Host after them.
            var (accepted, hostArgs) = UpdateFxRatesCommand.Parse(args);
            await using var app = ITradeHost.Build([.. RunningHost.Args(database, dataDir), .. hostArgs], services =>
            {
                services.AddSingleton<IClock>(clock);
                services.AddScoped<IFxRateSource>(_ => source);
            });
            using var output = new StringWriter();
            var code = await UpdateFxRatesCommand.RunAsync(app, accepted, output);
            return (code, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        var first = await RunAsync();
        Assert.Equal(0, first.Code);
        Assert.Equal(
            [
                "USD/ILS (BOI representative): 2 new; 0 of the last 14 days read again and unchanged.",
                "Latest: 3.055 for 2026-10-09 (no rate for today, 2026-10-12, yet).",
            ],
            first.Output);

        var again = await RunAsync("--ITrade:HeartbeatSeconds=20"); // a setting of the Host passes through
        Assert.Equal("USD/ILS (BOI representative): 0 new; 2 of the last 14 days read again and unchanged.", again.Output[0]);

        source.Fails = new TemporaryFailureException("The Bank of Israel's rates service could not be reached.", new HttpRequestException("No such host is known."));
        var away = await RunAsync();
        Assert.Equal((1, "The exchange rates were not updated: The Bank of Israel's rates service could not be reached. (No such host is known.)"), (away.Code, away.Output[0]));

        // The bank now states another rate for Thursday, and publishes Monday's.
        source.Fails = null;
        source.Published = [Rate(Day(8), 3.085m), Rate(Day(9), 3.055m), Rate(Day(12), 3.041m)];
        var disagrees = await RunAsync();
        Assert.Equal(1, disagrees.Code);
        Assert.Contains("BOI representative now states 3.085 for 2026-10-08; 3.084 is stored. Nothing was stored.", disagrees.Output[0], StringComparison.Ordinal);
        const string toAccept = "To accept exactly this: dotnet run --project src/ITrade.Host -- update-fx-rates --accept-changes ";
        Assert.StartsWith(toAccept, disagrees.Output[1], StringComparison.Ordinal);
        var code = disagrees.Output[1][toAccept.Length..].Split(' ')[0];
        Assert.Matches("^[0-9A-F]{8}$", code);
        await using var db = DbSetup.Create(database);
        Assert.Equal(2, await db.FxRates.CountAsync());

        // An acceptance that says nothing, or too much, explains itself and touches nothing.
        string[][] badOnes =
        [
            ["--accept-changes"],
            ["--accept-changes", code],
            ["--accept-changes", code, " "],
            ["--accept-changes", code, new string('x', 301)],
            ["--accept-changes", code, "checked", "on", "boi.org.il"], // the quotes were forgotten: not half a sentence in a row that stays
            ["--accept-changes", code, "<what you checked>"], // the printed line pasted as it is
        ];
        foreach (var bad in badOnes)
        {
            var usage = await RunAsync(bad);
            Assert.Equal(2, usage.Code);
            Assert.StartsWith("Usage: update-fx-rates --accept-changes <code> \"<what you checked>\"", usage.Output[0], StringComparison.Ordinal);
        }

        // By the time the owner accepts, the bank also shows a slip for today: the code no longer fits.
        source.Published = [Rate(Day(8), 3.085m), Rate(Day(9), 3.055m), Rate(Day(12), 3.550m)];
        var changedSince = await RunAsync("--accept-changes", code, "checked on boi.org.il: 3.085");
        Assert.Equal(1, changedSince.Code);
        Assert.Contains($"is not what was accepted with code {code}", changedSince.Output[0], StringComparison.Ordinal);
        Assert.Contains("the new rate 3.55 of 2026-10-12 moves more than 8%", changedSince.Output[0], StringComparison.Ordinal);
        Assert.Equal(2, await db.FxRates.CountAsync());

        source.Published = [Rate(Day(8), 3.085m), Rate(Day(9), 3.055m), Rate(Day(12), 3.041m)];
        var accepted = await RunAsync("--accept-changes", code, "checked on boi.org.il: 3.085");
        Assert.Equal(0, accepted.Code);
        Assert.Equal(
            [
                "USD/ILS (BOI representative): 1 new; 1 of the last 14 days read again and unchanged.",
                "Latest: 3.041 for 2026-10-12.",
                "Revised 2026-10-08: 3.084 -> 3.085 (the old rate is kept as the earlier revision).",
            ],
            accepted.Output);
        Assert.Equal(
            [(Day(8), 0, 3.084m), (Day(8), 1, 3.085m), (Day(9), 0, 3.055m), (Day(12), 0, 3.041m)],
            (await db.FxRates.AsNoTracking().OrderBy(r => r.RateDate).ThenBy(r => r.Revision).ToListAsync()).Select(r => (r.RateDate, r.Revision, r.Rate)));
        Assert.Equal("checked on boi.org.il: 3.085", (await db.FxRates.SingleAsync(r => r.Revision == 1)).Reason);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Kind == "fx-rates.updated")); // the first load; a run that adds nothing writes nothing
        var audit = await db.AuditEvents.SingleAsync(a => a.Kind == "fx-rates.changes-accepted");
        Assert.Equal("owner", audit.Actor);
        Assert.Contains("checked on boi.org.il: 3.085", audit.Payload, StringComparison.Ordinal);
        Assert.Contains(code, audit.Payload, StringComparison.Ordinal);
        Assert.Contains("3.084", audit.Payload, StringComparison.Ordinal);

        // The same acceptance again has nothing left to accept; the plain update works as before.
        var twice = await RunAsync("--accept-changes", code, "checked on boi.org.il: 3.085");
        Assert.Equal((1, "The exchange rates were not updated: There is nothing to accept: BOI representative and the stored rates agree. Run the plain update."), (twice.Code, twice.Output[0]));
        Assert.Equal(0, (await RunAsync()).Code);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Kind == "fx-rates.changes-accepted"));

        // A rate the bank's answer leaves out is kept and named, and stops nothing.
        clock.AdvanceDays(1);
        source.Published = [Rate(Day(8), 3.085m), Rate(Day(12), 3.041m), Rate(Day(13), 3.05m)];
        var leftOut = await RunAsync();
        Assert.Equal(0, leftOut.Code);
        Assert.Contains("Note: the source did not repeat the stored rate of 2026-10-09 (3.055); it is kept as it is.", leftOut.Output);
        Assert.Equal(0, (await RunAsync()).Code);
        Assert.Equal(3.055m, (await db.FxRates.SingleAsync(r => r.RateDate == Day(9))).Rate);
        // Written down both times - the second run added nothing and still noticed it.
        var noted = await db.AuditEvents.Where(a => a.Kind == "fx-rates.noted").OrderBy(a => a.OccurredAt).ToListAsync();
        Assert.Equal(2, noted.Count);
        Assert.All(noted, n => Assert.Contains("did not repeat the stored rate of 2026-10-09 (3.055)", n.Payload, StringComparison.Ordinal));
        Directory.Delete(dataDir, recursive: true);
    }

    [Fact]
    public async Task CheckShowsAStuckJobAndStaleRatesAndRetryJobReleasesTheJob()
    {
        var database = await MigratedAsync(pg);
        var dataDir = RunningHost.NewDataDir();
        var webRoot = Directory.CreateTempSubdirectory("itrade-web-").FullName;
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html>");
        string[] args = [.. RunningHost.Args(database, dataDir), $"--webroot={webRoot}"];
        // `check` reads the real clock, so the test data is placed around the real today.
        var now = SystemClock.Instance.GetCurrentInstant();
        var today = now.InZone(Zones.Israel).Date;
        var session = new TradeDate(today.PlusDays(-3));
        const string cause = "ITrade.Application.Fx.FxRatesDisagreeException: BOI representative now states 3.085 for 2026-10-08; 3.084 is stored. Nothing was stored. (acceptance code 1A2B3C4D)";

        async Task<(int Code, string Output)> CheckAsync()
        {
            using var output = new StringWriter();
            return (await CheckCommand.RunAsync(args, output), output.ToString());
        }

        async Task<(int Code, string Output)> RetryAsync(string job, string date)
        {
            await using var app = ITradeHost.Build(args);
            using var output = new StringWriter();
            return (await RetryJobCommand.RunAsync(app, job, date, output), output.ToString());
        }

        await using (var app = ITradeHost.Build(args))
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OwnerAccounts>()
                .SetPasswordAsync(Guid.NewGuid().ToString("N"), "test", CancellationToken.None);
        }

        var fresh = await CheckAsync();
        Assert.Equal(0, fresh.Code);
        Assert.Contains("  daily jobs       nothing is stuck", fresh.Output, StringComparison.Ordinal);
        Assert.Contains("  exchange rates   none stored yet", fresh.Output, StringComparison.Ordinal);

        await using (var app = ITradeHost.Build(args))
        {
            await using var scope = app.Services.CreateAsyncScope();
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
            for (var attempt = 0; attempt < DailyJobScheduler.MaxAttempts; attempt++)
            {
                await runs.BeginAsync(FxRatesJob.JobName, session, now, CancellationToken.None);
                await runs.FinishAsync(FxRatesJob.JobName, session, now, cause + "\n   at ...", temporary: false, CancellationToken.None);
            }

            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .AddAsync([Rate(today.PlusDays(-30), 3.2m)], [], null, now, Note, CancellationToken.None);
        }

        var stuck = await CheckAsync();
        Assert.Equal(1, stuck.Code);
        Assert.Contains($"! daily job        fx-rates is blocked on {session} after 5 attempts: {cause} First decide about the rates: dotnet run --project src/ITrade.Host -- update-fx-rates. When the cause is dealt with:", stuck.Output, StringComparison.Ordinal);
        Assert.Contains($"-- retry-job fx-rates {session}", stuck.Output, StringComparison.Ordinal);
        Assert.Contains($"! exchange rates   USD/ILS up to {today.PlusDays(-30):R} (3.2, BOI representative), 30 day(s) old - too old to use.", stuck.Output, StringComparison.Ordinal);
        Assert.EndsWith("PROBLEMS: 2" + Environment.NewLine, stuck.Output, StringComparison.Ordinal);

        Assert.Equal(2, (await RetryAsync(FxRatesJob.JobName, "12.10.2026")).Code);
        var unknown = await RetryAsync("no-such-job", session.ToString());
        Assert.Equal(1, unknown.Code);
        Assert.Contains($"unfinished: fx-rates {session} (Failed, 5 attempts)", unknown.Output, StringComparison.Ordinal);
        var released = await RetryAsync(FxRatesJob.JobName, session.ToString());
        Assert.Equal((0, $"Job 'fx-rates' will be tried again for {session} at the next pass of the Host.{Environment.NewLine}"), released);

        await using (var db = DbSetup.Create(database))
        {
            var run = await db.JobRuns.AsNoTracking().SingleAsync();
            Assert.Equal((0, JobRunStatus.Failed), (run.Attempts, run.Status));
            Assert.Contains("now states 3.085", run.Error, StringComparison.Ordinal); // the cause stays until the next attempt
            Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Kind == "job.retry-requested"));
            await new FxRateStore(db).AddAsync([Rate(today.PlusDays(-6), 3.06m)], [], null, now, Note, CancellationToken.None);
        }

        var waiting = await CheckAsync();
        Assert.Equal(0, waiting.Code);
        Assert.Contains($"  daily job        fx-rates did not succeed for {session} yet (0 attempts, it is tried again)", waiting.Output, StringComparison.Ordinal);
        Assert.Contains($"  exchange rates   USD/ILS up to {today.PlusDays(-6):R} (3.06, BOI representative), 6 day(s) old - older than a long weekend; is the daily job running?", waiting.Output, StringComparison.Ordinal);

        await using (var db = DbSetup.Create(database))
        {
            // What the daily job noticed on its own yesterday is shown for as long as those days are read again.
            var noticed = new FxRatesNote("fx-rates", FxRatesNote.Noted, "{\"remarks\":[\"the source did not repeat the stored rate of 2026-10-07 (3.074); it is kept as it is\"]}");
            await new FxRateStore(db).AddAsync([Rate(today.PlusDays(-1), 3.05m)], [], null, now - Duration.FromDays(1), noticed, CancellationToken.None);
        }

        var last = await CheckAsync();
        Assert.Contains($"  exchange rates   USD/ILS up to {today.PlusDays(-1):R} (3.05, BOI representative), 1 day(s) old{Environment.NewLine}", last.Output, StringComparison.Ordinal);
        Assert.Contains("  exchange rates   note of ", last.Output, StringComparison.Ordinal);
        Assert.Contains(": the source did not repeat the stored rate of 2026-10-07 (3.074); it is kept as it is", last.Output, StringComparison.Ordinal);
        Assert.Equal(0, last.Code);

        Directory.Delete(webRoot, recursive: true);
        Directory.Delete(dataDir, recursive: true);
    }

    /// <summary>A source that answers from a list, or fails as an unreachable one does.</summary>
    private sealed class StandInSource(params FxRate[] published) : IFxRateSource
    {
        public string Name => Boi;

        public Currency BaseCurrency => Currency.Usd;

        public Currency QuoteCurrency => Currency.Ils;

        public IReadOnlyList<FxRate> Published { get; set; } = published;

        public Exception? Fails { get; set; }

        public Task<IReadOnlyList<FxRate>> FetchAsync(LocalDate first, LocalDate last, CancellationToken cancellationToken) =>
            Fails is { } failure
                ? Task.FromException<IReadOnlyList<FxRate>>(failure)
                : Task.FromResult<IReadOnlyList<FxRate>>(Published.Where(r => r.Date >= first && r.Date <= last).ToList());
    }
}
