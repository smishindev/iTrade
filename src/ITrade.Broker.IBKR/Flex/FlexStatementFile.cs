using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using NodaTime;
using NodaTime.Text;

namespace ITrade.Broker.IBKR.Flex;

/// <summary>
/// One execution or one currency conversion of the `Trades` section (one row of `levelOfDetail="EXECUTION"`).
/// An order filled on several exchanges is several of these, each with its own transaction id.
/// </summary>
/// <param name="Category">`assetCategory`: STK for a share or a fund, CASH for a currency conversion.</param>
/// <param name="Symbol">For a conversion the pair, base currency first: USD.ILS.</param>
/// <param name="ExecutedAt">`dateTime`, New York time.</param>
/// <param name="Quantity">Shares, or units of the base currency of a conversion; negative for a sale.</param>
/// <param name="Proceeds">Cash of the trade in <paramref name="Currency"/>; negative for a buy.</param>
/// <param name="Commission">`ibCommission`, negative when charged, in <paramref name="CommissionCurrency"/>.</param>
/// <param name="NetCash">Proceeds and commission together; IBKR states 0 for a conversion.</param>
public sealed record FlexTrade(
    string TransactionId,
    string TransactionType,
    string LevelOfDetail,
    string Category,
    string Symbol,
    int? ConId,
    string? Isin,
    string Currency,
    LocalDate TradeDate,
    LocalDate SettleDate,
    LocalDateTime ExecutedAt,
    string BuySell,
    decimal Quantity,
    decimal TradePrice,
    decimal Proceeds,
    decimal Commission,
    string CommissionCurrency,
    decimal NetCash,
    string Notes);

/// <summary>One row of the `CashTransactions` section: a deposit, a dividend, withheld tax, interest, a fee.</summary>
/// <param name="TransactionId">Empty on the summary rows IBKR adds (`levelOfDetail="SUMMARY"`).</param>
/// <param name="Date">The date part of `dateTime`.</param>
/// <param name="ReportDate">The day IBKR booked the row; it is in the statement of that day.</param>
/// <param name="ActionId">IBKR's id of the corporate event (a dividend and the tax withheld from it share it).</param>
public sealed record FlexCash(
    string? TransactionId,
    string LevelOfDetail,
    string Type,
    string Currency,
    int? ConId,
    string Symbol,
    decimal Amount,
    LocalDate Date,
    LocalDate? SettleDate,
    LocalDate? ReportDate,
    string ActionId,
    string Description);

/// <summary>One row of the `CashReport` section: the cash of one currency (or of all, in the base currency).</summary>
/// <param name="Currency">A currency code, or `BASE_SUMMARY` for the whole account told in the base currency.</param>
public sealed record FlexCashReport(string Currency, decimal StartingCash, decimal EndingCash);

/// <summary>What IBKR says is held of one contract at the end of a period (`OpenPositions`, summary level).</summary>
public sealed record FlexPosition(string Category, int ConId, string Symbol, decimal Quantity);

/// <summary>
/// One `FlexStatement`: the days <paramref name="From"/> to <paramref name="To"/> (one day each when the query
/// breaks out by day) and the rows of it that the import reads. <paramref name="Unread"/> names every section
/// with rows that move money or shares and that the import does not read; <paramref name="Missing"/> every
/// section the import needs to see, even empty, that is not there. A statement with either cannot be recorded.
/// </summary>
public sealed record FlexPeriod(
    string Account,
    string BaseCurrency,
    LocalDate From,
    LocalDate To,
    IReadOnlyList<FlexTrade> Trades,
    IReadOnlyList<FlexCash> Cash,
    IReadOnlyList<FlexCashReport> CashReport,
    IReadOnlyList<FlexPosition> OpenPositions,
    IReadOnlyList<string> Unread,
    IReadOnlyList<string> Missing)
{
    /// <summary>The cash of each currency at the start and the end of the period, when IBKR reports it per currency.</summary>
    public IReadOnlyList<FlexCashReport> PerCurrency => CashReport.Where(r => r.Currency != "BASE_SUMMARY").ToList();

    /// <summary>The whole cash told in the base currency.</summary>
    public FlexCashReport? BaseSummary => CashReport.FirstOrDefault(r => r.Currency == "BASE_SUMMARY");
}

