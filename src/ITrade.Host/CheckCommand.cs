using ITrade.Application.Fx;
using ITrade.Application.Jobs;
using ITrade.Application.Prices;
using ITrade.Host.Auth;
using ITrade.Host.Jobs;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Host;

/// <summary>
/// `ITrade.Host check [--offline]`: builds the Host exactly as a normal start does — same environment, settings
/// and service checks — and says whether it can run: the interface is in place, the database answers, the
/// owner's password is set, no daily job is stuck, and how fresh the exchange rates and the closing prices
/// are. It changes nothing (no migration is applied) and does not listen on any port.
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

    /// <summary>
    /// What the daily jobs left behind: a date that is stuck, how fresh the exchange rates are — and how fresh
    /// the closing prices, which no job brings.
    /// </summary>
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

        // Closing prices come from the data pipeline, which nothing runs by itself: their age is shown, never
        // a reason to refuse a start - without a fresh price a valuation says "unavailable", it does not guess.
        var prices = services.GetRequiredService<IClosingPriceStore>();
        var marketToday = ITrade.Domain.TradeDate.Of(now).Value;
        var lastSession = await prices.LatestSessionOnOrBeforeAsync(ClosingPrices.Calendar, marketToday, CancellationToken.None);
        var lastCloses = await prices.LatestPerInstrumentAsync(CancellationToken.None);
        // The funds of the catalogue are the pipeline's universe etf_pullback_v1; its plain ingest fetches other ones.
        const string refresh = $"uv run --directory python itrade ingest --universe etf_pullback_v1, then dotnet run --project src/ITrade.Host -- {Prices.ImportPricesCommand.Name}";
        if (lastSession is null)
        {
            line("closing prices", lastCloses.Count == 0
                ? $"none stored yet - {refresh}"
                : $"stored without the sessions of the exchange, so only a session's own close can be used - {refresh}");
            return;
        }

        var daysOld = Period.DaysBetween(lastSession.Value, marketToday);
        line("closing prices", daysOld > ClosingPrices.MostDaysCarriedUnconfirmed
            ? $"up to session {lastSession.Value:R}, {daysOld} day(s) old - too old to value today with: {refresh}"
            : $"up to session {lastSession.Value:R}, {daysOld} day(s) old");
        // An instrument whose last close is of an earlier session has no price from then on: said here, so it
        // is not first met as a hole in a valuation.
        var behind = (await services.GetRequiredService<ITrade.Application.Portfolio.IInstrumentStore>().ListAsync(CancellationToken.None))
            .Where(i => lastCloses.TryGetValue(i.Id, out var last) && last < lastSession.Value)
            .Select(i => $"{i.Ticker} ({lastCloses[i.Id]:R})")
            .Order(StringComparer.Ordinal)
            .ToList();
        if (behind.Count > 0)
        {
            line("closing prices", $"behind, last close in brackets: {string.Join(", ", behind)} - no price to value them with after that");
        }

        // What the owner withdrew is out of every valuation and index: said, so it is not forgotten.
        var (sessionsWithdrawn, eventsWithdrawn) = await prices.CountWithdrawnAsync(CancellationToken.None);
        if (sessionsWithdrawn + eventsWithdrawn > 0)
        {
            var days = (await prices.ListWithdrawnSessionsAsync(ClosingPrices.Calendar, CancellationToken.None))
                .Select(s => s.Session.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var named = days.Count == 0 ? "" : $" ({string.Join(", ", days)})";
            line("closing prices", $"withdrawn by the owner: {sessionsWithdrawn} session(s){named}, {eventsWithdrawn} dividend(s) or split(s) - closed days and events that did not happen, from then on");
        }
    }
}
