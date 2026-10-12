using ITrade.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace ITrade.Application.Jobs;

public enum JobRunOutcome
{
    Succeeded,

    /// <summary>The job threw; it is tried again after <see cref="DailyJobScheduler.RetryDelay"/>.</summary>
    Failed,

    /// <summary>A failed date is waiting for its next attempt.</summary>
    Waiting,

    /// <summary>The date failed <see cref="DailyJobScheduler.MaxAttempts"/> times and needs the owner's attention.</summary>
    Blocked,
}

/// <summary>What one pass did about one job and trade date.</summary>
public sealed record JobRunResult(string Job, TradeDate Date, JobRunOutcome Outcome, string? Failure = null);

/// <summary>
/// Runs every daily job for every session that is due and not done yet — including sessions missed while the
/// application was off. Per job the sessions go in calendar order and a date that does not succeed holds back
/// the later ones (a later day's accounting builds on the earlier day's). A job that never ran starts with the
/// latest due session. Every run gets its own service scope, so a failed run leaves nothing behind for the next.
/// </summary>
public sealed class DailyJobScheduler(IServiceScopeFactory scopes, IExchangeCalendar calendar, IClock clock)
    : IDisposable
{
    /// <summary>One pass at a time: a second caller waits, then finds the work done.</summary>
    private readonly SemaphoreSlim _pass = new(1, 1);

    public const int MaxAttempts = 5;

    private const int MaxFailureLength = 4000;

    /// <summary>How far back to look for the latest session (longer than any exchange closure).</summary>
    private const int LookBackDays = 14;

    public static readonly Duration RetryDelay = Duration.FromHours(1);

    /// <summary>One pass over all jobs. Returns what was done; an empty list means nothing was due.</summary>
    public async Task<IReadOnlyList<JobRunResult>> RunDueAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken);
        try
        {
            List<(string Name, LocalTime DueAt)> jobs;
            await using (var scope = scopes.CreateAsyncScope())
            {
                jobs = scope.ServiceProvider.GetServices<IDailyJob>().Select(j => (j.Name, j.DueAt)).ToList();
            }

            var results = new List<JobRunResult>();
            foreach (var (name, dueAt) in jobs)
            {
                await CatchUpAsync(name, dueAt, results, cancellationToken);
            }

            return results;
        }
        finally
        {
            _pass.Release();
        }
    }

    public void Dispose() => _pass.Dispose();

    /// <summary>The instant from which a job with this due time may run for the session.</summary>
    public static Instant DueInstant(TradeDate day, LocalTime dueAt) =>
        day.Value.At(dueAt).InZoneLeniently(Zones.NewYork).ToInstant();

    private async Task CatchUpAsync(
        string name, LocalTime dueAt, List<JobRunResult> results, CancellationToken cancellationToken)
    {
        if (LatestDueSession(dueAt, clock.GetCurrentInstant()) is not { } latest)
        {
            return;
        }

        TradeDate day;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
            day = await store.LastSucceededAsync(name, cancellationToken) is { } done
                ? calendar.NextSession(done)
                : await store.FirstDateAsync(name, cancellationToken) ?? latest;
        }

        for (; day <= latest; day = calendar.NextSession(day))
        {
            var result = await RunOneAsync(name, day, cancellationToken);
            if (result is null)
            {
                continue;
            }

            results.Add(result);
            if (result.Outcome != JobRunOutcome.Succeeded)
            {
                return;
            }
        }
    }

    /// <summary>Runs one job for one session; null when that session is already done.</summary>
    private async Task<JobRunResult?> RunOneAsync(string name, TradeDate day, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
        var now = clock.GetCurrentInstant();
        var earlier = await store.FindAsync(name, day, cancellationToken);
        if (earlier is { Status: JobRunStatus.Succeeded })
        {
            return null;
        }

        if (earlier is { Attempts: >= MaxAttempts })
        {
            return new JobRunResult(name, day, JobRunOutcome.Blocked);
        }

        // An interrupted run (still "running" after a crash) is repeated at once; a failed one waits.
        if (earlier is { Status: JobRunStatus.Failed } && now < earlier.StartedAt + RetryDelay)
        {
            return new JobRunResult(name, day, JobRunOutcome.Waiting);
        }

        if (!await store.BeginAsync(name, day, now, cancellationToken))
        {
            return null;
        }

        var job = scope.ServiceProvider.GetServices<IDailyJob>().Single(j => j.Name == name);
        string? failure = null;
        try
        {
            await job.RunAsync(day, cancellationToken);
        }
#pragma warning disable CA1031 // any failure of a job is recorded with the run; the scheduler carries on
        // Only a stop of the application is a cancellation. A timeout inside the job (an HTTP call that gives
        // up throws TaskCanceledException too) is a failure like any other and must be recorded.
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            // Type, message, inner exceptions and stack: "see the inner exception" alone explains nothing later.
            failure = e.ToString();
            failure = failure.Length <= MaxFailureLength ? failure : failure[..MaxFailureLength];
        }

        // The work is done (or has failed): record it even if the application is stopping right now,
        // otherwise a finished date would run again at the next start.
        await store.FinishAsync(name, day, clock.GetCurrentInstant(), failure, CancellationToken.None);
        return new JobRunResult(
            name, day, failure is null ? JobRunOutcome.Succeeded : JobRunOutcome.Failed, failure);
    }

    private TradeDate? LatestDueSession(LocalTime dueAt, Instant now)
    {
        var today = TradeDate.Of(now);
        for (var back = 0; back <= LookBackDays; back++)
        {
            var day = new TradeDate(today.Value.PlusDays(-back));
            if (calendar.IsSession(day) && DueInstant(day, dueAt) <= now)
            {
                return day;
            }
        }

        return null;
    }
}
