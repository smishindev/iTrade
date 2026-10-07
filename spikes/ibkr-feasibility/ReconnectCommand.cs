namespace ITrade.Spikes.Ibkr;

/// <summary>
/// P1.B.03: keep a session open, detect a dropped socket, reconnect with backoff and re-check the
/// paper account each time. Drops can be forced from the client side (eDisconnect) to test the path;
/// a Gateway restart (daily auto-restart) is caught the same way if the command is left running across it.
/// </summary>
public static class ReconnectCommand
{
    public static async Task<int> Run(SpikeConfig config, MessageLog log, string[] args)
    {
        var minutes = Requests.Option(args, "--minutes", 3.0);
        var dropEverySeconds = Requests.Option(args, "--drop-every", 30.0);
        var drops = (int)Requests.Option(args, "--drops", 2.0);
        var heartbeatSeconds = Requests.Option(args, "--heartbeat", 30.0);

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        using var session = new IbkrSession(config, log);
        session.Dropped += () => log.Write("internal", "dropDetected", [new("connectCount", session.ConnectCount)]);
        session.Callbacks.On("currentTime", a =>
            Console.WriteLine($"  heartbeat: server time {DateTimeOffset.FromUnixTimeSeconds((long)a[0]!):u}"));

        var result = await session.ConnectAsync();
        if (result != 0)
        {
            return result;
        }

        var start = DateTime.UtcNow;
        var deadline = start.AddMinutes(minutes);
        var nextDrop = drops > 0 ? start.AddSeconds(dropEverySeconds) : DateTime.MaxValue;
        var nextHeartbeat = start;
        var dropsForced = 0;
        var dropsSeen = 0;
        var downtime = TimeSpan.Zero;
        Console.WriteLine($"Holding the session for {minutes} min; forced drops: {drops} every {dropEverySeconds}s. Ctrl+C stops.");

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (!session.Client.IsConnected())
                {
                    dropsSeen++;
                    var lostAt = DateTime.UtcNow;
                    Console.WriteLine($"DROP #{dropsSeen} detected at {lostAt:HH:mm:ss}Z — reconnecting with backoff");
                    result = await session.ReconnectAsync(deadline, cancel.Token);
                    if (result == 3)
                    {
                        return 3;
                    }

                    if (result != 0)
                    {
                        Console.Error.WriteLine("Gave up reconnecting before the deadline.");
                        return 1;
                    }

                    var gap = DateTime.UtcNow - lostAt;
                    downtime += gap;
                    log.Write("internal", "reconnected", [new("drop", dropsSeen), new("downtimeSeconds", gap.TotalSeconds)]);
                    Console.WriteLine($"RECONNECTED after {gap.TotalSeconds:0.0}s; paper account re-checked. Re-subscriptions/reconciliation would run here.");
                    nextHeartbeat = DateTime.UtcNow;
                    continue;
                }

                var now = DateTime.UtcNow;
                if (dropsForced < drops && now >= nextDrop)
                {
                    dropsForced++;
                    Console.WriteLine($"FORCING drop {dropsForced}/{drops} (client eDisconnect)");
                    log.Write("internal", "forcedDrop", [new("n", dropsForced)]);
                    session.Disconnect();
                    nextDrop = now.AddSeconds(dropEverySeconds);
                    continue;
                }

                if (now >= nextHeartbeat)
                {
                    log.Write("out", "reqCurrentTime", []);
                    session.Client.reqCurrentTime();
                    nextHeartbeat = now.AddSeconds(heartbeatSeconds);
                }

                await Task.Delay(250, cancel.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopped by user.");
        }

        Console.WriteLine($"Summary: connects {session.ConnectCount}, drops seen {dropsSeen} (forced {dropsForced}), total downtime {downtime.TotalSeconds:0.0}s.");
        return 0;
    }
}
