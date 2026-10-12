using System.Globalization;
using CsCheck;
using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Domain;

/// <summary>P3.1.02: an entry of the ledger — which way shares and cash may move, and how a mistake is cancelled.</summary>
public sealed class TransactionTests
{
    private static readonly HoldingPlaceId Ibkr = HoldingPlaceId.New();
    private static readonly InstrumentId Voo = InstrumentId.New();
    private static readonly LocalDate Traded = new(2026, 10, 12);
    private static readonly LocalDate Settled = new(2026, 10, 13);
    private static readonly Instant Recorded = Instant.FromUtc(2026, 10, 12, 21, 0);

    private static Transaction Entry(
        TransactionKind kind, decimal cash, decimal quantity = 0m, bool instrument = false, decimal? price = null,
        Currency? currency = null, TransactionId? reverses = null, string? externalId = null) =>
        new(
            TransactionId.New(), Ibkr, kind, Traded, Settled, new Money(cash, currency ?? Currency.Usd),
            TransactionSource.Flex, Recorded, instrument ? Voo : null, quantity,
            price is { } p ? new Price(p, currency ?? Currency.Usd) : null, externalId, reverses: reverses);

    [Fact]
    public void EveryKindOfEntryCanBeWrittenWithItsNaturalEffects()
    {
        var entries = new[]
        {
            Entry(TransactionKind.Buy, -1203.50m, 2m, instrument: true, price: 601.75m),
            Entry(TransactionKind.Sell, 601.75m, -1m, instrument: true, price: 601.75m),
            Entry(TransactionKind.Dividend, 3.42m, instrument: true),
            Entry(TransactionKind.TaxWithheld, -0.86m, instrument: true),
            Entry(TransactionKind.TaxWithheld, -0.86m),
            Entry(TransactionKind.Fee, -0.35m, instrument: true),
            Entry(TransactionKind.Fee, -10m),
            Entry(TransactionKind.Interest, 0.12m),
            Entry(TransactionKind.Interest, -0.07m),
            Entry(TransactionKind.FxConversion, 327.87m),
            Entry(TransactionKind.FxConversion, -1000m, currency: Currency.Ils),
            Entry(TransactionKind.Deposit, 1000m, currency: Currency.Ils),
            Entry(TransactionKind.Withdrawal, -500m),
            Entry(TransactionKind.CorporateAction, 0m, 4m, instrument: true), // a split adds shares, no cash
            Entry(TransactionKind.CorporateAction, 1.15m, instrument: true), // cash in lieu
        };

        Assert.Equal(15, entries.Length);
        Assert.All(entries, e => Assert.False(e.IsReversal));
    }

