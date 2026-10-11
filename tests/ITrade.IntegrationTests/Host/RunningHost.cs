using ITrade.Host;
using Microsoft.AspNetCore.Builder;

namespace ITrade.IntegrationTests.Host;

/// <summary>
/// The real Host (Kestrel, HTTPS, logging) on a free loopback port, with its own temporary data folder.
/// The "Testing" environment keeps the owner's user-secrets out, so a test can only reach the given database.
/// </summary>
internal sealed class RunningHost : IAsyncDisposable
{
    private RunningHost(WebApplication app, string dataDir)
    {
        App = app;
        DataDir = dataDir;
        BaseAddress = new Uri(app.Urls.Single());
        // The developer certificate is not necessarily trusted on this machine; accept it for localhost only.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Subject == "CN=localhost",
        };
        Client = new HttpClient(handler) { BaseAddress = BaseAddress };
    }

    public WebApplication App { get; }

    public string DataDir { get; }

    public Uri BaseAddress { get; }

    public HttpClient Client { get; }

    public static string[] Args(string connectionString, string dataDir, params string[] extra) =>
    [
        $"--environment={ITradeHost.TestingEnvironment}",
        "--ITrade:Port=0",
        $"--ITrade:DataDir={dataDir}",
        $"--ConnectionStrings:{ITradeHost.ConnectionStringName}={connectionString}",
        .. extra,
    ];

    public static string NewDataDir() => Directory.CreateTempSubdirectory("itrade-host-").FullName;

    public static async Task<RunningHost> StartAsync(string connectionString)
    {
        var dataDir = NewDataDir();
        var app = ITradeHost.Build(Args(connectionString, dataDir));
        await ITradeHost.StartAsync(app);
        return new RunningHost(app, dataDir);
    }

    /// <summary>Stops the host (flushing its log) and returns every line it wrote to the log folder.</summary>
    public async Task<IReadOnlyList<string>> StopAndReadLogAsync()
    {
        await App.DisposeAsync();
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(DataDir, "logs"), "itrade-*.json"))
        {
            lines.AddRange(await File.ReadAllLinesAsync(file));
        }

        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        Directory.Delete(DataDir, recursive: true);
    }
}
