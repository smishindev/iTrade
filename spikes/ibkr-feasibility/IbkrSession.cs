using IBApi;

namespace ITrade.Spikes.Ibkr;

/// <summary>
/// One logical session with IB Gateway (paper only). Owns the socket, the reader thread and the
/// paper-account check, and can reconnect after a dropped connection. The same <see cref="EWrapper"/>
/// (and its handlers) is reused across reconnects; a new <see cref="EClientSocket"/> is created each time.
/// Only classic callbacks are handled; the ProtoBuf duplicates of API 10.50 are just logged.
/// </summary>
public sealed class IbkrSession : IDisposable
{
    /// <summary>Connectivity codes worth explaining (docs: "Message Codes").</summary>
    private static readonly Dictionary<int, string> ConnectivityCodes = new()
    {
        [1100] = "LOST: connectivity between IB Gateway and IBKR servers lost",
        [1101] = "RESTORED, DATA LOST: re-subscribe market data/account updates and reconcile orders",
        [1102] = "RESTORED, DATA KEPT: reconcile orders anyway",
        [1300] = "SOCKET PORT RESET: reconnect on the new port",
        [2103] = "market data farm disconnected",
        [2104] = "info: market data farm OK",
        [2105] = "HMDS (historical data) farm disconnected",
        [2106] = "info: HMDS farm OK",
        [2107] = "info: HMDS farm inactive but available on demand",
        [2108] = "info: market data farm inactive but available on demand",
        [2110] = "connectivity between IB Gateway and server is broken; will be restored automatically",
        [2119] = "info: market data farm connecting",
        [2157] = "sec-def data farm disconnected",
        [2158] = "info: sec-def data farm OK",
    };

    private readonly SpikeConfig _config;
    private readonly MessageLog _log;
    private int _nextRequestId = 1000;
    private volatile bool _closing;

    public IbkrSession(SpikeConfig config, MessageLog log)
    {
        _config = config;
        _log = log;
        (Wrapper, Callbacks) = LoggingWrapper.Create(log);
        Callbacks.On("error/5", a => OnError((int)a[0]!, (int)a[2]!, (string)a[3]!));
        Callbacks.On("error/1", a => Console.WriteLine(a[0] is Exception ex
            ? $"  [ibkr exception] {ex.GetType().Name}: {ex.Message}"
            : $"  [ibkr] {a[0]}"));
        Callbacks.On("connectionClosed", _ => OnConnectionClosed());
    }

    public EWrapper Wrapper { get; }

    public LoggingWrapper Callbacks { get; }

    public EClientSocket Client { get; private set; } = null!;

    public string[] Accounts { get; private set; } = [];

    public int NextOrderId { get; private set; }

    public int ConnectCount { get; private set; }

    /// <summary>Raised (on the reader thread) when the socket closes without <see cref="Dispose"/>.</summary>
    public event Action? Dropped;

    /// <summary>Raised for every error/5 callback: (id, code, message).</summary>
    public event Action<int, int, string>? Error;

    public int NewRequestId() => Interlocked.Increment(ref _nextRequestId);

