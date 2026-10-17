using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ITrade.Broker.IBKR.Flex;
using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Broker;

/// <summary>
/// P3.2.02: reading an IBKR Activity Flex statement and turning it into ledger entries. The fixture is the
/// statement of the owner's paper account of 16 October 2026, anonymised with tools/anonymise_flex.py (account
/// U0000000, no name, e-mail or country; conversion rates left out), days 7 October (the deposit), 8 October
/// (interest accrued, no cash), 14 October (100 VT in 18 executions) and 15 October (3 VT in 3 executions, 100 USD
/// changed into shekels). The cases the statement does not have are written into a copy of it. How the statement
/// is held against the ledger is tested through the real Host (StatementImportTests).
/// </summary>
public sealed class FlexStatementTests
{
    private const string Ordinary = "VT(US9220427424) Cash Dividend USD 0.35 per Share (Ordinary Dividend)";
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flex-statement.xml");
    private static readonly HoldingPlaceId Place = new(Guid.Parse("0199f000-0000-7000-8000-000000000001"));
    private static readonly Instant Recorded = Instant.FromUtc(2026, 10, 16, 12, 0);
    private static readonly Instrument Vt = new(
        InstrumentId.New(), new Ticker("VT"), "ARCA", Currency.Usd, "Vanguard Total World Stock ETF",
        new Isin("US9220427424"), 52197301);

    private static readonly Dictionary<int, Instrument> Catalogue = new() { [52197301] = Vt };

    private static LocalDate Day(int day) => new(2026, 10, day);

    private static XDocument Fixture() => XDocument.Load(FixturePath);

