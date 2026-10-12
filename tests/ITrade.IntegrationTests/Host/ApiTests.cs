using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ITrade.Host;
using ITrade.Host.Auth;
using ITrade.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Builder;

namespace ITrade.IntegrationTests.Host;

/// <summary>P2.4.03: the OpenAPI document (after sign-in) and one error format — problem details.</summary>
[Collection(PostgresTests.Name)]
public sealed class ApiTests(PostgresFixture pg)
{
    private const string Detail = "boom-detail-that-must-stay-inside";
    private const string UpdateVariable = "ITRADE_UPDATE_OPENAPI";

    private static string NewSecret() => Guid.NewGuid().ToString("N");

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal((int)expected, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("title").GetString()));
        Assert.DoesNotContain(Detail, body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApiDocumentIsServedAfterSignInOnly()
    {
        await using var host = await RunningHost.StartAsync(await pg.NewEmptyDatabaseAsync());
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        var path = new Uri($"/openapi/{ITradeHost.ApiVersion}.json", UriKind.Relative);

        using var anonymous = await host.Client.GetAsync(path);
        await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized);

        using var login = await RunningHost.LoginAsync(host.Client, secret);
        using var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var info = document.RootElement.GetProperty("info");
        Assert.Equal("iTrade API", info.GetProperty("title").GetString());
        Assert.Equal(ITradeHost.ApiVersion, info.GetProperty("version").GetString());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/auth/session", out _));
        Assert.True(paths.TryGetProperty("/api/auth/login", out var loginPath));
        Assert.True(loginPath.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/auth/logout", out _));
    }

    /// <summary>
    /// The web client is generated from web/openapi/v1.json. This test keeps that file equal to what the Host
    /// serves: after an API change run it once with ITRADE_UPDATE_OPENAPI=1, then `npm run gen:api` in web/.
    /// </summary>
    [Fact]
    public async Task OpenApiDocumentInTheRepositoryIsCurrent()
    {
        await using var host = await RunningHost.StartAsync(await pg.NewEmptyDatabaseAsync());
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        using var login = await RunningHost.LoginAsync(host.Client, secret);
        var served = await host.Client.GetStringAsync(
            new Uri($"/openapi/{ITradeHost.ApiVersion}.json", UriKind.Relative));
        var current = JsonNode.Parse(served)!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
        var path = Repository.PathTo("web", "openapi", $"{ITradeHost.ApiVersion}.json");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, current);
            return;
        }

        var committed = File.Exists(path)
            ? (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal)
            : "";
        Assert.True(
            committed == current,
            $"The API changed: {path} is out of date. Run this test once with {UpdateVariable}=1, " +
            "then `npm run gen:api` in web/, and commit both files.");
    }

    [Fact]
    public async Task EveryErrorIsProblemDetailsWithoutInternals()
    {
        await using var host = await RunningHost.StartAsync(
            await pg.NewEmptyDatabaseAsync(),
            configureApp: app => app.MapGet("/api/test/boom", string () => throw new InvalidOperationException(Detail)));
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        var (_, anonymousToken) = await RunningHost.SessionAsync(host.Client);

        using var notSignedIn = await host.Client.GetAsync(new Uri("/api/test/boom", UriKind.Relative));
        await AssertProblemAsync(notSignedIn, HttpStatusCode.Unauthorized);

        using var noToken = await RunningHost.PostAsync(host.Client, "/api/auth/login", new LoginRequest(secret), null);
        await AssertProblemAsync(noToken, HttpStatusCode.BadRequest);

        using var wrong = await RunningHost.PostAsync(
            host.Client, "/api/auth/login", new LoginRequest(NewSecret()), anonymousToken);
        await AssertProblemAsync(wrong, HttpStatusCode.Unauthorized);

        using var malformed = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/login", UriKind.Relative))
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        };
        malformed.Headers.Add(AuthSetup.AntiforgeryHeader, anonymousToken);
        using var badBody = await host.Client.SendAsync(malformed);
        await AssertProblemAsync(badBody, HttpStatusCode.BadRequest);

        using var login = await RunningHost.LoginAsync(host.Client, secret);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        using var missing = await host.Client.GetAsync(new Uri("/api/nothing-here", UriKind.Relative));
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);

        using var failed = await host.Client.GetAsync(new Uri("/api/test/boom", UriKind.Relative));
        await AssertProblemAsync(failed, HttpStatusCode.InternalServerError);

        // The cause of the failure is in the log (for the owner), not in the answer.
        var log = string.Join('\n', await host.StopAndReadLogAsync());
        Assert.Contains(Detail, log, StringComparison.Ordinal);
    }
}
