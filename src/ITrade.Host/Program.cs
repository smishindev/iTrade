using ITrade.Host;
using ITrade.Host.Auth;

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