/// <summary>A downloaded Activity Flex statement: its periods in the order of the file.</summary>
/// <param name="Generated">`whenGenerated`, New York time as IBKR writes it.</param>
public sealed record FlexStatement(string QueryName, LocalDateTime Generated, IReadOnlyList<FlexPeriod> Periods)
{
    /// <summary>Every account the statement speaks of.</summary>
    public IReadOnlyList<string> Accounts => Periods.Select(p => p.Account).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Reads an IBKR Activity Flex statement in XML (Flex query `iTrade`, docs/roadmap/P3-accounting.md P3.2.01):
/// dates `yyyyMMdd`, date and time `yyyyMMdd;HHmmss` in New York time. Only what the import needs is taken;
/// anything that is not as IBKR writes it stops the whole statement, naming the day and the row.
/// </summary>
public static class FlexStatementFile
{
    /// <summary>Sections whose rows move money or shares and that the import does not read (yet).</summary>
    public static readonly IReadOnlyList<string> SectionsNotRead =
    [
        "CorporateActions", "Transfers", "TradeTransfers", "TransactionTaxes", "SalesTaxes", "OptionEAE",
        "SLBActivities", "SLBFees", "CFDCharges", "DebitCardActivities", "IBGNoteTransactions",
        "HKIPOSubscriptionActivity", "StockGrantActivities",
    ];

    /// <summary>
    /// Sections every period must have, even without rows: what the import reads, what it checks against, and
    /// what it must be able to see is empty. A query that lost one would let shares or money pass unseen.
    /// </summary>
    public static readonly IReadOnlyList<string> SectionsRequired =
        ["Trades", "CashTransactions", "CashReport", "OpenPositions", .. SectionsNotRead];

    private static readonly LocalDatePattern Date = LocalDatePattern.CreateWithInvariantCulture("uuuuMMdd");
    private static readonly LocalDateTimePattern DateTime = LocalDateTimePattern.CreateWithInvariantCulture("uuuuMMdd';'HHmmss");

    public static FlexStatement Read(string path) => Read(File.ReadAllBytes(path));

