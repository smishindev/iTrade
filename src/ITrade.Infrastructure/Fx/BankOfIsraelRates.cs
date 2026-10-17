using System.Globalization;
using System.Net;
using ITrade.Application.Fx;
using ITrade.Application.Jobs;
using ITrade.Domain;
using NodaTime;
using NodaTime.Text;

namespace ITrade.Infrastructure.Fx;

/// <summary>
/// The representative rate of the US dollar in shekels from the public statistics service of the Bank of Israel
/// (series RER_USD_ILS, SDMX, CSV format). No key is needed. Checked on 2026-10-12: one row per published day
/// back to 2005-01-03, no row for weekends and holidays, HTTP 404 with an HTML page when the range holds no
/// rate or the series does not exist.
/// </summary>
public sealed class BankOfIsraelRates(HttpClient http) : IFxRateSource
{
    public const string SourceName = FxSources.BankOfIsraelRepresentative;
    public const string SeriesCode = "RER_USD_ILS";

    public static readonly Uri Series = new(
        "https://edge.boi.gov.il/FusionEdgeServer/sdmx/v2/data/dataflow/BOI.STATISTICS/EXR/1.0/" + SeriesCode);

    /// <summary>
    /// The dollar has cost between 2.8 and 4.8 shekels since 2005. A value outside 1 … 10 is far more likely a
    /// changed unit or a broken answer than a rate, and a wrong rate must not be stored.
    /// </summary>
    private const decimal LowestPlausible = 1m;
    private const decimal HighestPlausible = 10m;

    private static readonly string[] Columns =
        ["SERIES_CODE", "FREQ", "BASE_CURRENCY", "COUNTER_CURRENCY", "UNIT_MULT", "TIME_PERIOD", "OBS_VALUE"];

    public string Name => SourceName;

    public Currency BaseCurrency => Currency.Usd;

    public Currency QuoteCurrency => Currency.Ils;

    public async Task<IReadOnlyList<FxRate>> FetchAsync(LocalDate first, LocalDate last, CancellationToken cancellationToken)
    {
        var iso = LocalDatePattern.Iso;
        var uri = new Uri($"{Series}?startperiod={iso.Format(first)}&endperiod={iso.Format(last)}&format=csv");
        string answer;
        try
        {
            using var response = await http.GetAsync(uri, cancellationToken);
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
            {
                throw new TemporaryFailureException(
                    $"The Bank of Israel's rates service is not available now (it answered {(int)response.StatusCode}).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The Bank of Israel answered {(int)response.StatusCode} for {SeriesCode} {iso.Format(first)} … {iso.Format(last)} " +
                    "(404 also means that the range holds no rate).");
            }

            answer = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        // No connection, no name resolution, a connection cut in the middle, or no answer within the time
        // allowed (which arrives as a cancellation that is not ours).
        catch (Exception e) when (e is HttpRequestException or IOException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new TemporaryFailureException("The Bank of Israel's rates service could not be reached.", e);
        }

        return Parse(answer);
    }

    /// <summary>Reads the CSV answer. Anything unexpected is refused: a wrong rate is worse than no rate.</summary>
    public static IReadOnlyList<FxRate> Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            throw new FormatException("The answer of the Bank of Israel is empty.");
        }

        var header = lines[0].Split(',');
        var at = Columns.ToDictionary(c => c, c => Array.IndexOf(header, c));
        if (at.Values.Any(i => i < 0))
        {
            throw new FormatException(
                $"The answer of the Bank of Israel is not the expected table: columns {string.Join(", ", at.Where(c => c.Value < 0).Select(c => c.Key))} are missing.");
        }

        var rates = new List<FxRate>(lines.Count - 1);
        foreach (var line in lines.Skip(1))
        {
            // The service does not quote its fields; a quote would mean a comma inside a field.
            var fields = line.Split(',');
            if (fields.Length != header.Length || line.Contains('"', StringComparison.Ordinal))
            {
                throw new FormatException($"Unexpected row in the answer of the Bank of Israel: '{line}'.");
            }

            string Field(string column) => fields[at[column]];
            if (Field("SERIES_CODE") != SeriesCode || Field("FREQ") != "D" || Field("BASE_CURRENCY") != "USD"
                || Field("COUNTER_CURRENCY") != "ILS" || Field("UNIT_MULT") != "0")
            {
                throw new FormatException($"Not a plain daily USD/ILS rate of {SeriesCode}: '{line}'.");
            }

            var date = LocalDatePattern.Iso.Parse(Field("TIME_PERIOD"));
            if (!date.Success
                || !decimal.TryParse(Field("OBS_VALUE"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || value < LowestPlausible || value > HighestPlausible)
            {
                throw new FormatException($"Not a date and a plausible USD/ILS rate: '{line}'.");
            }

            // The bank publishes three decimals. More than the table keeps would be rounded on the way in, and
            // the rounded rate would then differ from the source for ever.
            if (decimal.Round(value, FxRate.Scale) != value)
            {
                throw new FormatException($"A rate with more than {FxRate.Scale} decimal places: '{line}'.");
            }

            if (rates.Count > 0 && date.Value <= rates[^1].Date)
            {
                throw new FormatException($"The rates of the Bank of Israel are not in order of date at '{line}'.");
            }

            rates.Add(new FxRate(Currency.Usd, Currency.Ils, value, date.Value, SourceName));
        }

        return rates;
    }
}
