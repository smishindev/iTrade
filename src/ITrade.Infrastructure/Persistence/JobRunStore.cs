using ITrade.Application.Jobs;
using ITrade.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Infrastructure.Persistence;

/// <summary>
/// `job_runs` behind <see cref="IJobRunStore"/>. Writes are single statements that bypass the change tracker,
/// so nothing a job left unsaved in the context can ride along.
/// </summary>
public sealed class JobRunStore(ITradeDbContext db) : IJobRunStore
{
    public async Task<TradeDate?> FirstDateAsync(string job, CancellationToken cancellationToken)
    {
        var first = await db.JobRuns.AsNoTracking()
            .Where(r => r.Name == job)
            .OrderBy(r => r.TradeDate)
            .FirstOrDefaultAsync(cancellationToken);
        return first?.TradeDate;
    }

    public async Task<TradeDate?> LastSucceededAsync(string job, CancellationToken cancellationToken)
    {
        var last = await db.JobRuns.AsNoTracking()
            .Where(r => r.Name == job && r.Status == JobRunStatus.Succeeded)
            .OrderByDescending(r => r.TradeDate)
            .FirstOrDefaultAsync(cancellationToken);
        return last?.TradeDate;
    }

    public async Task<JobRunState?> FindAsync(string job, TradeDate day, CancellationToken cancellationToken)
    {
        var run = await db.JobRuns.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Name == job && r.TradeDate == day, cancellationToken);
        return run is null ? null : new JobRunState(run.TradeDate, run.Status, run.Attempts, run.StartedAt);
    }

    public async Task<bool> BeginAsync(string job, TradeDate day, Instant now, CancellationToken cancellationToken)
    {
        // One statement: a new row, or one more attempt on an unfinished one. A succeeded date is never reopened.
        var rows = await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO job_runs (name, trade_date, status, attempts, started_at)
             VALUES ({job}, {day.Value}, {nameof(JobRunStatus.Running)}, 1, {now})
             ON CONFLICT (name, trade_date) DO UPDATE
             SET status = EXCLUDED.status, attempts = job_runs.attempts + 1, started_at = EXCLUDED.started_at,
                 finished_at = NULL, error = NULL
             WHERE job_runs.status <> {nameof(JobRunStatus.Succeeded)}
             """,
            cancellationToken);
        return rows == 1;
    }

    public async Task FinishAsync(
        string job, TradeDate day, Instant now, string? failure, CancellationToken cancellationToken)
    {
        var status = failure is null ? JobRunStatus.Succeeded : JobRunStatus.Failed;
        await db.JobRuns
            .Where(r => r.Name == job && r.TradeDate == day)
            .ExecuteUpdateAsync(
                run => run
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.FinishedAt, (Instant?)now)
                    .SetProperty(r => r.Error, failure),
                cancellationToken);
    }
}
