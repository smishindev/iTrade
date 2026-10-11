using System.Net.Http.Json;
using System.Text.Json;
using ITrade.Host;
using ITrade.Host.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ITrade.IntegrationTests.Host;

/// <summary>
/// The real Host (Kestrel, HTTPS, logging) on a free loopback port, with its own temporary data folder.
/// The "Testing" environment keeps the owner's user-secrets out, so a test can only reach the given database.
/// </summary>
internal sealed class RunningHost : IAsyncDisposable
{
    private readonly List<HttpClient> _clients = [];

    private RunningHost(WebApplication app, string dataDir)
    {
        App = app;
        DataDir = dataDir;
        BaseAddress = new Uri(app.Urls.Single());
        Client = NewClient();
    }

    public WebApplication App { get; }

    public string DataDir { get; }

    public Uri BaseAddress { get; }

    /// <summary>One "browser": it keeps its cookies between requests.</summary>
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

    public static async Task<RunningHost> StartAsync(
        string connectionString, Action<IServiceCollection>? configureServices = null)
    {
        var dataDir = NewDataDir();
        var app = ITradeHost.Build(Args(connectionString, dataDir), configureServices);
        await ITradeHost.StartAsync(app);
        return new RunningHost(app, dataDir);
    }

    /// <summary>Another "browser" with no cookies.</summary>
    public HttpClient NewClient()
    {
        // The developer certificate is not necessarily trusted on this machine; accept it for localhost only.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Subject == "CN=localhost",
        };
        var client = new HttpClient(handler) { BaseAddress = BaseAddress };
        _clients.Add(client);
        return client;
    }

    public async Task SetOwnerSecretAsync(string secret)
    {
        await using var scope = App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OwnerAccounts>()
            .SetPasswordAsync(secret, "test", CancellationToken.None);
    }

    /// <summary>The session as the client sees it: signed in or not, and a fresh antiforgery token.</summary>
    public static async Task<(bool SignedIn, string Token)> SessionAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/auth/session", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            json.RootElement.GetProperty("authenticated").GetBoolean(),
            json.RootElement.GetProperty("antiforgeryToken").GetString()!);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Content = body is null ? null : JsonContent.Create(body);
        if (token is not null)
        {
            request.Headers.Add(AuthSetup.AntiforgeryHeader, token);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Signs in with a fresh antiforgery token.</summary>
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string secret)
    {
        var (_, token) = await SessionAsync(client);
        return await PostAsync(client, "/api/auth/login", new LoginRequest(secret), token);
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
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await App.DisposeAsync();
        Directory.Delete(DataDir, recursive: true);
    }
}
