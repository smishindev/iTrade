using System.Collections.Concurrent;
using ITrade.Application.Jobs;
using ITrade.Domain;
using ITrade.Infrastructure.Calendar;
using ITrade.Infrastructure.Persistence;

namespace ITrade.Host.Jobs;

/// <summary>
/// Runs the daily jobs: once right at the start — that is the catch-up for sessions missed while the
/// application was off — and then on every tick.
/// </summary>
internal sealed partial class JobsService(DailyJobScheduler scheduler, ITradeOptions options, ILogger<JobsService> logger)
    : BackgroundService
{
    /// <summary>A blocked date is reported once, not on every tick (it stays blocked until someone looks).</summary>
    private readonly ConcurrentDictionary<(string Job, TradeDate Date), bool> _reportedBlocked = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.JobsTickSeconds));
        do
        {
            try
            {
                foreach (var result in await scheduler.RunDueAsync(stoppingToken))
                {
                    Report(result);
                }
            }
#pragma warning disable CA1031 // e.g. the database is away: the next tick tries again
            // Anything but the application stopping. A cancellation that is not ours (a timeout somewhere below)
            // must not end this service: an exception leaving it would stop the whole Host.
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogPassFailed(e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Report(JobRunResult result)
    {
        switch (result.Outcome)
        {
            case JobRunOutcome.Succeeded:
                _reportedBlocked.TryRemove((result.Job, result.Date), out _);
                LogSucceeded(result.Job, result.Date);
                break;
            case JobRunOutcome.Failed:
                LogFailed(result.Job, result.Date, result.Failure);
                break;
            case JobRunOutcome.Blocked when _reportedBlocked.TryAdd((result.Job, result.Date), true):
                LogBlocked(result.Job, result.Date, DailyJobScheduler.MaxAttempts);
                break;
            default:
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job} done for {TradeDate}")]
    private partial void LogSucceeded(string job, TradeDate tradeDate);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Job} failed for {TradeDate}: {Failure}")]
    private partial void LogFailed(string job, TradeDate tradeDate, string? failure);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job {Job} is blocked on {TradeDate} after {Attempts} failed attempts; later dates wait for it")]
    private partial void LogBlocked(string job, TradeDate tradeDate, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "The pass over the daily jobs failed")]
    private partial void LogPassFailed(Exception exception);
}

internal static class JobsSetup
{
    /// <summary>The scheduler and its background service. The jobs themselves arrive with later phases.</summary>
    public static void AddDailyJobs(this IServiceCollection services)
    {
        services.AddSingleton<IExchangeCalendar, WeekdayCalendar>();
        services.AddScoped<IJobRunStore, JobRunStore>();
        services.AddSingleton<DailyJobScheduler>();
        services.AddHostedService<JobsService>();
    }
}
