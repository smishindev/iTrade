using System.Globalization;
using ITrade.Broker.IBKR.Flex;

namespace ITrade.Host.Portfolio;

/// <summary>
/// `ITrade.Host fetch-statement`: downloads the statement of the owner's Flex query from IBKR's Flex Web
/// Service (both settings in user-secrets: <see cref="AccessKey"/> and <see cref="QueryKey"/>) into
/// `&lt;data&gt;/flex`. It records nothing; `import-statement` does that. The only call the Host makes to IBKR, and
/// only when the owner asks.
/// </summary>
internal static class FetchStatementCommand
{
    public const string Name = "fetch-statement";

    /// <summary>The name of the setting with the Flex Web Service's secret (not the secret).</summary>
    public const string AccessKey = "Flex:Token";

    public const string QueryKey = "Flex:QueryId";

    /// <summary>Where downloaded statements go: `&lt;data&gt;/flex`, where the owner keeps the ones from the portal.</summary>
    public static string Folder(string dataDir) => Path.GetFullPath(Path.Combine(dataDir, "flex"));

    /// <param name="app">A built, not started host (settings).</param>
    /// <param name="output">Where the answer goes.</param>
    /// <param name="handler">The way to IBKR; the tests give one that answers by itself.</param>
    /// <param name="wait">How a pause is waited; the tests do not wait.</param>
    public static async Task<int> RunAsync(
        WebApplication app, TextWriter output, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        var access = app.Configuration[AccessKey];
        var query = app.Configuration[QueryKey];
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(query))
        {
            await output.WriteLineAsync(
                $"{AccessKey} and {QueryKey} are not set. Save them once, from the repository root: " +
                $"dotnet user-secrets set \"{AccessKey}\" <value> --project src/ITrade.Host (and the same for {QueryKey}).");
            return 2;
        }

        // A plain client, not one from the factory: the factory's handlers log the address, and it carries the secret.
        // No redirect is followed: the secret would go along to wherever it points.
        using var http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(60);
        http.MaxResponseContentBufferSize = FlexWebService.MostBytes;
        byte[] content;
        try
        {
            content = await new FlexWebService(http, wait).DownloadAsync(access, query, CancellationToken.None);
        }
        catch (FlexWebServiceException e)
        {
            await output.WriteLineAsync($"No statement was downloaded: {e.Message}");
            return 1;
        }

        FlexStatement statement;
        try
        {
            statement = FlexStatementFile.Read(content);
        }
        catch (FormatException e)
        {
            await output.WriteLineAsync($"IBKR sent something that is not a statement: {e.Message}");
            return 1;
        }

        var options = app.Services.GetRequiredService<ITradeOptions>();
        var folder = Folder(options.DataDir);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"flex-{statement.Generated:uuuuMMdd-HHmmss}.xml"));
        await File.WriteAllBytesAsync(path, content);
        var from = statement.Periods.Min(p => p.From);
        var to = statement.Periods.Max(p => p.To);
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Downloaded the statement of {from:R} to {to:R} ({statement.Periods.Count} day(s)) to {path}."));
        await output.WriteLineAsync($"To record it: dotnet run --project src/ITrade.Host -- {ImportStatementCommand.Name} \"{path}\"");
        return 0;
    }
}
