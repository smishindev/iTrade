using ITrade.Host;
using ITrade.Host.Auth;
using ITrade.Host.Fx;
using ITrade.Host.Jobs;
using ITrade.Host.Portfolio;
using ITrade.Host.Prices;

if (args is [SetPasswordCommand.Name, .. var rest])
{
    await using var cli = ITradeHost.Build(rest);
    return await SetPasswordCommand.RunAsync(
        cli, Console.Out, SetPasswordCommand.ReadFromConsole, confirm: !Console.IsInputRedirected);
}

if (args is [CheckCommand.Name, .. var checkArgs])
{
    return await CheckCommand.RunAsync(checkArgs, Console.Out);
}

if (args is [ImportInstrumentsCommand.Name, ..])
{
    if (args is not [_, var file, .. var importArgs] || file.StartsWith('-'))
    {
        await Console.Error.WriteLineAsync($"Usage: {ImportInstrumentsCommand.Name} <file>, e.g. config/instruments.csv");
        return 2;
    }

    await using var cli = ITradeHost.Build(importArgs);
    return await ImportInstrumentsCommand.RunAsync(cli, file, Console.Out);
}

if (args is [UpdateFxRatesCommand.Name, .. var fxArgs])
{
    var (accepted, hostArgs) = UpdateFxRatesCommand.Parse(fxArgs);
    await using var cli = ITradeHost.Build(hostArgs);
    return await UpdateFxRatesCommand.RunAsync(cli, accepted, Console.Out);
}

if (args is [ImportPricesCommand.Name, .. var priceArgs])
{
    var (file, acceptedCode, hostArgs) = ImportPricesCommand.Parse(priceArgs);
    await using var cli = ITradeHost.Build(hostArgs);
    return await ImportPricesCommand.RunAsync(cli, file, acceptedCode, Console.Out);
}

if (args is [RetryJobCommand.Name, ..])
{
    if (args is not [_, var job, var date, .. var retryArgs] || job.StartsWith('-') || date.StartsWith('-'))
    {
        await Console.Error.WriteLineAsync($"Usage: {RetryJobCommand.Name} <job> <yyyy-MM-dd>, e.g. {RetryJobCommand.Name} fx-rates 2026-10-12");
        return 2;
    }

    await using var cli = ITradeHost.Build(retryArgs);
    return await RetryJobCommand.RunAsync(cli, job, date, Console.Out);
}

try
{
    await using var app = ITradeHost.Build(args);
    await ITradeHost.StartAsync(app);
    await app.WaitForShutdownAsync();
    return 0;
}
// A refusal the platform itself raised (a missing or unsafe setting, another Host on the same database) is
// an answer for the owner, not a crash: say it plainly. Anything else keeps its stack trace.
catch (InvalidOperationException refusal) when (IsOurs(refusal))
{
    await Console.Error.WriteLineAsync($"iTrade cannot start: {refusal.Message}");
    await Console.Error.WriteLineAsync("Run `dotnet run --project src/ITrade.Host -- check` to see what is missing.");
    return 1;
}

static bool IsOurs(Exception exception) =>
    exception.TargetSite?.DeclaringType?.Assembly.GetName().Name?.StartsWith("ITrade.", StringComparison.Ordinal) == true;
