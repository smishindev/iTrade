using System.Net;
using ITrade.Host.Web;
using ITrade.IntegrationTests.Persistence;

namespace ITrade.IntegrationTests.Host;

/// <summary>P2.5.06: one Host process serves the web interface and the API from the same origin.</summary>
[Collection(PostgresTests.Name)]
public sealed class WebUiTests(PostgresFixture pg) : IDisposable
{
    private const string Page = "<!doctype html><html><body><div id=\"root\">test page</div></body></html>";
    private const string Script = "console.log('test script')";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("itrade-web-").FullName;

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private async Task<RunningHost> StartAsync(bool built = true)
    {
        if (built)
        {
            Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
            await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), Page);
            await File.WriteAllTextAsync(Path.Combine(_webRoot, "assets", "index-abc123.js"), Script);
        }

        return await RunningHost.StartAsync(pg.ConnectionString, null, null, $"--webroot={_webRoot}");
    }

    private static Uri At(string path) => new(path, UriKind.Relative);

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/taxes")]
    [InlineData("/positions/VOO/lots")]
    public async Task EveryScreenAddressGetsThePageWithoutSignIn(string address)
    {
        await using var host = await StartAsync();

        using var response = await host.Client.GetAsync(At(address));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Page, await response.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task HashedAssetsAreServedAndCachedForGood()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.GetAsync(At("/assets/index-abc123.js"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Script, await response.Content.ReadAsStringAsync());
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/assets/missing.js")]
    [InlineData("/favicon.ico")]
    public async Task MissingFileIsNotAnsweredWithThePage(string address)
    {
        await using var host = await StartAsync();

        using var response = await host.Client.GetAsync(At(address));

        // A file is not a screen: the gate answers as for any unknown address, never with HTML.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("test page", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/portfolio")]
    [InlineData("/hubs/unknown")]
    [InlineData("/openapi/v9.json")]
    public async Task ServerAddressesAreNeverPages(string address)
    {
        await using var host = await StartAsync();
        var secret = Guid.NewGuid().ToString("N");
        await host.SetOwnerSecretAsync(secret);

        using var anonymous = await host.Client.GetAsync(At(address));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var login = await RunningHost.LoginAsync(host.Client, secret);
        using var signedIn = await host.Client.GetAsync(At(address));

        Assert.Equal(HttpStatusCode.NotFound, signedIn.StatusCode);
        Assert.NotEqual("text/html", signedIn.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("test page", await signedIn.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/assets/index-abc123.js")]
    [InlineData("/api/auth/session")]
    public async Task EveryAnswerCarriesTheSecurityHeaders(string address)
    {
        await using var host = await StartAsync();

        using var response = await host.Client.GetAsync(At(address));

        Assert.Equal(WebUi.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("frame-ancestors 'none'", WebUi.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", WebUi.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", WebUi.ContentSecurityPolicy, StringComparison.Ordinal); // scripts fall under default-src 'self'
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task WithoutABuiltInterfaceTheHostSaysWhatToDoAndTheApiStillWorks()
    {
        await using var host = await StartAsync(built: false);

        using var page = await host.Client.GetAsync(At("/"));
        using var health = await host.Client.GetAsync(At("/health"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
        Assert.Contains("npm run build", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
