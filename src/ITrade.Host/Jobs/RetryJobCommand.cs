using ITrade.Application.Jobs;
using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Text;

namespace ITrade.Host.Jobs;

/// <summary>
/// `ITrade.Host retry-job &lt;name&gt; &lt;yyyy-MM-dd&gt;`: lets a daily job be tried again for a date that did not
/// succeed. A date that failed <see cref="DailyJobScheduler.MaxAttempts"/> times is blocked and holds back the
/// later ones until the owner has looked at the cause; this is how the owner says "the cause is gone". The
/// Host picks the date up at its next pass (within a minute when it is running, otherwise at the next start).
/// </summary>
internal static class RetryJobCommand
{
    public const string Name = "retry-job";

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="job">The name of the job, as `check` and the log show it.</param>
    /// <param name="date">The trade date, yyyy-MM-dd.</param>
    /// <param name="output">Where the answer goes.</param>
    public static async Task<int> RunAsync(WebApplication app, string job, string date, TextWriter output)
    {
        var parsed = LocalDatePattern.Iso.Parse(date);
        if (!parsed.Success)
        {
            await output.WriteLineAsync($"'{date}' is not a date; write it as yyyy-MM-dd, e.g. 2026-10-12.");
            return 2;
        }

        var day = new TradeDate(parsed.Value);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
        if (!await store.RetryAsync(job, day, CancellationToken.None))
        {
            await output.WriteLineAsync($"Job '{job}' has no unfinished run for {day}: nothing to retry.");
            foreach (var (name, run, _) in await store.ListUnfinishedAsync(CancellationToken.None))
            {
                await output.WriteLineAsync($"  unfinished: {name} {run.Date} ({run.Status}, {run.Attempts} attempts)");
            }

            return 1;
        }

        var db = scope.ServiceProvider.GetRequiredService<ITradeDbContext>();
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant(),
            Actor = "cli",
            Kind = "job.retry-requested",
            Payload = System.Text.Json.JsonSerializer.Serialize(new { job, date = day.ToString() }),
        });
        await db.SaveChangesAsync();
        await output.WriteLineAsync($"Job '{job}' will be tried again for {day} at the next pass of the Host.");
        return 0;
    }
}
