using System.Net;
using System.Text.Json;
using ITrade.Host.Auth;
using ITrade.Host.Events;
using ITrade.IntegrationTests.Persistence;
using Microsoft.AspNetCore.SignalR.Client;
using NodaTime.Text;

namespace ITrade.IntegrationTests.Host;

/// <summary>P2.4.04: the events hub serves the signed-in owner from the same origin only.</summary>
[Collection(PostgresTests.Name)]
public sealed class EventsTests(PostgresFixture pg)
{
    private static string NewSecret() => Guid.NewGuid().ToString("N");

    private async Task<RunningHost> StartAsync() => await RunningHost.StartAsync(
        await pg.NewEmptyDatabaseAsync(), null, null, "--ITrade:HeartbeatSeconds=1");

    /// <summary>A hub connection that shares the cookies of <paramref name="client"/>, like a page of that browser.</summary>
    private static HubConnection Connect(RunningHost host, HttpClient client, string? token, string? origin = null) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(host.BaseAddress, EventsHub.Path), connection =>
            {
                connection.Cookies = host.CookiesOf(client);
                if (token is not null)
                {
                    connection.Headers[AuthSetup.AntiforgeryHeader] = token;
                }

                if (origin is not null)
                {
                    connection.Headers["Origin"] = origin;
                }

                // The developer certificate is not necessarily trusted on this machine; accept it for localhost only.
                connection.HttpMessageHandlerFactory = handler =>
                {
                    ((HttpClientHandler)handler).ServerCertificateCustomValidationCallback =
                        (_, certificate, _, _) => certificate?.Subject == "CN=localhost";
                    return handler;
                };
                connection.WebSocketConfiguration = socket =>
                    socket.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.Subject == "CN=localhost";
            })
            .Build();

    private static async Task<string> SignInAsync(RunningHost host)
    {
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        using var login = await RunningHost.LoginAsync(host.Client, secret);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return (await RunningHost.SessionAsync(host.Client)).Token;
    }

    [Fact]
    public async Task ConnectionWithoutSignInIsRefused()
    {
        await using var host = await StartAsync();
        var (_, token) = await RunningHost.SessionAsync(host.Client);
        await using var connection = Connect(host, host.Client, token);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task SignedInOwnerReceivesHeartbeats()
    {
        await using var host = await StartAsync();
        var token = await SignInAsync(host);
        var ownOrigin = host.BaseAddress.GetLeftPart(UriPartial.Authority);
        await using var connection = Connect(host, host.Client, token, ownOrigin);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(EventsHub.Heartbeat, heartbeat => received.TrySetResult(heartbeat));

        await connection.StartAsync();
        var heartbeat = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("Paper", heartbeat.GetProperty("mode").GetString());
        Assert.True(InstantPattern.ExtendedIso.Parse(heartbeat.GetProperty("at").GetString()!).Success);
    }

    [Fact]
    public async Task OpenConnectionIsClosedWhenTheSessionEnds()
    {
        await using var host = await StartAsync();
        var token = await SignInAsync(host);
        await using var connection = Connect(host, host.Client, token);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        var firstBeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(EventsHub.Heartbeat, _ => firstBeat.TrySetResult());
        await connection.StartAsync();
        await firstBeat.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // The owner signs out in the browser: the connection opened under that session must not live on.
        using var logout = await RunningHost.PostAsync(host.Client, "/api/auth/logout", null, token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task PageOfAnotherOriginIsRefusedEvenWithTheOwnersCookie()
    {
        await using var host = await StartAsync();
        var token = await SignInAsync(host);
        // Another site on the same machine: for the browser it is the same "site", so the cookie would be sent.
        await using var connection = Connect(host, host.Client, token, "https://127.0.0.1:3000");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task NegotiationWithoutAntiforgeryTokenIsRefused()
    {
        await using var host = await StartAsync();
        await SignInAsync(host);
        await using var connection = Connect(host, host.Client, token: null);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }
}
