using IBApi;
using ITrade.Spikes.Ibkr;

// Spike B console. Commands grow task by task (roadmap P1.B). Paper ports only.
//   ibkr-spike connect   — connect, wait for nextValidId + managedAccounts, verify paper account, disconnect

var command = args.Length > 0 ? args[0] : "connect";
var config = SpikeConfig.Load(AppContext.BaseDirectory);
using var log = new MessageLog(Path.Combine(Environment.CurrentDirectory, config.LogDirectory));
Console.WriteLine($"Message log: {log.Path}");

return command switch
{
    "connect" => await Connect(config, log),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: ibkr-spike connect");
    return 2;
}

static async Task<int> Connect(SpikeConfig config, MessageLog log)
{
    var (wrapper, callbacks) = LoggingWrapper.Create(log);
    var nextValidId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var accounts = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    callbacks.On("nextValidId", a => nextValidId.TrySetResult((int)a[0]!));
    callbacks.On("managedAccounts", a => accounts.TrySetResult((string)a[0]!));
    callbacks.On("error/5", a => Console.WriteLine($"  [ibkr {a[2]}] {a[3]}"));
    callbacks.On("error/1", a => Console.WriteLine($"  [ibkr] {a[0]}"));
    callbacks.On("connectionClosed", _ => Console.WriteLine("  connection closed"));

    var signal = new EReaderMonitorSignal();
    var client = new EClientSocket(wrapper, signal);

    log.Write("out", "eConnect", [new("host", config.Host), new("port", config.Port), new("clientId", config.ClientId)]);
    client.eConnect(config.Host, config.Port, config.ClientId);
    if (!client.IsConnected())
    {
        Console.Error.WriteLine($"Could not connect to {config.Host}:{config.Port}. Is IB Gateway running and logged in (paper)?");
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
        var timeout = TimeSpan.FromSeconds(config.ConnectTimeoutSeconds);
        var orderId = await nextValidId.Task.WaitAsync(timeout);
        var accountList = await accounts.Task.WaitAsync(timeout);

        Console.WriteLine($"Connected. Server version {client.ServerVersion}; next valid order id {orderId}.");

        var accountIds = accountList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var foreign = accountIds.Where(a => !a.StartsWith(config.ExpectedAccountPrefix, StringComparison.Ordinal)).ToList();
        Console.WriteLine($"Accounts: {string.Join(", ", accountIds.Select(Mask))}");
        if (accountIds.Length == 0 || foreign.Count > 0)
        {
            Console.Error.WriteLine($"REFUSED: every account must start with '{config.ExpectedAccountPrefix}' (paper). Not a paper session.");
            return 3;
        }

        Console.WriteLine("Paper account confirmed.");
        return 0;
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("Timed out waiting for nextValidId/managedAccounts.");
        return 1;
    }
    finally
    {
        log.Write("out", "eDisconnect", []);
        client.eDisconnect();
    }
}

static string Mask(string account) =>
    account.Length <= 4 ? "****" : account[..2] + new string('*', account.Length - 4) + account[^2..];
