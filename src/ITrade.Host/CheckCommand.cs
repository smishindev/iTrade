using ITrade.Application.Fx;
using ITrade.Application.Jobs;
using ITrade.Host.Auth;
using ITrade.Host.Jobs;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Host;

/// <summary>
/// `ITrade.Host check [--offline]`: builds the Host exactly as a normal start does — same environment, settings
/// and service checks — and says whether it can run: the interface is in place, the database answers, the
/// owner's password is set, no daily job is stuck and the exchange rates are fresh. It changes nothing (no migration is applied) and does not listen on any port.
/// `--offline` skips the database, so the check can run where there is none (the CI script).
/// </summary>
internal static class CheckCommand
{
    public const string Name = "check";
    public const string Offline = "--offline";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        var offline = args.Contains(Offline, StringComparer.OrdinalIgnoreCase);
        var hostArgs = args.Where(a => !string.Equals(a, Offline, StringComparison.OrdinalIgnoreCase)).ToArray();
        var problems = 0;

        void Line(string name, string value) => output.WriteLine($"  {name,-16} {value}");

        void Problem(string name, string value)
        {
            problems++;
            output.WriteLine($"! {name,-16} {value}");
        }

        WebApplication app;
        try
        {
            app = ITradeHost.Build(hostArgs);
        }
#pragma warning disable CA1031 // whatever stops the Host from being built is the finding of this command
        catch (Exception e)
#pragma warning restore CA1031
        {
            Problem("start", $"the Host cannot be built - {e.GetType().Name}: {e.Message}");
            await output.WriteLineAsync("PROBLEMS: 1");
            return 1;
        }

        await using (app)
        {
            var options = app.Services.GetRequiredService<ITradeOptions>();
            Line("environment", app.Environment.EnvironmentName);
            Line("mode", options.Mode.ToString().ToUpperInvariant());
            Line("address", $"https://127.0.0.1:{options.Port}");
            var dataDir = Path.GetFullPath(options.DataDir);
            Line("data folder", Directory.Exists(dataDir) ? dataDir : $"{dataDir} (will be created)");

            if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
            {
                Line("interface", "built");
            }
            else
            {
                Problem("interface", "not built - run `npm run build` in web/, then build the Host");
            }

            if (offline)
            {
                Line("database", "not checked (--offline)");
            }
            else
            {
                await CheckDatabaseAsync(app, Line, Problem);
            }
        }

        await output.WriteLineAsync(problems == 0 ? "OK" : $"PROBLEMS: {problems}");
        return problems == 0 ? 0 : 1;
    }

    private static async Task CheckDatabaseAsync(
        WebApplication app, Action<string, string> line, Action<string, string> problem)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ITradeDbContext>();
        try
        {
            if (!await db.Database.CanConnectAsync())
            {
                problem("database", "not reachable - is the container running? (docker compose -f deploy/docker-compose.yml up -d)");
                return;
            }

            var applied = (await db.Database.GetAppliedMigrationsAsync()).Count();
            var pending = (await db.Database.GetPendingMigrationsAsync()).Count();
            line("database", pending == 0
                ? $"reachable, up to date ({applied} migrations)"
                : $"reachable, {pending} migration(s) will be applied at the next start");
            if (applied == 0)
            {
                line("owner password", "not set yet - run `set-password` after the first start");
                return;
            }

            var account = await scope.ServiceProvider.GetRequiredService<OwnerAccounts>().FindAsync(CancellationToken.None);
            if (account is null)
            {
                problem("owner password", "not set - run: dotnet run --project src/ITrade.Host -- set-password");
            }
            else
            {
                line("owner password", "set");
            }

            if (pending == 0)
            {
                await CheckDailyWorkAsync(scope.ServiceProvider, line, problem);
            }
        }
#pragma warning disable CA1031 // a database error is reported as the finding
        catch (Exception e)
#pragma warning restore CA1031
        {
            problem("database", $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>What the daily jobs left behind: a date that is stuck, and how fresh the exchange rates are.</summary>
    private static async Task CheckDailyWorkAsync(
        IServiceProvider services, Action<string, string> line, Action<string, string> problem)
    {
        var unfinished = await services.GetRequiredService<IJobRunStore>().ListUnfinishedAsync(CancellationToken.None);
        foreach (var (job, run, failure) in unfinished)
        {
            var cause = failure?.Split('\n', 2)[0].Trim() ?? "interrupted";
            if (run.Attempts >= DailyJobScheduler.MaxAttempts)
            {
                // What the failure itself asks for comes first; the retry only helps once that is done.
                var first = cause.Contains("acceptance code", StringComparison.Ordinal)
                    ? $" First decide about the rates: dotnet run --project src/ITrade.Host -- {Fx.UpdateFxRatesCommand.Name}."
                    : "";
                problem("daily job", $"{job} is blocked on {run.Date} after {run.Attempts} attempts: {cause}{first} " +
                    $"When the cause is dealt with: dotnet run --project src/ITrade.Host -- {RetryJobCommand.Name} {job} {run.Date}");
            }
            else
            {
                line("daily job", $"{job} did not succeed for {run.Date} yet ({run.Attempts} attempts, it is tried again): {cause}");
            }
        }

        if (unfinished.Count == 0)
        {
            line("daily jobs", "nothing is stuck");
        }

        var source = services.GetRequiredService<IFxRateSource>();
        var now = services.GetRequiredService<IClock>().GetCurrentInstant();
        var today = now.InZone(ITrade.Domain.Zones.Israel).Date;
        // What the daily job noticed while nobody was looking: for as long as the days in question are read again.
        var since = now - Duration.FromDays(FxRates.OverlapDays);
        var noted = await services.GetRequiredService<ITradeDbContext>().AuditEvents.AsNoTracking()
            .Where(a => a.Kind == FxRatesNote.Noted && a.OccurredAt >= since)
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync();
        if (noted is not null)
        {
            using var payload = System.Text.Json.JsonDocument.Parse(noted.Payload);
            foreach (var remark in payload.RootElement.GetProperty("remarks").EnumerateArray())
            {
                line("exchange rates", $"note of {noted.OccurredAt.InZone(ITrade.Domain.Zones.Israel).Date:R}: {remark.GetString()}");
            }
        }

        var latest = await services.GetRequiredService<IFxRateStore>()
            .LatestOnOrBeforeAsync(source.BaseCurrency, source.QuoteCurrency, source.Name, today, CancellationToken.None);
        var pair = $"{source.BaseCurrency}/{source.QuoteCurrency}";
        if (latest is null)
        {
            line("exchange rates", $"none stored yet - the daily job loads them, or run: dotnet run --project src/ITrade.Host -- {Fx.UpdateFxRatesCommand.Name}");
        }
        else
        {
            // The bank is silent for 4 days at most around a weekend with a holiday on each side of it; the
            // longest silence since 2005 is 7 days. Past that the rates are not being updated.
            var age = Period.DaysBetween(latest.Date, today);
            var text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{pair} up to {latest.Date:R} ({latest.Rate:0.######}, {source.Name}), {age} day(s) old");
            if (age > FxRates.MaxDaysCarried)
            {
                problem("exchange rates", $"{text} - too old to use. Run: dotnet run --project src/ITrade.Host -- {Fx.UpdateFxRatesCommand.Name}");
            }
            else
            {
                line("exchange rates", age > 4 ? $"{text} - older than a long weekend; is the daily job running?" : text);
            }
        }
    }
}
