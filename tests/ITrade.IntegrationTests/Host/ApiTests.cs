using System.Net;
using System.Text;
using System.Text.Json;
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
