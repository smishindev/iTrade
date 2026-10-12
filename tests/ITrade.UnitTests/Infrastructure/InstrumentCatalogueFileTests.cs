using ITrade.Application.Portfolio;
using ITrade.Domain;
using ITrade.Infrastructure.Portfolio;

namespace ITrade.UnitTests.Infrastructure;

/// <summary>P3.1.01: reading the instrument catalogue file — and refusing a file with an error in it.</summary>
public sealed class InstrumentCatalogueFileTests
{
    private const string Header = "ticker,exchange,currency,name,isin,ibkr_con_id";

    private static IReadOnlyList<InstrumentListing> Read(params string[] lines) =>
        InstrumentCatalogueFile.Read(new StringReader(string.Join('\n', lines)));

    [Fact]
    public void ReadsListingsSkippingNotesAndEmptyLines()
    {
        var listings = Read(
            "# a note",
            Header,
            "",
            "SPY,ARCA,USD,SPDR S&P 500 ETF Trust,US78462F1030,756733",
            "voo , arca ,usd, Vanguard S&P 500 ETF ,,",
            "\"BRK B\",NYSE,USD,\"Berkshire Hathaway, Inc. \"\"B\"\"\",US0846707026,72063691");

        Assert.Equal(3, listings.Count);
        Assert.Equal(
            new InstrumentListing(new Ticker("SPY"), "ARCA", Currency.Usd, "SPDR S&P 500 ETF Trust", new Isin("US78462F1030"), 756733),
            listings[0]);
        Assert.Equal((new Ticker("VOO"), "arca", "Vanguard S&P 500 ETF", null, null),
            (listings[1].Ticker, listings[1].Exchange, listings[1].Name, listings[1].Isin, listings[1].IbkrConId));
        Assert.Equal((new Ticker("BRK B"), "Berkshire Hathaway, Inc. \"B\""), (listings[2].Ticker, listings[2].Name));
    }

    [Fact]
    public void ColumnsMayComeInAnyOrder()
    {
        var listings = Read("name,ibkr_con_id,isin,currency,exchange,ticker", "S&P 500,756733,US78462F1030,USD,ARCA,SPY");

        Assert.Equal((new Ticker("SPY"), "ARCA", 756733), (listings[0].Ticker, listings[0].Exchange, listings[0].IbkrConId));
    }

    [Theory]
    [InlineData("SPY,ARCA,USD,S&P 500,US78462F1031,756733", "Line 2", "ISIN")] // wrong check digit
    [InlineData("SPY,ARCA,EUR,S&P 500,US78462F1030,756733", "Line 2", "currency")]
    [InlineData("SPY,ARCA,USD,S&P 500,US78462F1030,7567x3", "Line 2", "")]
    [InlineData("SPY,ARCA,USD,S&P 500,US78462F1030,-5", "Line 2", "")]
    [InlineData("S$Y,ARCA,USD,S&P 500,US78462F1030,756733", "Line 2", "ticker")]
    [InlineData("SPY,ARCA,USD,S&P 500,US78462F1030", "Line 2", "5 values")]
    [InlineData("SPY,ARCA,USD,\"S&P 500,US78462F1030,756733", "Line 2", "not closed")]
    public void LineWithAnErrorStopsTheFileAndIsNamed(string line, string where, string what)
    {
        var error = Assert.Throws<FormatException>(() => Read(Header, line));

        Assert.StartsWith(where, error.Message, StringComparison.Ordinal);
        Assert.Contains(what, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileWithoutItsColumnsIsRefused()
    {
        var missing = Assert.Throws<FormatException>(() => Read("ticker,exchange,name", "SPY,ARCA,S&P 500"));
        Assert.Contains("currency, isin, ibkr_con_id", missing.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => Read("# only a note"));
    }
}
