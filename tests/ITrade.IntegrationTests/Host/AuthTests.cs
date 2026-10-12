using System.Net;
using ITrade.Host.Auth;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.IntegrationTests.Host;

/// <summary>P2.4.02: one owner, cookie session, antiforgery on every changing request, lockout after 5 failures.</summary>
[Collection(PostgresTests.Name)]
public sealed class AuthTests(PostgresFixture pg)
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 10, 12, 8, 0));

    private static string NewSecret() => Guid.NewGuid().ToString("N");

    /// <summary>The system time moved forward by <see cref="Shift"/>: what the cookie's lifetime is measured with.</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }

    /// <summary>A host on its own empty database, with the test clock.</summary>
    private async Task<RunningHost> StartAsync() => await RunningHost.StartAsync(
        await pg.NewEmptyDatabaseAsync(), services => services.AddSingleton<IClock>(_clock));

    [Fact]
    public async Task WithoutSignInEverythingButHealthAndSessionIs401()
    {
        await using var host = await StartAsync();
        var (signedIn, token) = await RunningHost.SessionAsync(host.Client);
        Assert.False(signedIn);

        using var page = await host.Client.GetAsync(new Uri("/api/portfolio", UriKind.Relative));
        using var logout = await RunningHost.PostAsync(host.Client, "/api/auth/logout", null, token);
        using var health = await host.Client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Null(page.Headers.Location);
        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ChangingRequestWithoutAntiforgeryTokenIs400()
    {
        await using var host = await StartAsync();
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        await RunningHost.SessionAsync(host.Client);

        using var noToken = await RunningHost.PostAsync(host.Client, "/api/auth/login", new LoginRequest(secret), null);
        using var madeUp = await RunningHost.PostAsync(host.Client, "/api/auth/login", new LoginRequest(secret), "made-up");

        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, madeUp.StatusCode);
        Assert.False((await RunningHost.SessionAsync(host.Client)).SignedIn);
    }

    [Fact]
    public async Task SignInGivesAStrictSecureHttpOnlyCookieAndSignOutEndsTheSession()
    {
        await using var host = await StartAsync();
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);

        using var login = await RunningHost.LoginAsync(host.Client, secret);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("itrade.session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var (signedIn, token) = await RunningHost.SessionAsync(host.Client);
        Assert.True(signedIn);
        Assert.False((await RunningHost.SessionAsync(host.NewClient())).SignedIn);

        using var withoutToken = await RunningHost.PostAsync(host.Client, "/api/auth/logout", null, null);
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        Assert.True((await RunningHost.SessionAsync(host.Client)).SignedIn);

        using var logout = await RunningHost.PostAsync(host.Client, "/api/auth/logout", null, token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False((await RunningHost.SessionAsync(host.Client)).SignedIn);
    }

    [Fact]
    public async Task AntiforgeryTokenOfAnotherBrowserIsRejected()
    {
        await using var host = await StartAsync();
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        using var login = await RunningHost.LoginAsync(host.Client, secret);
        var (_, foreignToken) = await RunningHost.SessionAsync(host.NewClient());

        using var logout = await RunningHost.PostAsync(host.Client, "/api/auth/logout", null, foreignToken);

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.True((await RunningHost.SessionAsync(host.Client)).SignedIn);
    }

    [Fact]
    public async Task FiveWrongPasswordsLockTheAccountFor15Minutes()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var host = await RunningHost.StartAsync(database, services => services.AddSingleton<IClock>(_clock));
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);

        // Four failures, then a success: the count starts again.
        for (var i = 0; i < OwnerAccount.MaxFailedAttempts - 1; i++)
        {
            using var wrong = await RunningHost.LoginAsync(host.NewClient(), NewSecret());
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var right = await RunningHost.LoginAsync(host.NewClient(), secret);
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);

        for (var i = 0; i < OwnerAccount.MaxFailedAttempts; i++)
        {
            using var wrong = await RunningHost.LoginAsync(host.NewClient(), NewSecret());
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Locked: even the right password is refused, and the wait is announced.
        using var locked = await RunningHost.LoginAsync(host.NewClient(), secret);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(TimeSpan.FromMinutes(15), locked.Headers.RetryAfter?.Delta);

        _clock.Advance(OwnerAccount.LockoutDuration - Duration.FromSeconds(1));
        using var stillLocked = await RunningHost.LoginAsync(host.NewClient(), secret);
        Assert.Equal(HttpStatusCode.TooManyRequests, stillLocked.StatusCode);

        _clock.Advance(Duration.FromSeconds(1));
        using var afterWait = await RunningHost.LoginAsync(host.NewClient(), secret);
        Assert.Equal(HttpStatusCode.NoContent, afterWait.StatusCode);

        await using var db = DbSetup.Create(database);
        var kinds = await db.AuditEvents.OrderBy(a => a.Id).Select(a => a.Kind).ToListAsync();
        Assert.Equal(1, kinds.Count(k => k == "auth.password_set"));
        Assert.Equal(8, kinds.Count(k => k == "auth.login_failed"));
        Assert.Equal(1, kinds.Count(k => k == "auth.locked"));
        Assert.Equal(2, kinds.Count(k => k == "auth.login"));
    }

    [Fact]
    public async Task ParallelWrongPasswordsCannotPassTheLimit()
    {
        await using var host = await StartAsync();
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        var clients = Enumerable.Range(0, 12).Select(_ => host.NewClient()).ToList();
        var tokens = new List<string>();
        foreach (var client in clients)
        {
            tokens.Add((await RunningHost.SessionAsync(client)).Token);
        }

        var responses = await Task.WhenAll(clients.Select((client, i) =>
            RunningHost.PostAsync(client, "/api/auth/login", new LoginRequest(NewSecret()), tokens[i])));

        Assert.Equal(OwnerAccount.MaxFailedAttempts, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        Assert.Equal(12 - OwnerAccount.MaxFailedAttempts, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task NewPasswordEndsExistingSessionsAndTheOldOneStopsWorking()
    {
        await using var host = await StartAsync();
        var old = NewSecret();
        await host.SetOwnerSecretAsync(old);
        using var login = await RunningHost.LoginAsync(host.Client, old);
        Assert.True((await RunningHost.SessionAsync(host.Client)).SignedIn);

        var changed = NewSecret();
        await host.SetOwnerSecretAsync(changed);

        Assert.False((await RunningHost.SessionAsync(host.Client)).SignedIn);
        using var withOld = await RunningHost.LoginAsync(host.NewClient(), old);
        using var withNew = await RunningHost.LoginAsync(host.NewClient(), changed);
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, withNew.StatusCode);
    }

    [Fact]
    public async Task SignOutEndsACopiedSessionTooAndIsRecorded()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var host = await RunningHost.StartAsync(database, services => services.AddSingleton<IClock>(_clock));
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        using var login = await RunningHost.LoginAsync(host.Client, secret);
        // The session cookie taken out of the browser, as a thief or a second device would hold it.
        var copy = host.NewClient();
        foreach (Cookie cookie in host.CookiesOf(host.Client).GetCookies(host.BaseAddress))
        {
            host.CookiesOf(copy).Add(host.BaseAddress, new Cookie(cookie.Name, cookie.Value, cookie.Path));
        }

        Assert.True((await RunningHost.SessionAsync(copy)).SignedIn);

        var (_, token) = await RunningHost.SessionAsync(host.Client);
        using var logout = await RunningHost.PostAsync(host.Client, "/api/auth/logout", null, token);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False((await RunningHost.SessionAsync(copy)).SignedIn);
        using var again = await RunningHost.LoginAsync(host.NewClient(), secret);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        await using var db = DbSetup.Create(database);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Kind == "auth.logout"));
    }

    [Fact]
    public async Task SessionEndsTwelveHoursAfterSignInEvenWhileTheTabKeepsAsking()
    {
        var time = new ShiftedTime();
        await using var host = await RunningHost.StartAsync(await pg.NewEmptyDatabaseAsync(), services =>
        {
            services.AddSingleton<IClock>(_clock);
            services.AddSingleton<TimeProvider>(time);
        });
        var secret = NewSecret();
        await host.SetOwnerSecretAsync(secret);
        using var login = await RunningHost.LoginAsync(host.Client, secret);

        // An open tab asks the server all day long; that must not renew the session.
        for (var hour = 1; hour <= 11; hour++)
        {
            time.Shift = TimeSpan.FromHours(hour);
            Assert.True((await RunningHost.SessionAsync(host.Client)).SignedIn, $"signed out after {hour} h");
        }

        time.Shift = AuthSetup.SessionLifetime + TimeSpan.FromMinutes(1);

        Assert.False((await RunningHost.SessionAsync(host.Client)).SignedIn);
    }

    [Fact]
    public async Task NobodyCanSignInBeforeAPasswordIsSet()
    {
        await using var host = await StartAsync();

        using var login = await RunningHost.LoginAsync(host.Client, NewSecret());

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task SetPasswordCommandChecksTheInputAndStoresOnlyAHash()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var host = await RunningHost.StartAsync(database);
        var secret = NewSecret();
        using var output = new StringWriter();

        Assert.Equal(1, await SetPasswordCommand.RunAsync(host.App, output, _ => "short", confirm: false));
        var answers = new Queue<string>([secret, NewSecret()]);
        Assert.Equal(1, await SetPasswordCommand.RunAsync(host.App, output, _ => answers.Dequeue(), confirm: true));
        using var beforeSet = await RunningHost.LoginAsync(host.NewClient(), secret);
        Assert.Equal(HttpStatusCode.Unauthorized, beforeSet.StatusCode);

        Assert.Equal(0, await SetPasswordCommand.RunAsync(host.App, output, _ => secret, confirm: true));

        using var login = await RunningHost.LoginAsync(host.NewClient(), secret);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
        await using var db = DbSetup.Create(database);
        var stored = await db.Settings.SingleAsync(s => s.Key == OwnerAccounts.Key);
        Assert.DoesNotContain(secret, stored.Value, StringComparison.Ordinal);
    }
}
