using ITrade.Application.Jobs;
using ITrade.Domain;
using ITrade.Infrastructure.Calendar;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.UnitTests.Application;

/// <summary>
/// P2.4.05. October 2026: the 5th is a Monday; New York is on UTC−4, so a job due at 18:00 may run from 22:00 UTC.
/// </summary>
public sealed class DailyJobSchedulerTests : IDisposable
{
    private const string JobName = "daily";

    private readonly FakeClock _clock = new(Utc(5, 23));
    private readonly MemoryStore _store = new();
    private readonly List<TradeDate> _calls = [];
    private readonly List<object> _scopes = [];
    private readonly DailyJobScheduler _scheduler;
    private Func<TradeDate, bool> _fails = _ => false;
    private Func<TradeDate, Exception?> _failure = _ => null;

    public DailyJobSchedulerTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJobRunStore>(_store);
        services.AddScoped<ScopeMarker>();
        services.AddScoped<IDailyJob>(provider => new TestJob(day =>
        {
            _calls.Add(day);
            _scopes.Add(provider.GetRequiredService<ScopeMarker>());
            if (_fails(day))
            {
                throw new InvalidOperationException($"no data for {day}");
            }

            if (_failure(day) is { } error)
            {
                throw error;
            }
        }));
        _scheduler = new DailyJobScheduler(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new WeekdayCalendar(), _clock);
    }

    public void Dispose() => _scheduler.Dispose();

    private static Instant Utc(int day, int hour, int minute = 0) => Instant.FromUtc(2026, 10, day, hour, minute);

    private static TradeDate Day(int day) => new(new LocalDate(2026, 10, day));

    private Task<IReadOnlyList<JobRunResult>> PassAsync() => _scheduler.RunDueAsync(CancellationToken.None);

    [Fact]
    public async Task JobThatNeverRanStartsWithTheLatestDueSessionOnly()
    {
        _clock.Reset(Utc(7, 23));

        var results = await PassAsync();

        Assert.Equal([Day(7)], _calls);
        Assert.Equal([new JobRunResult(JobName, Day(7), JobRunOutcome.Succeeded)], results);
    }

    [Fact]
    public async Task SessionIsNotDueBeforeItsNewYorkTime()
    {
        await PassAsync();
        Assert.Equal([Day(5)], _calls);

        _clock.Reset(Utc(6, 21, 59));
        Assert.Empty(await PassAsync());

        _clock.Reset(Utc(6, 22));
        await PassAsync();
        Assert.Equal([Day(5), Day(6)], _calls);
    }

    [Fact]
    public async Task MissedSessionsAreCaughtUpInOrderExactlyOnce()
    {
        await PassAsync();
        _clock.Reset(Utc(8, 13)); // Thursday morning in New York: Tuesday and Wednesday were missed

        await PassAsync();
        Assert.Empty(await PassAsync());

        Assert.Equal([Day(5), Day(6), Day(7)], _calls);
        Assert.All(_store.Runs.Values, run => Assert.Equal((JobRunStatus.Succeeded, 1), (run.Status, run.Attempts)));
    }

    [Fact]
    public async Task WeekendsAreNotSessions()
    {
        _clock.Reset(Utc(9, 23));
        await PassAsync();
        _clock.Reset(Utc(13, 12)); // Tuesday before the due time

        await PassAsync();

        Assert.Equal([Day(9), Day(12)], _calls);
    }

    [Fact]
    public async Task FailedDateHoldsBackLaterOnesAndIsRetriedAfterTheDelay()
    {
        await PassAsync();
        _clock.Reset(Utc(8, 13));
        _fails = day => day == Day(6);

        var failed = await PassAsync();
        var waiting = await PassAsync();

        Assert.Equal(JobRunOutcome.Failed, Assert.Single(failed).Outcome);
        Assert.Contains("no data for 2026-10-06", failed[0].Failure, StringComparison.Ordinal);
        Assert.Equal([new JobRunResult(JobName, Day(6), JobRunOutcome.Waiting)], waiting);
        Assert.Equal([Day(5), Day(6)], _calls);

        _fails = _ => false;
        _clock.Advance(DailyJobScheduler.RetryDelay);
        await PassAsync();

        Assert.Equal([Day(5), Day(6), Day(6), Day(7)], _calls);
        Assert.Equal(2, _store.Runs[(JobName, Day(6))].Attempts);
        Assert.Equal(JobRunStatus.Succeeded, _store.Runs[(JobName, Day(6))].Status);
    }

    [Fact]
    public async Task DateThatKeepsFailingIsBlockedAfterFiveAttempts()
    {
        _fails = _ => true;
        for (var attempt = 0; attempt < DailyJobScheduler.MaxAttempts; attempt++)
        {
            Assert.Equal(JobRunOutcome.Failed, Assert.Single(await PassAsync()).Outcome);
            _clock.Advance(DailyJobScheduler.RetryDelay);
        }

        _clock.Reset(Utc(8, 13));
        var results = await PassAsync();

        Assert.Equal([new JobRunResult(JobName, Day(5), JobRunOutcome.Blocked)], results);
        Assert.Equal(DailyJobScheduler.MaxAttempts, _calls.Count);
        Assert.All(_calls, day => Assert.Equal(Day(5), day));
    }

    [Fact]
    public async Task RunInterruptedByACrashIsRepeatedAtOnce()
    {
        _store.Runs[(JobName, Day(5))] = new JobRunState(Day(5), JobRunStatus.Running, 1, Utc(5, 22, 30));

        await PassAsync();

        Assert.Equal([Day(5)], _calls);
        Assert.Equal((JobRunStatus.Succeeded, 2), (_store.Runs[(JobName, Day(5))].Status, _store.Runs[(JobName, Day(5))].Attempts));
    }

    [Fact]
    public async Task TimeoutInsideAJobIsAFailureNotAShutdown()
    {
        // An HTTP call that gives up throws TaskCanceledException although nobody is stopping the application.
        _failure = _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var result = Assert.Single(await PassAsync());

        Assert.Equal(JobRunOutcome.Failed, result.Outcome);
        Assert.Contains("HttpClient.Timeout", result.Failure, StringComparison.Ordinal);
        Assert.Equal(JobRunStatus.Failed, _store.Runs[(JobName, Day(5))].Status);
    }

    [Fact]
    public async Task StoppingTheApplicationInterruptsTheRunAndLeavesItToBeRepeated()
    {
        using var stopping = new CancellationTokenSource();
        _failure = _ =>
        {
            stopping.Cancel();
            return new OperationCanceledException(stopping.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scheduler.RunDueAsync(stopping.Token));

        Assert.Equal(JobRunStatus.Running, _store.Runs[(JobName, Day(5))].Status);
    }

    [Fact]
    public async Task FailureIsRecordedWithItsCause()
    {
        _failure = _ => new InvalidOperationException("saving failed", new FormatException("bad amount '12,5'"));

        var result = Assert.Single(await PassAsync());

        Assert.Contains("InvalidOperationException", result.Failure, StringComparison.Ordinal);
        Assert.Contains("bad amount '12,5'", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassesDoNotOverlap()
    {
        _clock.Reset(Utc(8, 13));
        _store.Runs[(JobName, Day(5))] = new JobRunState(Day(5), JobRunStatus.Succeeded, 1, Utc(5, 23));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(PassAsync)));

        Assert.Equal([Day(6), Day(7)], _calls);
    }

    [Fact]
    public async Task EveryRunGetsItsOwnServiceScope()
    {
        await PassAsync();
        _clock.Reset(Utc(8, 13));

        await PassAsync();

        Assert.Equal(3, _scopes.Distinct().Count());
    }

    private sealed class ScopeMarker;

    private sealed class TestJob(Action<TradeDate> run) : IDailyJob
    {
        public string Name => JobName;

        public LocalTime DueAt => new(18, 0);

        public Task RunAsync(TradeDate day, CancellationToken cancellationToken)
        {
            run(day);
            return Task.CompletedTask;
        }
    }

    /// <summary>The store's contract, in memory.</summary>
    private sealed class MemoryStore : IJobRunStore
    {
        public Dictionary<(string Job, TradeDate Day), JobRunState> Runs { get; } = [];

        public Task<TradeDate?> FirstDateAsync(string job, CancellationToken cancellationToken) =>
            Task.FromResult(Dates(job, _ => true).Order().Cast<TradeDate?>().FirstOrDefault());

        public Task<TradeDate?> LastSucceededAsync(string job, CancellationToken cancellationToken) =>
            Task.FromResult(Dates(job, s => s == JobRunStatus.Succeeded).OrderDescending().Cast<TradeDate?>().FirstOrDefault());

        public Task<JobRunState?> FindAsync(string job, TradeDate day, CancellationToken cancellationToken) =>
            Task.FromResult(Runs.GetValueOrDefault((job, day)));

        public Task<bool> BeginAsync(string job, TradeDate day, Instant now, CancellationToken cancellationToken)
        {
            var earlier = Runs.GetValueOrDefault((job, day));
            if (earlier?.Status == JobRunStatus.Succeeded)
            {
                return Task.FromResult(false);
            }

            Runs[(job, day)] = new JobRunState(day, JobRunStatus.Running, (earlier?.Attempts ?? 0) + 1, now);
            return Task.FromResult(true);
        }

        public Task FinishAsync(string job, TradeDate day, Instant now, string? failure, CancellationToken cancellationToken)
        {
            Runs[(job, day)] = Runs[(job, day)] with
            {
                Status = failure is null ? JobRunStatus.Succeeded : JobRunStatus.Failed,
            };
            return Task.CompletedTask;
        }

        private IEnumerable<TradeDate> Dates(string job, Func<JobRunStatus, bool> status) =>
            Runs.Where(r => r.Key.Job == job && status(r.Value.Status)).Select(r => r.Key.Day);
    }
}
