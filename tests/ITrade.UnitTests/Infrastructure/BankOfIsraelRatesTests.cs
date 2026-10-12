using System.Net;
using System.Security.Cryptography;
using ITrade.Application.Jobs;
using ITrade.Domain;
using ITrade.Infrastructure.Fx;
using NodaTime;

namespace ITrade.UnitTests.Infrastructure;

/// <summary>
/// P3.3.01: reading the Bank of Israel's answer — on the bytes the real service sent, without the network —
/// and refusing anything that is not a plain table of USD/ILS rates.
/// </summary>
public sealed class BankOfIsraelRatesTests
{
    /// <summary>
    /// The answer of the service to startperiod=2026-09-20, endperiod=2026-10-12, format=csv, saved as it came
    /// on 2026-10-12, before that day's rate was published. No rate on the 21st (Yom Kippur) and on weekends.
    /// </summary>
    private static readonly string RecordedFile =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "boi-rer-usd-ils-2026-09-20_2026-10-12.csv");

    private const string Header =
        "SERIES_CODE,FREQ,BASE_CURRENCY,COUNTER_CURRENCY,UNIT_MEASURE,DATA_TYPE,DATA_SOURCE,TIME_COLLECT,CONF_STATUS,PUB_WEBSITE,UNIT_MULT,COMMENTS,TIME_PERIOD,OBS_VALUE,RELEASE_STATUS";

    private static string Row(string date = "2026-10-09", string value = "3.055", string series = "RER_USD_ILS",
        string baseCurrency = "USD", string counter = "ILS", string unitMult = "0", string comments = "", string freq = "D") =>
        $"{series},{freq},{baseCurrency},{counter},ILS,OF00,BOI_MRKT,V,F,Y,{unitMult},{comments},{date},{value},YP";

    private static string Table(params string[] rows) => Header + "\n" + string.Join('\n', rows) + "\n";

    [Fact]
    public async Task RecordedAnswerIsReadExactly()
    {
        var bytes = await File.ReadAllBytesAsync(RecordedFile);
        // The file is the service's own bytes: if a tool rewrote its line ends, this is no longer a recording.
        Assert.Equal("0E54B5BAA3B97BC644C328043FF8301262EA760D367855081249584E6A5EF690", Convert.ToHexString(SHA256.HashData(bytes)));
        using var handler = new FakeHandler(HttpStatusCode.OK, bytes);
        using var http = new HttpClient(handler);

        var rates = await new BankOfIsraelRates(http).FetchAsync(new LocalDate(2026, 9, 20), new LocalDate(2026, 10, 12), CancellationToken.None);

        Assert.Equal(14, rates.Count);
        Assert.All(rates, r => Assert.Equal((Currency.Usd, Currency.Ils, "BOI representative"), (r.From, r.To, r.Source)));
        Assert.Equal((new LocalDate(2026, 9, 22), 3.017m), (rates[0].Date, rates[0].Rate));
        Assert.Equal((new LocalDate(2026, 10, 1), 3.08m), (rates[7].Date, rates[7].Rate)); // as published, no digits invented
        Assert.Equal((new LocalDate(2026, 10, 9), 3.055m), (rates[^1].Date, rates[^1].Rate));
        Assert.DoesNotContain(rates, r => r.Date.DayOfWeek is IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday);
        Assert.DoesNotContain(rates, r => r.Date == new LocalDate(2026, 9, 21));
        Assert.Equal(
            "https://edge.boi.gov.il/FusionEdgeServer/sdmx/v2/data/dataflow/BOI.STATISTICS/EXR/1.0/RER_USD_ILS" +
            "?startperiod=2026-09-20&endperiod=2026-10-12&format=csv",
            Assert.Single(handler.Asked).AbsoluteUri);
    }

    [Fact]
    public void LineEndsAndTheOrderOfColumnsDoNotMatter()
    {
        var plain = BankOfIsraelRates.Parse(Table(Row("2026-10-08", "3.084"), Row()));

        Assert.Equal(plain, BankOfIsraelRates.Parse(Table(Row("2026-10-08", "3.084"), Row()).Replace("\n", "\r\n", StringComparison.Ordinal).TrimEnd()));
        var reordered = BankOfIsraelRates.Parse(
            "OBS_VALUE,TIME_PERIOD,UNIT_MULT,COUNTER_CURRENCY,BASE_CURRENCY,FREQ,SERIES_CODE,NEW_COLUMN\n3.055,2026-10-09,0,ILS,USD,D,RER_USD_ILS,x\n");
        Assert.Equal((new LocalDate(2026, 10, 9), 3.055m), (Assert.Single(reordered).Date, reordered[0].Rate));
        // The limits of what is taken for a rate are themselves rates.
        Assert.Equal([1m, 10m], BankOfIsraelRates.Parse(Table(Row("2026-10-08", "1"), Row("2026-10-09", "10.000000"))).Select(r => r.Rate));
    }

    [Theory]
    [InlineData("")] // nothing at all
    [InlineData("<!DOCTYPE html><html dir=\"rtl\"><head> <title>error</title>")] // the service's error page with status 200
    [InlineData("SERIES_CODE,FREQ,TIME_PERIOD\nRER_USD_ILS,D,2026-10-09\n")] // no value column
    public void AnswerThatIsNotTheTableIsRefused(string answer) =>
        Assert.Throws<FormatException>(() => BankOfIsraelRates.Parse(answer));

    [Fact]
    public void RowThatIsNotAPlainDailyDollarRateIsRefused()
    {
        string[] rows =
        [
            Row(series: "RER_EUR_ILS"),
            Row(baseCurrency: "EUR"),
            Row(counter: "USD"),
            Row(freq: "M"), // a monthly average is not the rate of a day
            Row(unitMult: "2"), // the value would be in hundreds
            Row(date: "09/10/2026"),
            Row(date: "2026-13-01"),
            Row(value: ""),
            Row(value: " 3.055"),
            Row(value: "3,055"), // a decimal comma shifts every column
            Row(value: "-3.055"),
            Row(value: "0"),
            Row(value: "0.999999"),
            Row(value: "10.000001"),
            Row(value: "305.5"), // agorot instead of shekels
            Row(value: "1e3"),
            Row(value: "3.0550001"), // seven decimals: the table would round it without a word
            Row(value: "3.0550000000000001"), // what a floating-point number looks like in text
            Row(comments: "\"revised, see note\""),
            Row() + ",extra",
        ];

        Assert.All(rows, row => Assert.Throws<FormatException>(() => BankOfIsraelRates.Parse(Table(row))));
        Assert.Single(BankOfIsraelRates.Parse(Table(Row()))); // the row itself is fine
        Assert.Equal(3.055001m, Assert.Single(BankOfIsraelRates.Parse(Table(Row(value: "3.055001")))).Rate); // six are kept
    }

    [Fact]
    public void DatesOutOfOrderOrTwiceAreRefused()
    {
        Assert.Throws<FormatException>(() => BankOfIsraelRates.Parse(Table(Row("2026-10-09"), Row("2026-10-08"))));
        Assert.Throws<FormatException>(() => BankOfIsraelRates.Parse(Table(Row("2026-10-09"), Row("2026-10-09", "3.056"))));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)] // the service's answer to a range without rates, and to a wrong series
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnswerThatRefusesTheQuestionIsAFaultThatNamesTheStatus(HttpStatusCode status)
    {
        using var handler = new FakeHandler(status, "<!DOCTYPE html><html dir=\"rtl\">"u8.ToArray());
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BankOfIsraelRates(http).FetchAsync(new LocalDate(2026, 10, 10), new LocalDate(2026, 10, 11), CancellationToken.None));

        Assert.Contains($"answered {(int)status}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("503")]
    [InlineData("429")]
    [InlineData("no connection")]
    [InlineData("cut off")]
    [InlineData("no answer in time")]
    public async Task ServiceThatIsAwayIsATemporaryFailureNotAFault(string away)
    {
        // Such a failure costs the daily job none of its attempts: nobody has to come and release it.
        using var handler = away switch
        {
            "503" => new FakeHandler(HttpStatusCode.ServiceUnavailable, []),
            "429" => new FakeHandler(HttpStatusCode.TooManyRequests, []),
            "no connection" => new FakeHandler(new HttpRequestException("No such host is known.")),
            "cut off" => new FakeHandler(new IOException("The response ended prematurely.")),
            _ => new FakeHandler(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")),
        };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<TemporaryFailureException>(() =>
            new BankOfIsraelRates(http).FetchAsync(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 12), CancellationToken.None));

        Assert.Contains("Bank of Israel", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingTheApplicationIsNotATemporaryFailure()
    {
        using var handler = new FakeHandler(new TaskCanceledException());
        using var http = new HttpClient(handler);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BankOfIsraelRates(http).FetchAsync(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 12), stopping.Token));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body = [];
        private readonly Exception? _failure;

        public FakeHandler(HttpStatusCode status, byte[] body) => (_status, _body) = (status, body);

        public FakeHandler(Exception failure) => _failure = failure;

        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Asked.Add(request.RequestUri!);
            return _failure is null
                ? Task.FromResult(new HttpResponseMessage(_status) { Content = new ByteArrayContent(_body) })
                : Task.FromException<HttpResponseMessage>(_failure);
        }
    }
}
