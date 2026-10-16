using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ITrade.Broker.IBKR.Flex;

/// <summary>
/// Why the Flex Web Service gave no statement. <see cref="Code"/> is IBKR's error code when it named one
/// (1012 token expired, 1015 token invalid, 1014 query invalid, …). The message never carries the token.
/// </summary>
public sealed class FlexWebServiceException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// The IBKR Flex Web Service, version 3: `SendRequest` with the token and the query id answers a reference
/// code and the address to fetch it from; `GetStatement` answers the statement, or "generation in progress"
/// until it is ready. The token is a secret: it goes into the address of the two requests and nowhere else —
/// not into a log, not into a message, and not to any host but IBKR's own.
/// </summary>
/// <param name="http">Without a logging handler: the addresses carry the token.</param>
/// <param name="wait">How a pause between two requests is waited (the tests do not wait).</param>
public sealed class FlexWebService(HttpClient http, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    public static readonly Uri SendRequestUri =
        new("https://ndcdyn.interactivebrokers.com/AccountManagement/FlexWebService/SendRequest");

    /// <summary>The largest answer read: a statement of thirty days is under a megabyte; this leaves room for years.</summary>
    public const int MostBytes = 64 * 1024 * 1024;

    /// <summary>How often a statement that is still being generated is asked for again.</summary>
    public const int MostAttempts = 10;

    /// <summary>
    /// The pause before asking again. IBKR allows one request a second and ten a minute per token; a statement
    /// of thirty days is usually ready within seconds.
    /// </summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

    // 1009 the server is busy, 1018 too many requests, 1019 the statement is being generated: ask again later.
    private static readonly HashSet<string> TryAgain = new(StringComparer.Ordinal) { "1009", "1018", "1019" };

    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? Task.Delay;

    /// <summary>Requests the statement of the query and waits for it. Returns the statement as IBKR sent it.</summary>
    public async Task<byte[]> DownloadAsync(string token, string queryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        var answer = Response(await GetAsync(With(SendRequestUri, token, queryId.Trim()), cancellationToken), "SendRequest")
            ?? throw new FlexWebServiceException("IBKR answered the request for a statement with something else than its usual answer.");
        if (Text(answer, "Status") != "Success")
        {
            throw Failure(answer, "IBKR refused the request for a statement");
        }

        var reference = Text(answer, "ReferenceCode");
        var address = Text(answer, "Url");
        if (string.IsNullOrEmpty(reference) || !Uri.TryCreate(address, UriKind.Absolute, out var statementUri))
        {
            throw new FlexWebServiceException("IBKR accepted the request but named no reference code or address to fetch the statement from.");
        }

        if (statementUri.Scheme != Uri.UriSchemeHttps
            || !(statementUri.Host == "interactivebrokers.com" || statementUri.Host.EndsWith(".interactivebrokers.com", StringComparison.OrdinalIgnoreCase)))
        {
            // The token would go wherever this says; only IBKR's own hosts get it.
            throw new FlexWebServiceException($"IBKR's answer points to {statementUri.Host}, which is not an IBKR host; the token was not sent there.");
        }

        for (var attempt = 1; ; attempt++)
        {
            await _wait(Pause, cancellationToken);
            var content = await GetAsync(With(statementUri, token, reference), cancellationToken);
            if (Response(content, "GetStatement") is not { } status)
            {
                return content; // the statement itself
            }

            var code = Text(status, "ErrorCode");
            if (!TryAgain.Contains(code ?? "") || attempt == MostAttempts)
            {
                throw Failure(status, attempt == MostAttempts && TryAgain.Contains(code ?? "")
                    ? string.Create(CultureInfo.InvariantCulture, $"The statement was not ready after {MostAttempts} attempts")
                    : "IBKR gave no statement");
            }
        }
    }

    private async Task<byte[]> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("iTrade/1.0"); // the service refuses requests without one
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new FlexWebServiceException(string.Create(
                    CultureInfo.InvariantCulture, $"IBKR's Flex Web Service answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}."));
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException e)
        {
            // The message of the exception names the host at most; the address with the token stays out.
            throw new FlexWebServiceException($"IBKR's Flex Web Service could not be reached: {e.HttpRequestError}.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FlexWebServiceException("IBKR's Flex Web Service did not answer in time.");
        }
    }

    private static Uri With(Uri address, string token, string query) =>
        new UriBuilder(address)
        {
            Query = $"t={Uri.EscapeDataString(token)}&q={Uri.EscapeDataString(query)}&v=3",
        }.Uri;

    /// <summary>The service's own answer (`FlexStatementResponse`), or null when the content is something else.</summary>
    private static XElement? Response(byte[] content, string step)
    {
        try
        {
            using var reader = XmlReader.Create(
                new MemoryStream(content, writable: false),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            reader.MoveToContent();
            return reader.LocalName == "FlexStatementResponse" ? XElement.Load(reader) : null;
        }
        catch (XmlException e)
        {
            throw new FlexWebServiceException($"IBKR's answer to {step} is not XML: {e.Message}");
        }
    }

    private static string? Text(XElement answer, string name) => answer.Element(name)?.Value.Trim();

    private static FlexWebServiceException Failure(XElement answer, string what)
    {
        var code = Text(answer, "ErrorCode");
        var message = Text(answer, "ErrorMessage");
        var hint = code switch
        {
            "1012" => " The token has expired: generate a new one in the portal (Flex Web Service Configuration) and save it again.",
            "1015" => " The token is not valid: check the one saved in user-secrets (Flex:Token).",
            "1014" or "1020" => " Check the query id saved in user-secrets (Flex:QueryId).",
            _ => "",
        };
        return new FlexWebServiceException($"{what}: {message ?? "no reason given"} (code {code ?? "none"}).{hint}", code);
    }
}