    [Theory]
    [InlineData(TransactionKind.Buy, "1203.50", "2", true, "601.75")] // cash must go out
    [InlineData(TransactionKind.Buy, "-1203.50", "-2", true, "601.75")] // shares must come in
    [InlineData(TransactionKind.Buy, "-1203.50", "2", false, "601.75")] // no instrument
    [InlineData(TransactionKind.Buy, "-1203.50", "2", true, null)] // no price
    [InlineData(TransactionKind.Sell, "-601.75", "-1", true, "601.75")]
    [InlineData(TransactionKind.Sell, "601.75", "1", true, "601.75")]
    [InlineData(TransactionKind.Dividend, "-3.42", "0", true, null)]
    [InlineData(TransactionKind.Dividend, "3.42", "1", true, null)]
    [InlineData(TransactionKind.Dividend, "3.42", "0", false, null)]
    [InlineData(TransactionKind.Dividend, "3.42", "0", true, "600")] // only a trade has a price
    [InlineData(TransactionKind.TaxWithheld, "0.86", "0", false, null)]
    [InlineData(TransactionKind.Fee, "0.35", "0", false, null)]
    [InlineData(TransactionKind.Fee, "-0.35", "1", true, null)]
    [InlineData(TransactionKind.Interest, "0", "0", false, null)]
    [InlineData(TransactionKind.Interest, "0.12", "0", true, null)] // cash only: no instrument
    [InlineData(TransactionKind.FxConversion, "0", "0", false, null)]
    [InlineData(TransactionKind.Deposit, "-1000", "0", false, null)]
    [InlineData(TransactionKind.Deposit, "1000", "0", true, null)]
    [InlineData(TransactionKind.Withdrawal, "500", "0", false, null)]
    [InlineData(TransactionKind.CorporateAction, "0", "0", true, null)] // nothing changes
    [InlineData(TransactionKind.CorporateAction, "0", "4", false, null)]
    public void EntryWithEffectsThatContradictItsKindIsRefused(
        TransactionKind kind, string cash, string quantity, bool instrument, string? price)
    {
        // Amounts arrive as text and are parsed exactly: no floating point, not even in test data.
        static decimal Exact(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

        Assert.Throws<ArgumentException>(() =>
            Entry(kind, Exact(cash), Exact(quantity), instrument, price is null ? null : Exact(price)));
    }

    [Fact]
    public void EntryWithImpossibleDatesPrecisionOrLinksIsRefused()
    {
        var id = TransactionId.New();
        Transaction Deposit(
            LocalDate? settled = null, decimal cash = 100m, TransactionId? self = null, TransactionId? reverses = null,
            TransactionId? relatedTo = null, string? externalId = null, string? note = null) =>
            new(self ?? TransactionId.New(), Ibkr, TransactionKind.Deposit, Traded, settled ?? Traded, Money.Usd(cash),
                TransactionSource.Manual, Recorded, externalId: externalId, relatedTo: relatedTo, reverses: reverses, note: note);

        Assert.Throws<ArgumentException>(() => Deposit(settled: Traded.PlusDays(-1)));
        Assert.Throws<ArgumentException>(() => Deposit(cash: 100.1234567m)); // seven decimals would be rounded
        Assert.Throws<ArgumentException>(() => Deposit(self: id, relatedTo: id));
        Assert.Throws<ArgumentException>(() => Deposit(externalId: new string('x', Transaction.MaxExternalIdLength + 1)));
        Assert.Throws<ArgumentException>(() => Deposit(note: new string('x', Transaction.MaxNoteLength + 1)));
        // A price in dollars with the cash in shekels: the two must be one currency.
        Assert.Throws<ArgumentException>(() => new Transaction(
            TransactionId.New(), Ibkr, TransactionKind.Buy, Traded, Settled, Money.Ils(-1203.50m),
            TransactionSource.Flex, Recorded, Voo, 2m, new Price(601.75m, Currency.Usd)));
        Assert.Equal(100.123456m, Deposit(cash: 100.123456m).Cash.Amount);
        Assert.Null(Deposit(externalId: "  ", note: " ").ExternalId);
    }

    [Fact]
    public void ReversalCarriesTheOppositeEffectsAndNamesWhatItCancels()
    {
        var buy = Entry(TransactionKind.Buy, -1203.50m, 2m, instrument: true, price: 601.75m, externalId: "T-1");

        var reversal = buy.Reversal(TransactionId.New(), Recorded.Plus(Duration.FromHours(1)), "entered twice");

        Assert.True(reversal.IsReversal);
        Assert.Equal(buy.Id, reversal.Reverses);
        Assert.Equal((TransactionKind.Buy, 1203.50m, -2m), (reversal.Kind, reversal.Cash.Amount, reversal.QuantityChange));
        Assert.Equal((buy.HoldingPlace, buy.Instrument, buy.TradeDate, buy.SettlementDate, buy.Price),
            (reversal.HoldingPlace, reversal.Instrument, reversal.TradeDate, reversal.SettlementDate, reversal.Price));
        Assert.Equal((TransactionSource.Manual, "entered twice", null), (reversal.Source, reversal.Note, reversal.ExternalId));
        Assert.Equal(0m, buy.Cash.Amount + reversal.Cash.Amount);
        Assert.Equal(0m, buy.QuantityChange + reversal.QuantityChange);
    }

    [Fact]
    public void ReversalIsNotReversedAndNeedsAReason()
    {
        var deposit = Entry(TransactionKind.Deposit, 1000m);
        var reversal = deposit.Reversal(TransactionId.New(), Recorded, "wrong amount");

        Assert.Throws<InvalidOperationException>(() => reversal.Reversal(TransactionId.New(), Recorded, "again"));
        Assert.Throws<ArgumentException>(() => deposit.Reversal(TransactionId.New(), Recorded, " "));
        // A reversal with the effects of an ordinary entry is not a reversal.
        Assert.Throws<ArgumentException>(() => Entry(TransactionKind.Deposit, 1000m, reverses: deposit.Id));
    }

    [Fact]
    public void AnyEntryAndItsReversalAddUpToNothing()
    {
        var kinds = Gen.OneOfConst(TransactionKind.Deposit, TransactionKind.Withdrawal, TransactionKind.Fee, TransactionKind.Interest);
        Gen.Select(kinds, Gen.Decimal[0.000001m, 1_000_000m]).Sample((kind, amount) =>
        {
            var magnitude = decimal.Round(amount, Transaction.Scale);
            if (magnitude == 0m)
            {
                return true;
            }

            var cash = kind is TransactionKind.Deposit or TransactionKind.Interest ? magnitude : -magnitude;
            var entry = Entry(kind, cash);
            var reversal = entry.Reversal(TransactionId.New(), Recorded, "test");
            return entry.Cash + reversal.Cash == Money.Zero(Currency.Usd)
                && entry.QuantityChange + reversal.QuantityChange == 0m;
        });
    }

    [Fact]
    public void SameFactsIgnoreWhatThePlatformAddsAndNoticeEveryRealDifference()
    {
        var first = Entry(TransactionKind.Buy, -1203.50m, 2m, instrument: true, price: 601.75m, externalId: "T-1");
        var again = new Transaction(
            TransactionId.New(), Ibkr, TransactionKind.Buy, Traded, Settled, Money.Usd(-1203.5000m), TransactionSource.Flex,
            Recorded.Plus(Duration.FromDays(3)), Voo, 2.0m, new Price(601.750m, Currency.Usd), "T-1",
            relatedTo: TransactionId.New(), note: "imported again");

        Assert.True(first.SameFactsAs(again));
        Assert.False(first.SameFactsAs(Entry(TransactionKind.Buy, -1203.51m, 2m, instrument: true, price: 601.75m, externalId: "T-1")));
        Assert.False(first.SameFactsAs(Entry(TransactionKind.Buy, -1203.50m, 2m, instrument: true, price: 601.75m, externalId: "T-2")));
        Assert.False(first.SameFactsAs(first.Reversal(TransactionId.New(), Recorded, "cancelled", TransactionSource.Flex, "T-1")));
    }
}
