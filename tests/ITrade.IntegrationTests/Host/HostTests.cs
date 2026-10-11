using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using ITrade.Host;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ITrade.IntegrationTests.Host;

/// <summary>P2.4.01: the Host starts on 127.0.0.1 only, in PAPER mode, migrates the database and logs no secrets.</summary>
[Collection(PostgresTests.Name)]
public sealed class HostTests(PostgresFixture pg)
{
    [Fact]
    public async Task StartMigratesAnEmptyDatabaseAndHealthReportsPaperMode()
    {
        var connectionString = await pg.NewEmptyDatabaseAsync();
        await using var host = await RunningHost.StartAsync(connectionString);

        using var response = await host.Client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Paper", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Healthy", body.RootElement.GetProperty("checks").GetProperty("database").GetString());
        await using var db = DbSetup.Create(connectionString);
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task ListensOnLoopbackOverHttpsOnly()
    {
        await using var host = await RunningHost.StartAsync(pg.ConnectionString);

        Assert.Equal("https", host.BaseAddress.Scheme);
        Assert.Equal("127.0.0.1", host.BaseAddress.Host);

        // The same port on every other address of this machine (LAN, Wi-Fi, virtual adapters) is closed.
        var others = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();
        foreach (var address in others)
        {
            using var socket = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var error = await Record.ExceptionAsync(
                async () => await socket.ConnectAsync(address, host.BaseAddress.Port, timeout.Token));
            Assert.True(error is SocketException or OperationCanceledException, $"{address} accepted a connection");
        }
    }

    [Fact]
    public async Task HostHeaderOfAnotherSiteIsRejected()
    {
        await using var host = await RunningHost.StartAsync(pg.ConnectionString);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health", UriKind.Relative));
        request.Headers.Host = "evil.example";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LogIsJsonAndHoldsNoSecrets()
    {
        var host = await RunningHost.StartAsync(pg.ConnectionString);
        try
        {
            using var response = await host.Client.GetAsync(new Uri("/health", UriKind.Relative));
            var logger = host.App.Services.GetRequiredService<ILogger<HostTests>>();
#pragma warning disable CA1848, CA2254 // a test log line, written the careless way on purpose
            logger.LogError(
                new InvalidOperationException($"cannot open {pg.ConnectionString}"),
                "Connecting with {ConnectionString}; api_key=K-12345-plain; raw " + pg.ConnectionString,
                pg.ConnectionString);
#pragma warning restore CA1848, CA2254

            var lines = await host.StopAndReadLogAsync();

            Assert.Contains(lines, l => l.Contains("iTrade started", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("/health", StringComparison.Ordinal));
            Assert.All(lines, l =>
            {
                using var json = JsonDocument.Parse(l);
                Assert.Equal("Paper", json.RootElement.GetProperty("Mode").GetString());
                Assert.EndsWith("Z", json.RootElement.GetProperty("@t").GetString(), StringComparison.Ordinal);
            });
            var text = string.Join('\n', lines);
            Assert.DoesNotContain(pg.Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("K-12345-plain", text, StringComparison.Ordinal);
            Assert.Contains("***", text, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("--ITrade:Mode=Live", "pilot phase")]
    [InlineData("--ITrade:Mode=Demo", "Unknown")]
    [InlineData("--ITrade:DataDir=logs-here", "absolute path")]
    [InlineData("--Kestrel:Endpoints:Lan:Url=http://0.0.0.0:5000", "127.0.0.1 only")]
    [InlineData("--ConnectionStrings:ITrade=", "user-secrets")]
    public void RefusesUnsafeOrIncompleteSettings(string setting, string expectedMessage)
    {
        var args = RunningHost.Args(pg.ConnectionString, Path.GetTempPath(), setting);

        var error = Assert.Throws<InvalidOperationException>(() => ITradeHost.Build(args));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataFolderInSettingsMatchesPathsToml()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ITrade.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var toml = File.ReadAllText(Path.Combine(root.FullName, "config", "paths.toml"));
        var expected = Regex.Match(toml, "(?m)^data_dir\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
        using var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root.FullName, "src", "ITrade.Host", "appsettings.json")));
        Assert.Equal(expected, settings.RootElement.GetProperty("ITrade").GetProperty("DataDir").GetString());
    }
}
