using ITrade.Host;
using ITrade.Host.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

// The real Host for the browser tests (web/e2e, P2.5.07): a throw-away PostgreSQL in Docker, a temporary data
// folder, its own port, and an owner secret handed over by the test run. The "Testing" environment keeps the
// owner's user-secrets out, so this process cannot reach the owner's database.
const string SecretVariable = "ITRADE_E2E_SECRET";
const string PortVariable = "ITRADE_E2E_PORT";

var secret = Environment.GetEnvironmentVariable(SecretVariable);
if (string.IsNullOrEmpty(secret))
{
    await Console.Error.WriteLineAsync($"{SecretVariable} is not set; this program is started by `npm run e2e` in web/.");
    return 2;
}

var port = Environment.GetEnvironmentVariable(PortVariable) ?? "7199";
await using var postgres = new PostgreSqlBuilder("postgres:18")
    .WithDatabase("itrade")
    .WithUsername("itrade")
    .WithPassword(Guid.NewGuid().ToString("N"))
    .Build();
await postgres.StartAsync();

var dataDir = Directory.CreateTempSubdirectory("itrade-e2e-").FullName;
await using var app = ITradeHost.Build(
[
    $"--environment={ITradeHost.TestingEnvironment}",
    $"--ITrade:Port={port}",
    $"--ITrade:DataDir={dataDir}",
    $"--ConnectionStrings:{ITradeHost.ConnectionStringName}={postgres.GetConnectionString()}",
]);
await ITradeHost.MigrateAsync(app);
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<OwnerAccounts>()
        .SetPasswordAsync(secret, "e2e", CancellationToken.None);
}

await ITradeHost.StartAsync(app);
await app.WaitForShutdownAsync();
return 0;
