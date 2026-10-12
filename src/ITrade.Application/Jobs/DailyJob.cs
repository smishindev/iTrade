using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Jobs;

/// <summary>
/// Work done once per trading session (imports, valuations, checks). If the application was off when a
/// session's job was due, the job runs for that session at the next start (P2.4.05).
/// </summary>
public interface IDailyJob
{
    /// <summary>Stable name; it is the key of the job's runs in the database.</summary>
    string Name { get; }

    /// <summary>New York time on the trade date from which the job may run.</summary>
    LocalTime DueAt { get; }

    /// <summary>
    /// Does the work for one session. Must be idempotent: after a crash in the middle the same date runs
    /// again, and a repeat must not duplicate anything.
    /// </summary>
    Task RunAsync(TradeDate day, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by a job that could not reach something outside the platform (a service is away, the network is
/// down). Trying again later costs nothing and needs nobody, so such a failure does not use up one of the
/// attempts after which a date is blocked: the date is simply tried again every
/// <see cref="DailyJobScheduler.RetryDelay"/>. A fault in what was received is not this — that one is thrown
/// as anything else and does count.
/// </summary>
public sealed class TemporaryFailureException : Exception
{
    public TemporaryFailureException()
    {
    }

    public TemporaryFailureException(string message)
        : base(message)
    {
    }

    public TemporaryFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum JobRunStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>What is recorded about one job on one trade date.</summary>
public sealed record JobRunState(TradeDate Date, JobRunStatus Status, int Attempts, Instant StartedAt);

/// <summary>The record of job runs (table `job_runs`).</summary>
public interface IJobRunStore
{
    /// <summary>The first trade date the job ever ran for; null for a job that never ran.</summary>
    Task<TradeDate?> FirstDateAsync(string job, CancellationToken cancellationToken);

    /// <summary>The latest trade date the job finished successfully; null when there is none.</summary>
    Task<TradeDate?> LastSucceededAsync(string job, CancellationToken cancellationToken);

    Task<JobRunState?> FindAsync(string job, TradeDate day, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the date as running and counts one more attempt. False when the date already succeeded —
    /// it must not run again.
    /// </summary>
    Task<bool> BeginAsync(string job, TradeDate day, Instant now, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the date as succeeded (no failure text) or failed. A <paramref name="temporary"/> failure does not
    /// count: the attempt just begun is taken back (the count never goes below one, so the date still waits
    /// for its next try).
    /// </summary>
    Task FinishAsync(
        string job, TradeDate day, Instant now, string? failure, bool temporary, CancellationToken cancellationToken);

    /// <summary>
    /// Lets a date that did not succeed be tried again at once, with all its attempts back: the owner's answer
    /// to a blocked date once its cause is gone. False when there is no such unfinished date.
    /// </summary>
    Task<bool> RetryAsync(string job, TradeDate day, CancellationToken cancellationToken);

    /// <summary>The dates that did not succeed (failed, blocked or interrupted), earliest first, with their causes.</summary>
    Task<IReadOnlyList<(string Job, JobRunState Run, string? Failure)>> ListUnfinishedAsync(CancellationToken cancellationToken);
}