    private static FlexStatement Read(XDocument document) =>
        FlexStatementFile.Read(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));

    private static FlexEntries Entries(XDocument document, IReadOnlyDictionary<int, Instrument>? catalogue = null) =>
        FlexLedgerEntries.From(Read(document), Place, catalogue ?? Catalogue, Recorded);

    private static XElement Statement(XDocument document, string day) =>
        document.Descendants("FlexStatement").Single(s => s.Attribute("fromDate")!.Value == day);

    /// <summary>Moves IBKR's reported cash of a day, as a statement with that extra line would report it.</summary>
    private static void AddToCash(XElement statement, string currency, decimal amount)
    {
        var row = statement.Element("CashReport")!.Elements("CashReportCurrency").Single(r => r.Attribute("currency")!.Value == currency);
        var ending = decimal.Parse(row.Attribute("endingCash")!.Value, CultureInfo.InvariantCulture);
        row.SetAttributeValue("endingCash", (ending + amount).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A cash transaction of 15 October as IBKR writes one, booked on the day it is dated.</summary>
    private static XElement CashLine(
        string id, string type, decimal amount, string conid = "", string symbol = "", string action = "", string? description = null) =>
        new("CashTransaction",
            new XAttribute("accountId", "U0000000"), new XAttribute("currency", "USD"), new XAttribute("symbol", symbol),
            new XAttribute("conid", conid), new XAttribute("description", description ?? $"{symbol} {type}"),
            new XAttribute("dateTime", "20261015;202000"), new XAttribute("settleDate", "20261015"),
            new XAttribute("reportDate", "20261015"), new XAttribute("actionID", action),
            new XAttribute("amount", amount.ToString(CultureInfo.InvariantCulture)), new XAttribute("type", type),
            new XAttribute("transactionID", id), new XAttribute("levelOfDetail", "DETAIL"));

    private static XElement Dividend(string id, decimal amount, string action) =>
        CashLine(id, "Dividends", amount, "52197301", "VT", action, Ordinary);

    private static XElement Tax(string id, decimal amount, string action) =>
        CashLine(id, "Withholding Tax", amount, "52197301", "VT", action, Ordinary + " - US Tax");

    private static XDocument WithCash(decimal moved, params XElement[] lines)
    {
        var document = Fixture();
        var day = Statement(document, "20261015");
        day.Element("CashTransactions")!.Add(lines);
        AddToCash(day, "USD", moved);
        return document;
    }

    [Fact]
    public void ReadsTheDaysTheTradesTheCashAndThePositionsOfTheStatement()
    {
        var statement = FlexStatementFile.Read(FixturePath);

        Assert.Equal("iTrade", statement.QueryName);
        Assert.Equal(new LocalDateTime(2026, 10, 16, 8, 41, 25), statement.Generated);
        Assert.Equal(["U0000000"], statement.Accounts);
        Assert.Equal([Day(7), Day(8), Day(14), Day(15)], statement.Periods.Select(p => p.From));
        Assert.Equal([0, 0, 18, 4], statement.Periods.Select(p => p.Trades.Count));
        var deposit = statement.Periods[0].Cash;
        Assert.Equal(["SUMMARY", "DETAIL"], deposit.Select(c => c.LevelOfDetail));
        Assert.Equal((null, "10971559768", (LocalDate?)Day(7)), (deposit[0].TransactionId, deposit[1].TransactionId, deposit[1].ReportDate));
        var first = statement.Periods[2].Trades[0];
        Assert.Equal(
            ("STK", 52197301, "US9220427424", Day(14), Day(15), new LocalDateTime(2026, 10, 14, 18, 5, 3), 5m, 157.67m, -788.35m, -1.000015m, -789.350015m),
            (first.Category, first.ConId, first.Isin, first.TradeDate, first.SettleDate, first.ExecutedAt, first.Quantity, first.TradePrice, first.Proceeds, first.Commission, first.NetCash));
        var conversion = statement.Periods[3].Trades[3];
        Assert.Equal(("CASH", "USD.ILS", "ILS", -100m, 3.0994m, 309.94m, -2m, "USD"), (conversion.Category, conversion.Symbol, conversion.Currency, conversion.Quantity, conversion.TradePrice, conversion.Proceeds, conversion.Commission, conversion.CommissionCurrency));
        Assert.Equal([new FlexPosition("STK", 52197301, "VT", 103m)], statement.Periods[3].OpenPositions);
        Assert.Empty(statement.Periods[1].OpenPositions);
        // Which days carry rows per currency depends on the download: here 7 October has only the base summary.
        Assert.Equal([0, 2, 2, 2], statement.Periods.Select(p => p.PerCurrency.Count));
        Assert.All(statement.Periods, p => Assert.Empty(p.Unread));
        Assert.All(statement.Periods, p => Assert.Empty(p.Missing));
    }

    [Fact]
    public void EveryExecutionIsAnEntryWithItsCommissionAndAConversionIsTwoLegs()
    {
        var built = Entries(Fixture());

        Assert.Empty(built.Refusals);
        Assert.Equal([Day(7)], built.DaysNotChecked);
        Assert.Equal(1 + (18 * 2) + (3 * 2) + 3, built.Entries.Count);
        Assert.All(built.Entries, e => Assert.Equal((Place, TransactionSource.Flex, Recorded), (e.HoldingPlace, e.Source, e.RecordedAt)));
        Assert.Equal(built.Entries.Count, built.Entries.Select(e => e.ExternalId).Distinct().Count());

        var deposit = built.Entries[0];
        Assert.Equal((TransactionKind.Deposit, Day(7), Day(7), Money.Usd(1_000_000m), "10971559768", (string?)null), (deposit.Kind, deposit.TradeDate, deposit.SettlementDate, deposit.Cash, deposit.ExternalId, deposit.Note));

        var buy = built.Entries[1];
        var fee = built.Entries[2];
        Assert.Equal((TransactionKind.Buy, Day(14), Day(15), Money.Usd(-788.35m), 5m, (Price?)new Price(157.67m, Currency.Usd), (InstrumentId?)Vt.Id, "11006946599"), (buy.Kind, buy.TradeDate, buy.SettlementDate, buy.Cash, buy.QuantityChange, buy.Price, buy.Instrument, buy.ExternalId));
        Assert.Equal((TransactionKind.Fee, Money.Usd(-1.000015m), (TransactionId?)buy.Id, "11006946599/commission", (InstrumentId?)Vt.Id), (fee.Kind, fee.Cash, fee.RelatedTo, fee.ExternalId, fee.Instrument));

        var legs = built.Entries.Where(e => e.Kind == TransactionKind.FxConversion).ToList();
        Assert.Equal((Money.Usd(-100m), Money.Ils(309.94m)), (legs[0].Cash, legs[1].Cash));
        Assert.Equal((Day(15), Day(19), "11013041293", "11013041293/ILS", (TransactionId?)legs[0].Id), (legs[0].TradeDate, legs[0].SettlementDate, legs[0].ExternalId, legs[1].ExternalId, legs[1].RelatedTo));
        var conversionFee = built.Entries[^1];
        Assert.Equal((TransactionKind.Fee, Money.Usd(-2m), (TransactionId?)legs[0].Id, (InstrumentId?)null), (conversionFee.Kind, conversionFee.Cash, conversionFee.RelatedTo, conversionFee.Instrument));
    }

    [Fact]
    public void TheBooksOfTheStatementAreIbkrsOwn()
    {
        var books = PortfolioBooks.From(Entries(Fixture()).Entries, Day(15));

        // IBKR's own figures of 15 October: endingCash per currency, and OpenPositions of VT.
        Assert.Equal(Money.Usd(983_654.099691m), books.CashOf(Place, Currency.Usd));
        Assert.Equal(Money.Ils(309.94m), books.CashOf(Place, Currency.Ils));
        var vt = Assert.Single(books.Positions);
        Assert.Equal(103m, vt.Quantity.Value);
        Assert.Equal(21, vt.Lots.Count); // a lot per execution, as IBKR keeps them
        Assert.Equal(16_243.900309m, (vt.Cost + vt.Fees).Amount); // costBasisMoney: IBKR counts the commission in
        Assert.Equal(Money.Usd(2.000309m), vt.Fees);
        Assert.Equal(Money.Usd(1.000015m), vt.Lots[0].Fees); // the minimum commission sits on the first execution
    }

    [Fact]
    public void ExecutionsAreRecordedInTheOrderTheyWereExecuted()
    {
        // A sale listed before the purchase it sells would leave the books unable to be worked out.
        var document = Fixture();
        var trades = Statement(document, "20261014").Element("Trades")!;
        var first = trades.Elements("Trade").First();
        first.SetAttributeValue("dateTime", "20261014;180504");
        first.Remove();
        trades.AddFirst(first); // still listed first, executed last

        var buys = Entries(document).Entries.Where(e => e is { Kind: TransactionKind.Buy, TradeDate.Day: 14 }).ToList();

        Assert.Equal("11006946599", buys[^1].ExternalId);
        Assert.Equal("11006946600", buys[0].ExternalId);
    }

    [Fact]
    public void AnInstrumentThatIsNotInTheCatalogueStopsTheStatement()
    {
        var built = Entries(Fixture(), new Dictionary<int, Instrument>());

        Assert.Equal(21, built.Refusals.Count(r => r.Contains("IBKR contract 52197301 (US9220427424) is not in the instrument catalogue", StringComparison.Ordinal)));
    }

    [Fact]
    public void CashThatTheEntriesDoNotExplainStopsTheStatement()
    {
        // A line IBKR booked that the import would not see: here, an execution taken out of the statement.
        var document = Fixture();
        Statement(document, "20261014").Element("Trades")!.Elements("Trade").Last().Remove();

        Assert.Equal(
            ["2026-10-14: the entries move -13402.950255 USD and IBKR's Cash Report -15768.0003 USD: something in the statement is not read."],
            Entries(document).Refusals);
    }

    [Theory]
    [InlineData("CashReport")]
    [InlineData("OpenPositions")]
    [InlineData("Trades")]
    [InlineData("CashTransactions")]
    [InlineData("CorporateActions")]
    [InlineData("Transfers")]
    public void APeriodWithoutASectionTheImportNeedsIsNotRecorded(string section)
    {
        // A query that lost CorporateActions would let a split pass: it moves shares, not cash.
        var document = Fixture();
        Statement(document, "20261008").Element(section)!.Remove();

        Assert.Contains(
            $"2026-10-08: the section {section} is not in the statement; the Flex query has to include it (even empty), or shares or money could pass unseen.",
            Entries(document).Refusals);
    }

    [Theory]
    [InlineData("transactionType", "TradeCancel", "is a 'TradeCancel'")]
    [InlineData("transactionType", "BookTrade", "is a 'BookTrade'")]
    [InlineData("notes", "Ca", "(notes 'Ca')")]
    [InlineData("notes", "Co", "(notes 'Co')")]
    [InlineData("notes", "P;R", "(notes 'P;R')")]
    [InlineData("levelOfDetail", "ORDER", "is at the level 'ORDER'")]
    [InlineData("assetCategory", "OPT", "asset category 'OPT'")]
    [InlineData("currency", "EUR", "Unsupported currency 'EUR'")]
    [InlineData("conid", "1", "IBKR contract 1 (US9220427424) is not in the instrument catalogue")]
    [InlineData("isin", "US9229083632", "ISIN US9229083632 in the statement, US9220427424 in the catalogue")]
    [InlineData("proceeds", "-788.40", "5 x 157.67 is not the cash of the trade, -788.40")]
    [InlineData("buySell", "SELL", "'SELL' of 5 shares for -788.35 does not add up")]
    [InlineData("ibCommissionCurrency", "ILS", "the commission is in ILS and the trade in USD")]
    [InlineData("netCash", "-789.35", "are not the net cash -789.35")]
    [InlineData("tradeDate", "20261013", "is of 2026-10-13, outside the period it is listed in")]
    public void ATradeTheImportCannotBeSureOfIsRefusedNotGuessed(string attribute, string value, string said)
    {
        var document = Fixture();
        Statement(document, "20261014").Element("Trades")!.Elements("Trade").First().SetAttributeValue(attribute, value);

        var refusals = Entries(document).Refusals;

        Assert.Contains(refusals, r => r.StartsWith("2026-10-14: trade 11006946599 (VT)", StringComparison.Ordinal) && r.Contains(said, StringComparison.Ordinal));
    }

    [Fact]
    public void ACommissionWithMoreDecimalsThanTheLedgerKeepsIsRefusedNotRounded()
    {
        var document = Fixture();
        var trade = Statement(document, "20261014").Element("Trades")!.Elements("Trade").First();
        trade.SetAttributeValue("ibCommission", "-1.0000151");
        trade.SetAttributeValue("netCash", "-789.3500151");

        Assert.Contains(
            Entries(document).Refusals,
            r => r.StartsWith("2026-10-14: trade 11006946599 (VT): The ledger keeps 6 decimal places", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("symbol", "USDILS", "is not a pair ending in the currency of the row")]
    [InlineData("proceeds", "-309.94", "-100 USD at 3.0994 is not -309.94 ILS")]
    [InlineData("tradePrice", "3.2", "-100 USD at 3.2 is not 309.94 ILS")]
    public void AConversionThatDoesNotAddUpIsRefused(string attribute, string value, string said)
    {
        var document = Fixture();
        Statement(document, "20261015").Element("Trades")!.Elements("Trade").Last().SetAttributeValue(attribute, value);

        Assert.Contains(Entries(document).Refusals, r => r.Contains("trade 11013041293", StringComparison.Ordinal) && r.Contains(said, StringComparison.Ordinal));
    }

    [Fact]
    public void ASectionThatMovesSharesOrMoneyAndIsNotReadStopsTheStatement()
    {
        var document = Fixture();
        Statement(document, "20261015").Element("CorporateActions")!.Add(new XElement("CorporateAction", new XAttribute("type", "FS")));

        Assert.Contains(
            "2026-10-15: the section CorporateActions has rows; the import does not read them yet (never seen on a statement of this account).",
            Entries(document).Refusals);
    }

    [Fact]
    public void DividendsWithheldTaxInterestAndFeesAreCashOfTheirKind()
    {
        var summary = Dividend("", 35m, "777");
        summary.SetAttributeValue("levelOfDetail", "SUMMARY"); // IBKR's sum of the day, without an id: not read
        var document = WithCash(
            35m - 8.75m + 12.34m - 1.5m,
            Tax("9001", -8.75m, "777"), Dividend("9002", 35m, "777"), CashLine("9003", "Broker Interest Received", 12.34m),
            CashLine("9004", "Other Fees", -1.5m), summary);

        var built = Entries(document);

        Assert.Empty(built.Refusals);
        var cash = built.Entries.Where(e => e.ExternalId is "9001" or "9002" or "9003" or "9004").ToList();
        // The dividend comes before the tax withheld from it, whatever the order of the statement.
        Assert.Equal(
            [(TransactionKind.Dividend, 35m), (TransactionKind.TaxWithheld, -8.75m), (TransactionKind.Interest, 12.34m), (TransactionKind.Fee, -1.5m)],
            cash.Select(e => (e.Kind, e.Cash.Amount)));
        Assert.Equal(((TransactionId?)cash[0].Id, (InstrumentId?)Vt.Id, (InstrumentId?)Vt.Id), (cash[1].RelatedTo, cash[0].Instrument, cash[1].Instrument));
        Assert.Equal(Ordinary, cash[0].Note);
        Assert.Equal(((InstrumentId?)null, (InstrumentId?)null), (cash[2].Instrument, cash[3].Instrument));
    }

    [Fact]
    public void WithheldTaxBelongsToTheDividendOfItsCorporateEventNotOfItsDay()
    {
        // Two dividends of the fund on one day: the tax goes with the one of the same actionID.
        var document = WithCash(35m + 5m - 1.25m, Dividend("9002", 35m, "777"), Dividend("9005", 5m, "888"), Tax("9001", -1.25m, "888"));

        var entries = Entries(document).Entries;

        Assert.Equal(entries.Single(e => e.ExternalId == "9005").Id, entries.Single(e => e.ExternalId == "9001").RelatedTo);
    }

    [Theory]
    [InlineData("999", "(actionID '999') is not in this statement")]
    [InlineData("", "(actionID '') is not in this statement")]
    public void WithheldTaxThatCannotBeToldToItsDividendIsRefused(string action, string said)
    {
        // A refund of tax months after its dividend, or a tax posted without its event.
        var document = WithCash(35m + 2.5m, Dividend("9002", 35m, "777"), Tax("9001", 2.5m, action));

        Assert.Contains(Entries(document).Refusals, r => r.StartsWith("2026-10-15: cash transaction 9001 (Withholding Tax)", StringComparison.Ordinal) && r.Contains(said, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("VT(US9220427424) Cash Dividend USD 0.35 per Share (Return of Capital)")]
    [InlineData("VT(US9220427424) Cash Dividend USD 0.35 per Share (Long Term Capital Gain)")]
    [InlineData("VT(US9220427424) Bonus Dividend USD 0.35 per Share")]
    public void ADistributionThatIsNotAnOrdinaryDividendIsRefused(string description)
    {
        // A return of capital lowers the cost and is not taxed as a dividend: it is not guessed to be one.
        var document = WithCash(35m, CashLine("9002", "Dividends", 35m, "52197301", "VT", "777", description));

        Assert.Contains(
            $"2026-10-15: cash transaction 9002 (Dividends): '{description}' is not described as an ordinary dividend; another kind of distribution is not guessed.",
            Entries(document).Refusals);
    }

    [Theory]
    [InlineData("reportDate", "20261016")]
    [InlineData("dateTime", "20261014;202000")]
    [InlineData("settleDate", "20261014")]
    [InlineData("reportDate", "")]
    public void ACashTransactionWhoseDatesDifferIsRefused(string attribute, string value)
    {
        // Interest dated at the end of a month and booked at the start of the next has not been seen yet.
        var line = CashLine("9003", "Broker Interest Received", 12.34m);
        line.SetAttributeValue(attribute, value);
        var document = WithCash(12.34m, line);

        Assert.Contains(Entries(document).Refusals, r => r.StartsWith("2026-10-15: cash transaction 9003 (Broker Interest Received) is dated", StringComparison.Ordinal) && r.EndsWith("a row whose dates differ has not been seen yet and is not guessed.", StringComparison.Ordinal));
    }

    [Fact]
    public void ADepositIbkrBookedAfterTheDayItIsDatedIsRecordedOnTheDayOfTheBooking()
    {
        // As the first deposit of the real account (17 October 2026): dated and settling one day, booked the next.
        var line = CashLine("9010", "Deposits/Withdrawals", 100m, description: "CASH RECEIPTS / ELECTRONIC FUND TRANSFERS");
        line.SetAttributeValue("dateTime", "20261014");
        line.SetAttributeValue("settleDate", "20261014");
        var built = Entries(WithCash(100m, line));

        Assert.Empty(built.Refusals);
        var deposit = built.Entries.Single(e => e.ExternalId == "9010");
        Assert.Equal(
            (TransactionKind.Deposit, Day(15), Day(15), Money.Usd(100m), (string?)"dated 2026-10-14 by IBKR, booked 2026-10-15"),
            (deposit.Kind, deposit.TradeDate, deposit.SettlementDate, deposit.Cash, deposit.Note));
        Assert.Equal(Money.Usd(983_754.099691m), PortfolioBooks.From(built.Entries, Day(15)).CashOf(Place, Currency.Usd));
    }

    [Fact]
    public void FourDaysBetweenTheDateAndTheBookingAreRead()
    {
        // A deposit dated on a Friday before a holiday Monday and booked on the Tuesday.
        var line = CashLine("9012", "Deposits/Withdrawals", 100m);
        line.SetAttributeValue("dateTime", "20261011");
        line.SetAttributeValue("settleDate", "20261011");

        Assert.Equal(Day(15), Entries(WithCash(100m, line)).Entries.Single(e => e.ExternalId == "9012").TradeDate);
    }

    [Fact]
    public void ASeveralDayPeriodIsCheckedOnlyAsAWholeAndSaysSo()
    {
        var built = Entries(Fixture());
        var document = Fixture();
        Statement(document, "20261015").SetAttributeValue("toDate", "20261016");

        Assert.Empty(built.CheckedOnlyAsAWhole);
        Assert.Equal("4 day(s)", Read(Fixture()).Shape);
        Assert.Equal([Day(15)], Entries(document).CheckedOnlyAsAWhole);
        Assert.Equal("4 period(s), not broken out by day", Read(document).Shape);
    }

    [Theory]
    [InlineData("Deposits/Withdrawals", 100, "20261014", "20261014", "20261016")] // booked after the period it is listed in
    [InlineData("Deposits/Withdrawals", 100, "20261012", "20261012", "20261014")] // booked before the period it is listed in
    [InlineData("Deposits/Withdrawals", 100, "20261014", "20261016", "20261015")] // settles after it was booked
    [InlineData("Deposits/Withdrawals", 100, "20261014", "20261013", "20261015")] // settles before it is dated
    [InlineData("Deposits/Withdrawals", 100, "20261014", "", "20261015")] // no settlement date
    [InlineData("Deposits/Withdrawals", 100, "20261010", "20261010", "20261015")] // five days later: not seen
    [InlineData("Deposits/Withdrawals", -100, "20261014", "20261014", "20261015")] // a withdrawal, or a returned deposit
    [InlineData("Other Fees", -10, "20261014", "20261014", "20261015")] // booked later, but not a deposit
    public void OnlyADepositBookedAFewDaysLaterWithinThePeriodIsRead(string type, int amount, string dated, string settles, string booked)
    {
        var line = CashLine("9011", type, amount);
        line.SetAttributeValue("dateTime", dated);
        line.SetAttributeValue("settleDate", settles);
        line.SetAttributeValue("reportDate", booked);
        var document = WithCash(amount, line);

        Assert.Contains(Entries(document).Refusals, r => r.StartsWith($"2026-10-15: cash transaction 9011 ({type}) is dated", StringComparison.Ordinal) && r.EndsWith("a row whose dates differ has not been seen yet and is not guessed.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Payment In Lieu Of Dividends")]
    [InlineData("Commission Adjustments")]
    public void AKindOfCashTransactionNotSeenYetIsRefused(string type)
    {
        var document = WithCash(1m, CashLine("9009", type, 1m, "52197301", "VT"));

        Assert.Contains(
            $"2026-10-15: cash transaction 9009 ({type}): this kind of cash transaction has not been seen yet and is not guessed.",
            Entries(document).Refusals);
    }

    [Fact]
    public void ACashTransactionWithoutAnIdIsRefused()
    {
        var document = WithCash(35m, Dividend("", 35m, "777"));

        Assert.Contains(
            "2026-10-15: cash transaction (no id) (Dividends) has no transaction id; the ledger cannot tell it from itself in the next import.",
            Entries(document).Refusals);
    }

    [Theory]
    [InlineData("<html/>", "This is not a Flex statement (the file starts with <html>")]
    [InlineData("<FlexStatementResponse><Status>Fail</Status></FlexStatementResponse>", "starts with <FlexStatementResponse>")]
    [InlineData("<FlexQueryResponse type=\"TF\"><FlexStatements/></FlexQueryResponse>", "of type 'TF'")]
    [InlineData("<FlexQueryResponse><FlexStatements/></FlexQueryResponse>", "of type ''")]
    [InlineData("<FlexQueryResponse type=\"AF\"><FlexStatements count=\"0\"/></FlexQueryResponse>", "holds no FlexStatement")]
    [InlineData("not xml", "The statement is not XML")]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"x\">]><FlexQueryResponse/>", "The statement is not XML")]
    public void WhatIsNotAnActivityStatementIsNotRead(string content, string said)
    {
        var e = Assert.Throws<FormatException>(() => FlexStatementFile.Read(Encoding.UTF8.GetBytes(content)));
        Assert.Contains(said, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatementWithFewerDaysThanItSaysIsNotRead()
    {
        var document = Fixture();
        Statement(document, "20261008").Remove();

        var e = Assert.Throws<FormatException>(() => Read(document));

        Assert.Equal("The statement says it holds 4 statements and holds 3: it is not whole.", e.Message);
    }

    [Fact]
    public void ADateWrittenOtherwiseThanTheQuerySaysNamesItsDayAndRow()
    {
        var document = Fixture();
        Statement(document, "20261014").Element("Trades")!.Elements("Trade").First().SetAttributeValue("tradeDate", "2026-10-14");

        var e = Assert.Throws<FormatException>(() => Read(document));

        Assert.Equal(
            "In the statement of 2026-10-14: trade 11006946599: tradeDate '2026-10-14' is not a date as yyyyMMdd (set so in the Flex query).",
            e.Message);
    }

    [Theory]
    [InlineData("DU0000000", true)]
    [InlineData("DF0000000", true)]
    [InlineData("U0000000", false)]
    [InlineData("F0000000", false)]
    public void PaperAccountsAreTold(string account, bool paper) => Assert.Equal(paper, FlexAccount.IsPaper(account));

    [Fact]
    public void AccountsAreToldApartByTheirWholeNumberAndShownMasked()
    {
        var (one, other) = (FlexAccount.Of("U0000000"), FlexAccount.Of("U00000000"));

        Assert.Equal(("U…000", "U…000"), (one.Label, other.Label));
        Assert.NotEqual(one.Key, other.Key);
        Assert.Equal(one, FlexAccount.Of(" u0000000 "));
        Assert.DoesNotContain("0000000", one.Key, StringComparison.Ordinal);
    }
}
