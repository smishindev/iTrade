using CsCheck;
using ITrade.Domain;

namespace ITrade.UnitTests.Domain;

/// <summary>P2.2.03: instruments and holding places — the types the accounting of phase 3 is built on.</summary>
public sealed class PortfolioTypesTests
{
    private static Instrument Voo(
        string exchange = "ARCA", string name = "Vanguard S&P 500 ETF", Isin? isin = null, int? conId = 136155102) =>
        new(InstrumentId.New(), new Ticker("VOO"), exchange, Currency.Usd, name, isin, conId);

    [Theory]
    [InlineData("VOO", "VOO")]
    [InlineData(" voo ", "VOO")]
    [InlineData("brk b", "BRK B")]
    [InlineData("BF.B", "BF.B")]
    [InlineData("CSPX", "CSPX")]
    public void TickerIsTrimmedAndUpperCased(string text, string expected)
    {
        Assert.Equal(expected, new Ticker(text).Value);
        Assert.Equal(new Ticker(expected), new Ticker(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-VOO")]
    [InlineData("VOO$")]
    [InlineData("АБВ")]
    [InlineData("THIRTEENCHARS")]
    public void TickerOfAnotherShapeIsRejected(string? text)
    {
        Assert.Throws<ArgumentException>(() => new Ticker(text!));
    }

    [Theory]
    [InlineData("US0378331005")] // Apple: the textbook example
    [InlineData("US9229083632")] // VOO
    [InlineData("US78462F1030")] // SPY: a letter inside the identifier
    [InlineData("IE00B4L5Y983")] // an Irish UCITS ETF
    public void IsinWithACorrectCheckDigitIsAccepted(string text)
    {
        var isin = new Isin(text.ToLowerInvariant() + " ");

        Assert.Equal(text, isin.Value);
        Assert.Equal(text[..2], isin.CountryCode);
        Assert.True(Isin.IsValid(text));
    }

    [Theory]
    [InlineData("US0378331006")] // wrong check digit
    [InlineData("US0387331005")] // two digits swapped
    [InlineData("IE00B4L5Y984")]
    [InlineData("IE00B4L5Y98")] // eleven characters
    [InlineData("IE00B4L5Y9833")] // thirteen
    [InlineData("1S0378331005")] // country is not two letters
    [InlineData("IE00B4L5Y98X")] // check digit is not a digit
    [InlineData("IE00B4L-Y983")]
    [InlineData("")]
    [InlineData(null)]
    public void IsinWithAnErrorIsRejected(string? text)
    {
        Assert.False(Isin.IsValid(text));
        Assert.Throws<ArgumentException>(() => new Isin(text!));
    }

    [Fact]
    public void ChangingAnyOneDigitOfAnIsinIsNoticed()
    {
        const string valid = "US9229083632";
        var position = Gen.Int[2, Isin.Length - 1];
        var digit = Gen.Char['0', '9'];

        position.Select(digit).Sample((at, replacement) =>
        {
            if (!char.IsAsciiDigit(valid[at]) || valid[at] == replacement)
            {
                return true;
            }

            var changed = valid[..at] + replacement + valid[(at + 1)..];
            return !Isin.IsValid(changed);
        });
    }

    [Fact]
    public void InstrumentKeepsWhatItWasGivenNormalised()
    {
        var instrument = Voo(exchange: " arca ", name: "  Vanguard S&P 500 ETF ", isin: new Isin("US9229083632"));

        Assert.Equal("VOO", instrument.Ticker.Value);
        Assert.Equal("ARCA", instrument.Exchange);
        Assert.Equal(Currency.Usd, instrument.Currency);
        Assert.Equal("Vanguard S&P 500 ETF", instrument.Name);
        Assert.Equal("US9229083632", instrument.Isin?.Value);
        Assert.Equal(136155102, instrument.IbkrConId);
    }

    [Fact]
    public void InstrumentHeldOutsideIbkrNeedsNoContractIdOrIsin()
    {
        var instrument = Voo(conId: null);

        Assert.Null(instrument.IbkrConId);
        Assert.Null(instrument.Isin);
    }

    [Fact]
    public void InstrumentWithMissingOrImpossibleDataIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Voo(exchange: ""));
        Assert.Throws<ArgumentException>(() => Voo(exchange: "NYSE ARCA"));
        Assert.Throws<ArgumentException>(() => Voo(name: "   "));
        Assert.Throws<ArgumentException>(() => Voo(name: new string('x', Instrument.MaxNameLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Voo(conId: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Voo(conId: -5));
        Assert.Throws<ArgumentException>(() =>
            new Instrument(default, new Ticker("VOO"), "ARCA", Currency.Usd, "Vanguard S&P 500 ETF"));
        Assert.Throws<ArgumentException>(() =>
            new Instrument(InstrumentId.New(), default, "ARCA", Currency.Usd, "Vanguard S&P 500 ETF"));
        Assert.Throws<ArgumentException>(() =>
            new Instrument(InstrumentId.New(), new Ticker("VOO"), "ARCA", default, "Vanguard S&P 500 ETF"));
    }

    [Fact]
    public void TwoInstrumentsAreTheSameOnlyWithTheSameIdAndData()
    {
        var id = InstrumentId.New();
        var one = new Instrument(id, new Ticker("VOO"), "ARCA", Currency.Usd, "Vanguard S&P 500 ETF");
        var same = new Instrument(id, new Ticker("voo"), "arca", Currency.Usd, "Vanguard S&P 500 ETF");
        var other = new Instrument(InstrumentId.New(), new Ticker("VOO"), "ARCA", Currency.Usd, "Vanguard S&P 500 ETF");

        Assert.Equal(one, same);
        Assert.NotEqual(one, other);
    }

    [Theory]
    [InlineData(HoldingPlaceKind.Broker, IntegrationKind.Broker, false)] // IBKR
    [InlineData(HoldingPlaceKind.Broker, IntegrationKind.Manual, true)] // an Israeli broker, from statements
    [InlineData(HoldingPlaceKind.StudyFund, IntegrationKind.Manual, true)] // keren hishtalmut
    [InlineData(HoldingPlaceKind.Other, IntegrationKind.Manual, true)]
    public void HoldingPlaceOfEveryAllowedKind(HoldingPlaceKind kind, IntegrationKind integration, bool manual)
    {
        var place = new HoldingPlace(HoldingPlaceId.New(), "  My account ", kind, integration);

        Assert.Equal("My account", place.Name);
        Assert.Equal(kind, place.Kind);
        Assert.Equal(integration, place.Integration);
        Assert.Equal(manual, place.IsManual);
    }

    [Theory]
    [InlineData(HoldingPlaceKind.StudyFund)]
    [InlineData(HoldingPlaceKind.Other)]
    public void OnlyABrokerageAccountCanBeReadAutomatically(HoldingPlaceKind kind)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new HoldingPlace(HoldingPlaceId.New(), "Keren", kind, IntegrationKind.Broker));

        Assert.Contains("entered by hand", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HoldingPlaceWithMissingOrImpossibleDataIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new HoldingPlace(default, "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker));
        Assert.Throws<ArgumentException>(() =>
            new HoldingPlace(HoldingPlaceId.New(), " ", HoldingPlaceKind.Broker, IntegrationKind.Broker));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HoldingPlace(HoldingPlaceId.New(), "IBKR", (HoldingPlaceKind)99, IntegrationKind.Manual));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, (IntegrationKind)99));
    }
}