    public static FlexStatement Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        XDocument document;
        try
        {
            // A downloaded file is untrusted: no DTD, nothing resolved from outside.
            using var reader = XmlReader.Create(
                new MemoryStream(content, writable: false),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new FormatException($"The statement is not XML: {e.Message}", e);
        }

        var root = document.Root;
        if (root?.Name.LocalName != "FlexQueryResponse")
        {
            // The Web Service answers an error with a FlexStatementResponse instead of a statement.
            throw new FormatException(
                $"This is not a Flex statement (the file starts with <{root?.Name.LocalName}>, not <FlexQueryResponse>).");
        }

        if (root.Attribute("type")?.Value is not "AF")
        {
            throw new FormatException(
                $"This is a Flex statement of type '{root.Attribute("type")?.Value}'; the import reads Activity statements (AF).");
        }

        var statements = root.Elements("FlexStatements").ToList();
        var elements = statements.Elements("FlexStatement").ToList();
        if (elements.Count == 0)
        {
            throw new FormatException("The statement holds no FlexStatement.");
        }

        if (statements.Count != 1
            || !int.TryParse(statements[0].Attribute("count")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count != elements.Count)
        {
            throw new FormatException(
                $"The statement says it holds {statements.FirstOrDefault()?.Attribute("count")?.Value} statements and holds {elements.Count}: it is not whole.");
        }

        var periods = elements.Select(ReadPeriod).ToList();
        var generated = elements.Select(s => ParseDateTime(Required(s, "whenGenerated", "FlexStatement"), "whenGenerated")).Max();
        return new FlexStatement(root.Attribute("queryName")?.Value ?? "", generated, periods);
    }

    private static FlexPeriod ReadPeriod(XElement statement)
    {
        var from = ParseDate(Required(statement, "fromDate", "FlexStatement"), "fromDate");
        var to = ParseDate(Required(statement, "toDate", "FlexStatement"), "toDate");
        var where = from == to
            ? string.Create(CultureInfo.InvariantCulture, $"the statement of {from:R}")
            : string.Create(CultureInfo.InvariantCulture, $"the statement of {from:R} to {to:R}");
        try
        {
            var account = Required(statement, "accountId", "FlexStatement");
            var info = statement.Element("AccountInformation");
            var baseCurrency = info?.Attribute("currency")?.Value is { Length: > 0 } code ? code : "";
            var trades = Rows(statement, "Trades", "Trade").Select(ReadTrade).ToList();
            var cash = Rows(statement, "CashTransactions", "CashTransaction").Select(ReadCash).ToList();
            var report = Rows(statement, "CashReport", "CashReportCurrency").Select(r => new FlexCashReport(
                Required(r, "currency", "CashReportCurrency"),
                Number(r, "startingCash"),
                Number(r, "endingCash"))).ToList();
            var positions = Rows(statement, "OpenPositions", "OpenPosition")
                .Where(p => Optional(p, "levelOfDetail") is "SUMMARY" or "")
                .Select(p => new FlexPosition(
                    Required(p, "assetCategory", "OpenPosition"),
                    ConId(p) ?? throw new FormatException("<OpenPosition> has no conid."),
                    Optional(p, "symbol"),
                    Number(p, "position")))
                .ToList();
            var unread = SectionsNotRead.Where(s => statement.Element(s)?.HasElements == true).ToList();
            var missing = SectionsRequired.Where(s => statement.Element(s) is null).ToList();
            return new FlexPeriod(account, baseCurrency, from, to, trades, cash, report, positions, unread, missing);
        }
        catch (FormatException e)
        {
            throw new FormatException($"In {where}: {e.Message}", e);
        }
    }

    private static IEnumerable<XElement> Rows(XElement statement, string section, string row) =>
        statement.Elements(section).Elements(row);

    private static FlexTrade ReadTrade(XElement row)
    {
        const string element = "Trade";
        var id = Required(row, "transactionID", element);
        try
        {
            return new FlexTrade(
                id,
                Optional(row, "transactionType"),
                Optional(row, "levelOfDetail"),
                Required(row, "assetCategory", element),
                Required(row, "symbol", element),
                ConId(row),
                Optional(row, "isin") is { Length: > 0 } isin ? isin : null,
                Required(row, "currency", element),
                ParseDate(Required(row, "tradeDate", element), "tradeDate"),
                ParseDate(Required(row, "settleDateTarget", element), "settleDateTarget"),
                ParseDateTime(Required(row, "dateTime", element), "dateTime"),
                Optional(row, "buySell"),
                Number(row, "quantity"),
                Number(row, "tradePrice"),
                Number(row, "proceeds"),
                Number(row, "ibCommission"),
                Required(row, "ibCommissionCurrency", element),
                Number(row, "netCash"),
                Optional(row, "notes"));
        }
        catch (FormatException e)
        {
            throw new FormatException($"trade {id}: {e.Message}", e);
        }
    }

    private static FlexCash ReadCash(XElement row)
    {
        const string element = "CashTransaction";
        var id = Optional(row, "transactionID") is { Length: > 0 } given ? given : null;
        try
        {
            var stamp = Required(row, "dateTime", element);
            var settle = Optional(row, "settleDate");
            var report = Optional(row, "reportDate");
            return new FlexCash(
                id,
                Optional(row, "levelOfDetail"),
                Required(row, "type", element),
                Required(row, "currency", element),
                ConId(row),
                Optional(row, "symbol"),
                Number(row, "amount"),
                // A summary row carries only the date, a detail row the date and the time.
                stamp.Contains(';', StringComparison.Ordinal)
                    ? ParseDateTime(stamp, "dateTime").Date
                    : ParseDate(stamp, "dateTime"),
                settle.Length > 0 ? ParseDate(settle, "settleDate") : null,
                report.Length > 0 ? ParseDate(report, "reportDate") : null,
                Optional(row, "actionID"),
                Optional(row, "description"));
        }
        catch (FormatException e)
        {
            throw new FormatException($"cash transaction {id ?? "(summary)"}: {e.Message}", e);
        }
    }

    private static int? ConId(XElement row)
    {
        var text = Optional(row, "conid");
        if (text.Length == 0)
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var conId) && conId > 0
            ? conId
            : throw new FormatException($"conid '{text}' is not a contract id.");
    }

    private static string Required(XElement row, string attribute, string element) =>
        row.Attribute(attribute)?.Value is { Length: > 0 } value
            ? value
            : throw new FormatException($"<{element}> has no {attribute}.");

    private static string Optional(XElement row, string attribute) => row.Attribute(attribute)?.Value ?? "";

    private static decimal Number(XElement row, string attribute)
    {
        var text = Optional(row, attribute);
        if (text.Length == 0)
        {
            throw new FormatException($"{attribute} is missing.");
        }

        return decimal.TryParse(
            text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{attribute} '{text}' is not a number.");
    }

    private static LocalDate ParseDate(string text, string attribute) =>
        Date.Parse(text) is { Success: true } parsed
            ? parsed.Value
            : throw new FormatException($"{attribute} '{text}' is not a date as yyyyMMdd (set so in the Flex query).");

    private static LocalDateTime ParseDateTime(string text, string attribute) =>
        DateTime.Parse(text) is { Success: true } parsed
            ? parsed.Value
            : throw new FormatException($"{attribute} '{text}' is not yyyyMMdd;HHmmss (set so in the Flex query).");
}
