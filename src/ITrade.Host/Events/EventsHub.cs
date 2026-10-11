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
internal sealed class EventsHub : Hub
{
    public const string Path = "/hubs/events";
    public const string Heartbeat = "heartbeat";
}

/// <summary>"The server is alive": its time (UTC, ISO 8601) and mode.</summary>
internal sealed record HeartbeatEvent(string At, string Mode);

internal sealed class HeartbeatService(IHubContext<EventsHub> hub, IClock clock, ITradeOptions options)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var heartbeat = new HeartbeatEvent(
                InstantPattern.ExtendedIso.Format(clock.GetCurrentInstant()), options.Mode.ToString());
            await hub.Clients.All.SendAsync(EventsHub.Heartbeat, heartbeat, stoppingToken);
        }
    }
}

internal static class EventsSetup
{
    public static void AddEvents(this IServiceCollection services)
    {
        services.AddSignalR();
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
