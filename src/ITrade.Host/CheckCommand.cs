using ITrade.Host.Auth;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ITrade.Host;

/// <summary>
/// `ITrade.Host check [--offline]`: builds the Host exactly as a normal start does — same environment, settings
/// and service checks — and says whether it can run: the interface is in place, the database answers, the
/// owner's password is set. It changes nothing (no migration is applied) and does not listen on any port.
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
        }
#pragma warning disable CA1031 // a database error is reported as the finding
        catch (Exception e)
#pragma warning restore CA1031
        {
            problem("database", $"{e.GetType().Name}: {e.Message}");
        }
    }
}
