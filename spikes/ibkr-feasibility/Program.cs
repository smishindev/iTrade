using ITrade.Spikes.Ibkr;

// Spike B console. Commands grow task by task (roadmap P1.B). Paper ports only; read-only commands.
//   connect    — connect, wait for nextValidId + managedAccounts, verify paper account, disconnect (P1.B.03)
//   reconnect  — hold a session, force/detect drops, reconnect with backoff, re-check paper (P1.B.03)
//   account    — account summary, cash by currency, positions (P1.B.04)
//   contracts  — contract details of the universe → out/contracts.csv (P1.B.05)
//   quotes     — market data types 1–4 for SPY/VWO (P1.B.06)
//   history    — daily bars 5 Y for SPY/VWO → out/history-*.csv (P1.B.07)

var command = args.Length > 0 ? args[0] : "connect";
var options = args.Skip(1).ToArray();
var config = SpikeConfig.Load(AppContext.BaseDirectory);
using var log = new MessageLog(Path.Combine(Environment.CurrentDirectory, config.LogDirectory));
Console.WriteLine($"Message log: {log.Path}");

return command switch
{
    "connect" => await Connect(config, log),
    "reconnect" => await ReconnectCommand.Run(config, log, options),
    "account" => await AccountCommand.Run(config, log),
    "contracts" => await ContractsCommand.Run(config, log, options),
    "quotes" => await QuotesCommand.Run(config, log, options),
    "history" => await HistoryCommand.Run(config, log, options),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        Usage: ibkr-spike <command> [options]
          connect
          reconnect [--minutes 3] [--drops 2] [--drop-every 30] [--heartbeat 30]
          account
          contracts [--universe path]
          quotes    [--tickers SPY,VWO] [--types 1,2,3,4] [--seconds 10]
          history   [--tickers SPY,VWO] [--duration "5 Y"] [--what TRADES] [--end "yyyyMMdd HH:mm:ss US/Eastern"] [--repeat N]
        """);
    return 2;
}

static async Task<int> Connect(SpikeConfig config, MessageLog log)
{
    using var session = new IbkrSession(config, log);
    return await session.ConnectAsync();
}
