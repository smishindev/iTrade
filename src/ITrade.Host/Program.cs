using ITrade.Host;
using ITrade.Host.Auth;

if (args is [SetPasswordCommand.Name, .. var rest])
{
    await using var cli = ITradeHost.Build(rest);
    return await SetPasswordCommand.RunAsync(
        cli, Console.Out, SetPasswordCommand.ReadFromConsole, confirm: !Console.IsInputRedirected);
}

await using var app = ITradeHost.Build(args);
await ITradeHost.StartAsync(app);
await app.WaitForShutdownAsync();
return 0;