    /// <summary>
    /// Connects, waits for nextValidId and managedAccounts and checks that every account is paper.
    /// Returns 0 on success, 1 on connection failure/timeout, 3 when the account is not paper.
    /// </summary>
    public async Task<int> ConnectAsync()
    {
        var nextValidId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s1 = Callbacks.On("nextValidId", a => nextValidId.TrySetResult((int)a[0]!));
        using var s2 = Callbacks.On("managedAccounts", a => accounts.TrySetResult((string)a[0]!));
        // Gateway closes the socket on a refused handshake (e.g. 326 client id in use): fail fast.
        using var s3 = Callbacks.On("connectionClosed", _ =>
        {
            var closed = new IOException("connection closed during handshake");
            nextValidId.TrySetException(closed);
            accounts.TrySetException(closed);
        });

        var signal = new EReaderMonitorSignal();
        var client = new EClientSocket(Wrapper, signal);
        Client = client;

        _log.Write("out", "eConnect", [new("host", _config.Host), new("port", _config.Port), new("clientId", _config.ClientId)]);
        client.eConnect(_config.Host, _config.Port, _config.ClientId);
        if (!client.IsConnected())
        {
            Console.Error.WriteLine($"Could not connect to {_config.Host}:{_config.Port}. Is IB Gateway running and logged in (paper)?");
            return 1;
        }

        var reader = new EReader(client, signal);
        reader.Start();
        var readerThread = new Thread(() =>
        {
            while (client.IsConnected())
            {
                signal.waitForSignal();
                reader.processMsgs();
            }
        })
        { IsBackground = true, Name = "ibkr-reader" };
        readerThread.Start();

        try
        {
            var timeout = TimeSpan.FromSeconds(_config.ConnectTimeoutSeconds);
            NextOrderId = await nextValidId.Task.WaitAsync(timeout);
            var accountList = await accounts.Task.WaitAsync(timeout);
            Accounts = accountList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Timed out waiting for nextValidId/managedAccounts.");
            Disconnect();
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Handshake failed: {ex.Message}.");
            return 1;
        }

        ConnectCount++;
        Console.WriteLine($"Connected (#{ConnectCount}). Server version {client.ServerVersion}; next valid order id {NextOrderId}.");
        Console.WriteLine($"Accounts: {string.Join(", ", Accounts.Select(Mask))}");

        var foreign = Accounts.Where(a => !a.StartsWith(_config.ExpectedAccountPrefix, StringComparison.Ordinal)).ToList();
        if (Accounts.Length == 0 || foreign.Count > 0)
        {
            Console.Error.WriteLine($"REFUSED: every account must start with '{_config.ExpectedAccountPrefix}' (paper). Not a paper session.");
            _log.Write("internal", "paperCheck", [new("ok", false)]);
            _closing = true;
            Disconnect();
            return 3;
        }

        _log.Write("internal", "paperCheck", [new("ok", true), new("connectCount", ConnectCount)]);
        Console.WriteLine("Paper account confirmed.");
        return 0;
    }

    /// <summary>
    /// Reconnects with exponential backoff (1, 2, 4 … capped) until success, a non-paper account,
    /// or the deadline. Returns the last <see cref="ConnectAsync"/> result.
    /// </summary>
    public async Task<int> ReconnectAsync(DateTime deadlineUtc, CancellationToken cancel)
    {
        var delay = TimeSpan.FromSeconds(1);
        var attempt = 0;
        while (true)
        {
            attempt++;
            Console.WriteLine($"  reconnect attempt {attempt} in {delay.TotalSeconds:0}s");
            _log.Write("internal", "reconnectAttempt", [new("attempt", attempt), new("delaySeconds", delay.TotalSeconds)]);
            await Task.Delay(delay, cancel);
            var result = await ConnectAsync();
            if (result is 0 or 3)
            {
                return result;
            }

            if (DateTime.UtcNow + delay > deadlineUtc)
            {
                return result;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, _config.MaxReconnectDelaySeconds));
        }
    }

    /// <summary>Client-side disconnect. Raises connectionClosed; treated as a drop unless the session is closing.</summary>
    public void Disconnect()
    {
        _log.Write("out", "eDisconnect", []);
        Client.eDisconnect();
    }

    public void Dispose()
    {
        _closing = true;
        if (Client is not null && Client.IsConnected())
        {
            Disconnect();
        }
    }

    public static string Mask(string account) =>
        account.Length <= 4 ? "****" : account[..2] + new string('*', account.Length - 4) + account[^2..];

    private void OnError(int id, int code, string message)
    {
        var note = ConnectivityCodes.TryGetValue(code, out var meaning) ? $" -> {meaning}" : "";
        Console.WriteLine($"  [ibkr id={id} code={code}] {message}{note}");
        Error?.Invoke(id, code, message);
    }

    private void OnConnectionClosed()
    {
        Console.WriteLine("  connection closed");
        if (!_closing)
        {
            Dropped?.Invoke();
        }
    }
}
