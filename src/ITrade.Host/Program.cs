using ITrade.Host;

var app = ITradeHost.Build(args);
await using (app)
{
    await ITradeHost.StartAsync(app);
    await app.WaitForShutdownAsync();
}
