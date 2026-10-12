using System.Collections.Concurrent;
using System.Security.Claims;
using ITrade.Host.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NodaTime;
using NodaTime.Text;

namespace ITrade.Host.Events;

/// <summary>
/// Server → browser events (P2.4.04). Signed-in owner only; a browser page of another origin is refused,
/// because cookies of `localhost` are shared between ports and a WebSocket carries no antiforgery header.
/// </summary>
[Authorize]
internal sealed class EventsHub(EventConnections connections) : Hub
{
    public const string Path = "/hubs/events";
    public const string Heartbeat = "heartbeat";

    public override Task OnConnectedAsync()
    {
        connections.Add(Context);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// The open event connections with the session each was opened under. A connection is checked when it is
/// opened, but it then lives for hours: when the session ends (sign-out, new password) it has to be closed.
/// </summary>
internal sealed class EventConnections
{
    private readonly ConcurrentDictionary<string, (HubCallerContext Context, string? Stamp)> _open = new();

    public int Count => _open.Count;

    public void Add(HubCallerContext context) =>
        _open[context.ConnectionId] = (context, context.User?.FindFirstValue(AuthSetup.StampClaim));

    public void Remove(string connectionId) => _open.TryRemove(connectionId, out _);

    /// <summary>Closes every connection that was not opened under the current session stamp.</summary>
    public int CloseAllBut(string? currentStamp)
    {
        var closed = 0;
        foreach (var (id, entry) in _open)
        {
            if (currentStamp is null || entry.Stamp != currentStamp)
            {
                entry.Context.Abort();
                _open.TryRemove(id, out _);
                closed++;
            }
        }

        return closed;
    }
}

/// <summary>"The server is alive": its time (UTC, ISO 8601) and mode.</summary>
internal sealed record HeartbeatEvent(string At, string Mode);

internal sealed partial class HeartbeatService(
    IHubContext<EventsHub> hub,
    EventConnections connections,
    IServiceScopeFactory scopes,
    IClock clock,
    ITradeOptions options,
    ILogger<HeartbeatService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CloseEndedSessionsAsync(stoppingToken);
                var heartbeat = new HeartbeatEvent(
                    InstantPattern.ExtendedIso.Format(clock.GetCurrentInstant()), options.Mode.ToString());
                await hub.Clients.All.SendAsync(EventsHub.Heartbeat, heartbeat, stoppingToken);
            }
#pragma warning disable CA1031 // e.g. the database is away: the next tick tries again
            // An exception leaving a background service stops the whole Host; a missed heartbeat must not.
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogTickFailed(e);
            }
        }
    }

    private async Task CloseEndedSessionsAsync(CancellationToken cancellationToken)
    {
        if (connections.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<OwnerAccounts>().FindAsync(cancellationToken);
        var closed = connections.CloseAllBut(account?.Stamp.ToString());
        if (closed > 0)
        {
            LogClosed(closed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed {Count} event connection(s) of an ended session")]
    private partial void LogClosed(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The heartbeat tick failed")]
    private partial void LogTickFailed(Exception exception);
}

internal static class EventsSetup
{
    public static void AddEvents(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<EventConnections>();
        services.AddHostedService<HeartbeatService>();
    }

    /// <summary>Refuses hub requests that a page of another origin started (browsers always name the origin).</summary>
    public static void UseSameOriginForHubs(this WebApplication app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
            && request.Headers.Origin is { Count: > 0 } origin
            && !string.Equals(origin.ToString(), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Requests from another origin are not accepted.").ExecuteAsync(context);
            return;
        }

        await next(context);
    });

    public static void MapEvents(this IEndpointRouteBuilder routes) => routes.MapHub<EventsHub>(EventsHub.Path);
}
