using System.Net;
using System.Text;
using ITrade.Broker.IBKR.Flex;

namespace ITrade.UnitTests.Broker;

/// <summary>
/// P3.2.02: the Flex Web Service, version 3, answered by a stand-in that replies as IBKR does: SendRequest with
/// a reference code, then "generation in progress" until the statement is ready. No test reaches the network,
/// and the secret is made up for each test.
/// </summary>
public sealed class FlexWebServiceTests
{
    private const string Statement = "<FlexQueryResponse queryName=\"iTrade\" type=\"AF\"><FlexStatements count=\"0\"/></FlexQueryResponse>";
    private const string Accepted =
        "<FlexStatementResponse timestamp=\"16 October, 2026 08:41 AM EDT\"><Status>Success</Status><ReferenceCode>4242</ReferenceCode>" +
        "<Url>https://gdcdyn.interactivebrokers.com/Universal/servlet/FlexStatementService.GetStatement</Url></FlexStatementResponse>";

    private static readonly string Secret = Guid.NewGuid().ToString("N");

    private static string Warn(string code, string message) =>
        $"<FlexStatementResponse><Status>Warn</Status><ErrorCode>{code}</ErrorCode><ErrorMessage>{message}</ErrorMessage></FlexStatementResponse>";

    private static string Fail(string code, string message) =>
        $"<FlexStatementResponse><Status>Fail</Status><ErrorCode>{code}</ErrorCode><ErrorMessage>{message}</ErrorMessage></FlexStatementResponse>";

    /// <summary>Answers the requests in turn and keeps what was asked.</summary>
    private sealed class Ibkr(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<HttpRequestMessage> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            var (status, body) = answers[Math.Min(_next++, answers.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/xml") });
        }
    }

    private static (FlexWebService Service, Ibkr Ibkr, List<TimeSpan> Waited) Service(params (HttpStatusCode, string)[] answers)
    {
        var ibkr = new Ibkr(answers);
        var waited = new List<TimeSpan>();
        var service = new FlexWebService(new HttpClient(ibkr), (pause, _) =>
        {
            waited.Add(pause);
            return Task.CompletedTask;
        });
        return (service, ibkr, waited);
    }

    [Fact]
    public async Task TheStatementIsAskedForAgainUntilItIsReady()
    {
        var (service, ibkr, waited) = Service(
            (HttpStatusCode.OK, Accepted),
            (HttpStatusCode.OK, Warn("1019", "Statement generation in progress. Please try again shortly.")),
            (HttpStatusCode.OK, Statement));

        var content = await service.DownloadAsync(Secret, "123456", CancellationToken.None);

        Assert.Equal(Statement, Encoding.UTF8.GetString(content));
        Assert.Equal(
            [
                $"https://ndcdyn.interactivebrokers.com/AccountManagement/FlexWebService/SendRequest?t={Secret}&q=123456&v=3",
                $"https://gdcdyn.interactivebrokers.com/Universal/servlet/FlexStatementService.GetStatement?t={Secret}&q=4242&v=3",
                $"https://gdcdyn.interactivebrokers.com/Universal/servlet/FlexStatementService.GetStatement?t={Secret}&q=4242&v=3",
            ],
            ibkr.Asked.Select(r => r.RequestUri!.AbsoluteUri));
        Assert.All(ibkr.Asked, r => Assert.Equal("iTrade/1.0", r.Headers.UserAgent.ToString()));
        Assert.Equal([FlexWebService.Pause, FlexWebService.Pause], waited);
    }

    [Theory]
    [InlineData("1012", "Token has expired.", "The token has expired: generate a new one")]
    [InlineData("1015", "Token is invalid.", "check the one saved in user-secrets (Flex:Token)")]
    [InlineData("1014", "Query is invalid.", "Check the query id saved in user-secrets (Flex:QueryId)")]
    public async Task ARefusedRequestSaysWhatToDoAndNeverShowsTheSecret(string code, string message, string hint)
    {
        var (service, ibkr, _) = Service((HttpStatusCode.OK, Fail(code, message)));

        var e = await Assert.ThrowsAsync<FlexWebServiceException>(() => service.DownloadAsync(Secret, "123456", CancellationToken.None));

        Assert.Equal(code, e.Code);
        Assert.StartsWith($"IBKR refused the request for a statement: {message} (code {code}).", e.Message, StringComparison.Ordinal);
        Assert.Contains(hint, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, e.Message, StringComparison.Ordinal);
        Assert.Single(ibkr.Asked);
    }

    [Theory]
    [InlineData("https://example.com/GetStatement")]
    [InlineData("https://interactivebrokers.com.example.com/GetStatement")]
    [InlineData("http://gdcdyn.interactivebrokers.com/GetStatement")]
    public async Task TheSecretGoesToNoOtherHostThanIbkrs(string address)
    {
        var (service, ibkr, _) = Service((HttpStatusCode.OK, Accepted.Replace(
            "https://gdcdyn.interactivebrokers.com/Universal/servlet/FlexStatementService.GetStatement", address, StringComparison.Ordinal)));

        var e = await Assert.ThrowsAsync<FlexWebServiceException>(() => service.DownloadAsync(Secret, "123456", CancellationToken.None));

        Assert.Contains("is not an IBKR host; the token was not sent there", e.Message, StringComparison.Ordinal);
        Assert.Single(ibkr.Asked);
    }

    [Fact]
    public async Task AStatementThatIsNeverReadyIsGivenUpAfterTenAttempts()
    {
        var (service, ibkr, _) = Service((HttpStatusCode.OK, Accepted), (HttpStatusCode.OK, Warn("1019", "Statement generation in progress.")));

        var e = await Assert.ThrowsAsync<FlexWebServiceException>(() => service.DownloadAsync(Secret, "123456", CancellationToken.None));

        Assert.StartsWith("The statement was not ready after 10 attempts: Statement generation in progress. (code 1019).", e.Message, StringComparison.Ordinal);
        Assert.Equal(1 + FlexWebService.MostAttempts, ibkr.Asked.Count);
    }

    [Fact]
    public async Task AnErrorWhileFetchingTheStatementIsNotAskedAgain()
    {
        var (service, ibkr, _) = Service((HttpStatusCode.OK, Accepted), (HttpStatusCode.OK, Fail("1003", "Statement is not available.")));

        var e = await Assert.ThrowsAsync<FlexWebServiceException>(() => service.DownloadAsync(Secret, "123456", CancellationToken.None));

        Assert.Equal(("1003", 2), (e.Code, ibkr.Asked.Count));
        Assert.StartsWith("IBKR gave no statement: Statement is not available. (code 1003).", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpErrorIsSaidWithoutTheAddress()
    {
        var (service, _, _) = Service((HttpStatusCode.InternalServerError, "oops"));

        var e = await Assert.ThrowsAsync<FlexWebServiceException>(() => service.DownloadAsync(Secret, "123456", CancellationToken.None));

        Assert.Equal("IBKR's Flex Web Service answered HTTP 500 Internal Server Error.", e.Message);
    }

    [Fact]
    public async Task AnAnswerThatIsNotXmlIsSaid()
    {
        var (service, _, _) = Service((HttpStatusCode.OK, "<html"));

        var e = await Assert.ThrowsAsync<FlexWebServiceException>(() => service.DownloadAsync(Secret, "123456", CancellationToken.None));

        Assert.StartsWith("IBKR's answer to SendRequest is not XML", e.Message, StringComparison.Ordinal);
    }
}
