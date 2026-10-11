using System.Collections.Concurrent;
using ITrade.Application.Jobs;
using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.IntegrationTests.Host;

/// <summary>
/// P2.4.05 on the real Host and database: a start after missed sessions runs the job for each of them once.
/// October 2026: the 5th is a Monday; a job due at 18:00 New York may run from 22:00 UTC.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class JobsTests(PostgresFixture pg)
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 10, 5, 23, 0));
    private readonly ConcurrentQueue<TradeDate> _calls = new();
    private bool _fail;

    private static TradeDate Day(int day) => new(new LocalDate(2026, 10, day));

    private Task<RunningHost> StartAsync(string database) => RunningHost.StartAsync(database, services =>
    {
        services.AddSingleton<IClock>(_clock);
        services.AddScoped<IDailyJob>(_ => new RecordingJob(this));
    });

    private async Task WaitForCallsAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (_calls.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(count, _calls.Count);
    }

    [Fact]
    public async Task StartAfterTwoMissedSessionsRunsEachOfThemExactlyOnce()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using (await StartAsync(database))
        {
            await WaitForCallsAsync(1); // Monday evening: the first run of a new job
        }

        // The application was off on Tuesday and Wednesday; it starts on Thursday morning (New York).
        _clock.Reset(Instant.FromUtc(2026, 10, 8, 13, 0));
        await using (var host = await StartAsync(database))
        {
            await WaitForCallsAsync(3);
            var scheduler = host.App.Services.GetRequiredService<DailyJobScheduler>();
            Assert.Empty(await scheduler.RunDueAsync(CancellationToken.None));
        }

        await using (var host = await StartAsync(database))
        {
            var scheduler = host.App.Services.GetRequiredService<DailyJobScheduler>();
            Assert.Empty(await scheduler.RunDueAsync(CancellationToken.None));
        }

        Assert.Equal([Day(5), Day(6), Day(7)], _calls);
        await using var db = DbSetup.Create(database);
        var runs = await db.JobRuns.OrderBy(r => r.TradeDate).ToListAsync();
        Assert.Equal([Day(5), Day(6), Day(7)], runs.Select(r => r.TradeDate));
        Assert.All(runs, run =>
        {
            Assert.Equal(RecordingJob.JobName, run.Name);
            Assert.Equal(JobRunStatus.Succeeded, run.Status);
            Assert.Equal(1, run.Attempts);
            Assert.NotNull(run.FinishedAt);
            Assert.Null(run.Error);
        });
    }

    [Fact]
    public async Task FailureIsRecordedAndTheDateRunsAgainUntilItSucceeds()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        _fail = true;
        await using var host = await StartAsync(database);
        await WaitForCallsAsync(1);
        var scheduler = host.App.Services.GetRequiredService<DailyJobScheduler>();
        await using var db = DbSetup.Create(database);

        // The start-up pass has recorded the failure by the time a second pass answers "waiting".
        var waiting = Assert.Single(await scheduler.RunDueAsync(CancellationToken.None));
        Assert.Equal(JobRunOutcome.Waiting, waiting.Outcome);
        var failed = await db.JobRuns.AsNoTracking().SingleAsync();
        Assert.Equal((JobRunStatus.Failed, 1), (failed.Status, failed.Attempts));
        Assert.Contains("the source is away", failed.Error, StringComparison.Ordinal);

        _fail = false;
        _clock.Advance(DailyJobScheduler.RetryDelay);
        var retried = Assert.Single(await scheduler.RunDueAsync(CancellationToken.None));

        Assert.Equal(JobRunOutcome.Succeeded, retried.Outcome);
        var done = await db.JobRuns.AsNoTracking().SingleAsync();
        Assert.Equal((JobRunStatus.Succeeded, 2), (done.Status, done.Attempts));
        Assert.Null(done.Error);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task FinishedDateCannotBeStartedAgain()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var db = DbSetup.Create(database);
        await db.Database.MigrateAsync();
        var store = new JobRunStore(db);
        var now = _clock.GetCurrentInstant();

        Assert.True(await store.BeginAsync("job", Day(5), now, CancellationToken.None));
        Assert.True(await store.BeginAsync("job", Day(5), now, CancellationToken.None)); // interrupted, started again
        await store.FinishAsync("job", Day(5), now, null, CancellationToken.None);

        Assert.False(await store.BeginAsync("job", Day(5), now, CancellationToken.None));
        var state = await store.FindAsync("job", Day(5), CancellationToken.None);
        Assert.Equal((JobRunStatus.Succeeded, 2), (state!.Status, state.Attempts));
        Assert.Equal(Day(5), await store.FirstDateAsync("job", CancellationToken.None));
        Assert.Equal(Day(5), await store.LastSucceededAsync("job", CancellationToken.None));
        Assert.Null(await store.LastSucceededAsync("another", CancellationToken.None));
    }

    private sealed class RecordingJob(JobsTests test) : IDailyJob
    {
        public const string JobName = "recording";

        public string Name => JobName;

        public LocalTime DueAt => new(18, 0);

        public Task RunAsync(TradeDate day, CancellationToken cancellationToken)
        {
            test._calls.Enqueue(day);
            return test._fail
                ? Task.FromException(new InvalidOperationException("the source is away"))
                : Task.CompletedTask;
        }
    }
}
