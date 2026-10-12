using CsCheck;
using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Domain;

/// <summary>
/// P3.4.01: the value of the portfolio on a date — a hand-worked example by holding place and in all, in
/// dollars and in shekels, and the rules that keep a figure honest: no price, no value; a price from before a
/// split does not value the new shares; what is provisional is said.
/// </summary>
public sealed class PortfolioValueTests
{
    private const string Boi = "BOI representative";

    private static readonly HoldingPlaceId Ibkr = HoldingPlaceId.New();
    private static readonly HoldingPlaceId Elsewhere = HoldingPlaceId.New();
    private static readonly InstrumentId Vt = InstrumentId.New();
    private static readonly InstrumentId Spy = InstrumentId.New();
    private static readonly InstrumentId Xlk = InstrumentId.New();
    private static readonly LocalDate Friday = new(2026, 10, 9);
    private static readonly Instant Recorded = Instant.FromUtc(2026, 10, 12, 6, 0);

    private static string NameOf(InstrumentId instrument) =>
        instrument == Vt ? "VT" : instrument == Spy ? "SPY" : instrument == Xlk ? "XLK" : "?";

    private static Transaction Entry(
        TransactionKind kind, LocalDate traded, decimal cash, decimal quantity = 0m, InstrumentId? instrument = null,
        Currency? currency = null, TransactionId? relatedTo = null, HoldingPlaceId? place = null)
    {
        var money = new Money(cash, currency ?? Currency.Usd);
        var price = kind == TransactionKind.Buy ? new Price(Math.Round(-cash / quantity, Transaction.Scale), money.Currency) : (Price?)null;
        return new Transaction(
            TransactionId.New(), place ?? Ibkr, kind, traded, traded, money, TransactionSource.Manual, Recorded,
            instrument, quantity, price, relatedTo: relatedTo);
    }

    private static Transaction Buy(InstrumentId instrument, decimal shares, decimal price, LocalDate traded, HoldingPlaceId? place = null) =>
        Entry(TransactionKind.Buy, traded, -shares * price, shares, instrument, place: place);

    private static Transaction FeeOf(Transaction trade, decimal amount) =>
        Entry(TransactionKind.Fee, trade.TradeDate, -amount, instrument: trade.Instrument, relatedTo: trade.Id, place: trade.HoldingPlace);

    private static ClosingPrice Close(InstrumentId instrument, LocalDate session, decimal close) =>
        new(instrument, session, new Price(close, Currency.Usd), "yfinance");

    private static PriceOnDate Of(InstrumentId instrument, LocalDate session, decimal close) =>
        PriceOnDate.OfTheSession(Close(instrument, session, close));

    private static AppliedFxRate Rate(decimal rate, LocalDate published, LocalDate? on = null, bool provisional = false) =>
        new(new FxRate(Currency.Usd, Currency.Ils, rate, published, Boi), on ?? published, provisional);

    /// <summary>
    /// At IBKR: 3,000 USD and 150 ILS paid in; 4 VT at 120.13 (commission 1.00) and 2 SPY at 769.64 (commission
    /// 0.35). Elsewhere: 1,000 USD paid in and 1 SPY at 770.00.
    /// </summary>
    private static Transaction[] Ledger()
    {
        var day = new LocalDate(2026, 10, 1);
        var vt = Buy(Vt, 4m, 120.13m, day);
        var spy = Buy(Spy, 2m, 769.64m, day);
        return
        [
            Entry(TransactionKind.Deposit, day, 3000m),
            Entry(TransactionKind.Deposit, day, 150m, currency: Currency.Ils),
            vt, FeeOf(vt, 1.00m),
            spy, FeeOf(spy, 0.35m),
            Entry(TransactionKind.Deposit, day, 1000m, place: Elsewhere),
            Buy(Spy, 1m, 770.00m, day, Elsewhere),
        ];
    }

    private static PortfolioValue Value(
        LocalDate asOf, AppliedFxRate? rate, string? whyNoRate, params PriceOnDate[] prices) =>
        PortfolioValue.Of(PortfolioBooks.From(Ledger(), asOf), prices.ToDictionary(p => p.Instrument), rate, whyNoRate);

    /// <summary>Friday 9 October 2026: VT closed at 121.50, SPY at 778.57, a dollar was 3.055 shekels.</summary>
    private static PortfolioValue OnFriday() =>
        Value(Friday, Rate(3.055m, Friday), null, Of(Vt, Friday, 121.50m), Of(Spy, Friday, 778.57m));

    [Fact]
    public void HandWorkedExampleByPlaceAndInAllInDollarsAndInShekels()
    {
        var value = OnFriday();

        // IBKR in dollars: 4 x 121.50 = 486.00; 2 x 778.57 = 1,557.14; cash 3,000 - 480.52 - 1.00 - 1,539.28 - 0.35
        // = 978.85; together 3,021.99. And 150 shekels of cash.
        Assert.Equal([Money.Usd(486.00m), Money.Usd(1557.14m), Money.Usd(778.57m)], value.Positions.Select(p => p.Value!.Value));
        var ibkrInShekels = value.In(Currency.Ils, Ibkr);
        Assert.Equal(Money.Ils(9382.17945m), ibkrInShekels.Total); // 3,021.99 x 3.055 = 9,232.17945, + 150
        Assert.Equal(Money.Usd(3021.99m + (150m / 3.055m)), value.In(Currency.Usd, Ibkr).Total);
        Assert.Equal(3071.089836m, decimal.Round(value.In(Currency.Usd, Ibkr).Total!.Value.Amount, 6)); // 150 / 3.055 = 49.099836...

        // Elsewhere: 778.57 + cash 230.00 = 1,008.57 dollars = 3,081.18135 shekels.
        Assert.Equal(Money.Usd(1008.57m), value.In(Currency.Usd, Elsewhere).Total);
        Assert.Equal(Money.Ils(3081.18135m), value.In(Currency.Ils, Elsewhere).Total);

        // In all: 4,030.56 dollars and 150 shekels = 12,463.3608 shekels - the two places added up, to the last digit.
        var inShekels = value.In(Currency.Ils);
        Assert.Equal(Money.Ils(12463.3608m), inShekels.Total);
        Assert.Equal(ibkrInShekels.Total!.Value + value.In(Currency.Ils, Elsewhere).Total!.Value, inShekels.Total);
        Assert.Equal(Money.Usd(4030.56m + (150m / 3.055m)), value.In(Currency.Usd).Total);
        Assert.Equal(4079.659836m, decimal.Round(value.In(Currency.Usd).Total!.Value.Amount, 6));

        Assert.Equal([Ibkr, Elsewhere], value.Places);
        Assert.Equal((true, false, 3.055m, Friday), (inShekels.IsComplete, inShekels.IsProvisional, inShekels.Rate!.Rate.Rate, inShekels.Rate.Rate.Date));
        Assert.Null(value.In(Currency.Usd, Elsewhere).Rate); // nothing there was converted: no rate is in that figure
        Assert.Empty(value.Remarks(NameOf));
        // Each amount keeps what it was computed with.
        Assert.All(value.Positions, p => Assert.Equal((PositionValueState.Valued, PriceAvailability.OfTheSession, Friday), (p.State, p.Price.Availability, p.Price.Price!.Session)));
    }

    [Fact]
    public void PositionWithoutAPriceIsNotValuedAndTheRestIsNotCalledTheTotal()
    {
        var value = Value(Friday, Rate(3.055m, Friday), null, Of(Vt, Friday, 121.50m), PriceOnDate.Missing(Spy, Friday));

        var total = value.In(Currency.Usd, Ibkr);

        Assert.Null(total.Total);
        Assert.False(total.IsComplete);
        Assert.Equal(Money.Usd(486.00m + 978.85m + (150m / 3.055m)), total.Valued); // VT and the cash, and not a cent for SPY
        var spy = Assert.Single(total.NotValued);
        Assert.Equal((Spy, PositionValueState.PriceMissing, null, new Quantity(2m)), (spy.Position.Instrument, spy.State, spy.Value, spy.Position.Quantity));
        Assert.Equal(2, value.In(Currency.Ils).NotValued.Count); // SPY is held at both places
        // Said once, though two positions are concerned.
        Assert.Equal(["SPY: not valued - there is no closing price for the session 2026-10-09 goes by."], value.Remarks(NameOf));

        var unavailable = Value(Friday, Rate(3.055m, Friday), null, PriceOnDate.Unavailable(Vt, Friday), Of(Spy, Friday, 778.57m));
        Assert.Equal(PositionValueState.PriceUnavailable, Assert.Single(unavailable.In(Currency.Usd).NotValued).State);
        Assert.Equal(["VT: not valued - no closing price can be used on 2026-10-09."], unavailable.Remarks(NameOf));
        Assert.True(unavailable.In(Currency.Usd, Elsewhere).IsComplete); // VT is not held there
    }

    [Fact]
    public void PriceFromBeforeASplitDoesNotValueTheSharesAfterIt()
    {
        // 2 XLK bought on Wednesday 3 December 2025 at 289.99; on Friday the 5th the fund splits 2:1 - 2 more shares.
        var (wednesday, thursday, friday, sunday) = (new LocalDate(2025, 12, 3), new LocalDate(2025, 12, 4), new LocalDate(2025, 12, 5), new LocalDate(2025, 12, 7));
        Transaction[] ledger =
        [
            Buy(Xlk, 2m, 289.99m, wednesday),
            Buy(Spy, 1m, 680m, wednesday),
            Entry(TransactionKind.CorporateAction, friday, 0m, 2m, Xlk),
        ];
        PortfolioValue On(LocalDate asOf, PriceOnDate xlk) => PortfolioValue.Of(
            PortfolioBooks.From(ledger, asOf),
            new Dictionary<InstrumentId, PriceOnDate>
            {
                [Xlk] = xlk,
                // SPY has no close after Thursday's either - and no split: its Thursday close still values it.
                [Spy] = asOf == thursday ? Of(Spy, thursday, 685m) : PriceOnDate.Carried(Close(Spy, thursday, 685m), asOf, isProvisional: true),
            },
            null, "no rate in this example");
        PositionValue XlkOn(LocalDate asOf, PriceOnDate xlk) => On(asOf, xlk).Positions.Single(p => p.Position.Instrument == Xlk);

        // The day before the split: 2 shares at that day's close.
        Assert.Equal(Money.Usd(582.14m), XlkOn(thursday, Of(Xlk, thursday, 291.07m)).Value);
        // The split day: 4 shares at the new price - the close of that day is already the split one.
        Assert.Equal(Money.Usd(586.40m), XlkOn(friday, Of(Xlk, friday, 146.60m)).Value);
        // The weekend after: Friday's close carried, 4 shares.
        Assert.Equal(Money.Usd(586.40m), XlkOn(sunday, PriceOnDate.Carried(Close(Xlk, friday, 146.60m), sunday, false)).Value);

        // The prices were not loaded since Thursday: its close is of the shares before the split. 4 x 291.07
        // would show twice what is there, so the position has no value - whoever looks is told why.
        var stale = On(sunday, PriceOnDate.Carried(Close(Xlk, thursday, 291.07m), sunday, isProvisional: true));
        var xlk = stale.Positions.Single(p => p.Position.Instrument == Xlk);
        Assert.Equal((PositionValueState.PriceFromBeforeASplit, null, friday, new Quantity(4m)), (xlk.State, xlk.Value, xlk.SplitOn, xlk.Position.Quantity));
        var total = stale.In(Currency.Usd);
        Assert.Equal((null, Money.Usd(-1259.98m + 685m), true), (total.Total, total.Valued, total.IsProvisional)); // the cash paid out, and SPY
        Assert.Equal(
            [
                "XLK: not valued - the latest close is of 2025-12-04, before the split of 2025-12-05.",
                "SPY: provisional - valued at the close of 2025-12-04, 3 day(s) before; the exchange may have been open since.",
                "No exchange rate, so nothing is converted: no rate in this example",
            ],
            stale.Remarks(NameOf));
    }

    [Fact]
    public void SplitIsTheFundsWhereverItWasRecorded()
    {
        // XLK at two places. The split of Friday the 5th is in the books of one of them; the other's statement
        // dates it Monday the 8th. A close of Thursday values the shares of neither - and the reason names the
        // earliest split, once.
        var (wednesday, thursday, friday, monday) = (new LocalDate(2025, 12, 3), new LocalDate(2025, 12, 4), new LocalDate(2025, 12, 5), new LocalDate(2025, 12, 8));
        Transaction[] ledger =
        [
            Buy(Xlk, 2m, 289.99m, wednesday),
            Entry(TransactionKind.CorporateAction, monday, 0m, 2m, Xlk),
            Buy(Xlk, 1m, 289.99m, wednesday, Elsewhere),
            Entry(TransactionKind.CorporateAction, friday, 0m, 1m, Xlk, place: Elsewhere),
        ];
        PortfolioValue On(LocalDate asOf, LocalDate session, decimal close) => PortfolioValue.Of(
            PortfolioBooks.From(ledger, asOf),
            new Dictionary<InstrumentId, PriceOnDate> { [Xlk] = PriceOnDate.Carried(Close(Xlk, session, close), asOf, isProvisional: true) },
            null, "no rate in this example");

        var tuesday = On(monday.PlusDays(1), thursday, 291.07m);

        Assert.All(tuesday.Positions, p => Assert.Equal((PositionValueState.PriceFromBeforeASplit, friday), (p.State, p.SplitOn)));
        Assert.Equal("XLK: not valued - the latest close is of 2025-12-04, before the split of 2025-12-05.", tuesday.Remarks(NameOf)[0]);
        Assert.Equal(2, tuesday.Remarks(NameOf).Count);

        // On Saturday the books of the first place hold no split yet; the other place's is enough to say that
        // Thursday's close is of the old shares.
        Assert.All(On(friday.PlusDays(1), thursday, 291.07m).Positions, p => Assert.Equal(PositionValueState.PriceFromBeforeASplit, p.State));
        // With Monday's close every share is a new one.
        Assert.Equal([Money.Usd(4m * 147.63m), Money.Usd(2m * 147.63m)], On(monday.PlusDays(1), monday, 147.63m).Positions.Select(p => p.Value!.Value));
    }

    [Fact]
    public void ProvisionalPriceAndProvisionalRateAreInTheFigureAndSaidInWords()
    {
        // Monday 12 October, before anything of that day is known: Friday's closes and Friday's rate.
        var monday = new LocalDate(2026, 10, 12);
        var value = Value(
            monday, Rate(3.055m, Friday, monday, provisional: true), null,
            PriceOnDate.Carried(Close(Vt, Friday, 121.50m), monday, isProvisional: true),
            PriceOnDate.Carried(Close(Spy, Friday, 778.57m), monday, isProvisional: true));

        Assert.Equal((Money.Ils(12463.3608m), true), (value.In(Currency.Ils).Total, value.In(Currency.Ils).IsProvisional));
        Assert.Equal(
            [
                "VT: provisional - valued at the close of 2026-10-09, 3 day(s) before; the exchange may have been open since.",
                "SPY: provisional - valued at the close of 2026-10-09, 3 day(s) before; the exchange may have been open since.",
                "The exchange rate is provisional: 3.055 of 2026-10-09 (BOI representative), 3 day(s) before; a later one may still be published.",
            ],
            value.Remarks(NameOf));

        // Sunday the 11th, looked at later: the exchange and the bank were closed, and that is all there is to it.
        var sunday = new LocalDate(2026, 10, 11);
        var settled = Value(
            sunday, Rate(3.055m, Friday, sunday), null,
            PriceOnDate.Carried(Close(Vt, Friday, 121.50m), sunday, false), PriceOnDate.Carried(Close(Spy, Friday, 778.57m), sunday, false));
        Assert.Equal((Money.Ils(12463.3608m), false, 2), (settled.In(Currency.Ils).Total, settled.In(Currency.Ils).IsProvisional, settled.In(Currency.Ils).Rate!.DaysCarried));
        Assert.Empty(settled.Remarks(NameOf));

        // A provisional rate makes only the converted figure provisional.
        var rateOnly = Value(
            monday, Rate(3.055m, Friday, monday, provisional: true), null,
            PriceOnDate.Carried(Close(Vt, Friday, 121.50m), monday, false), PriceOnDate.Carried(Close(Spy, Friday, 778.57m), monday, false));
        Assert.Equal((true, false), (rateOnly.In(Currency.Ils).IsProvisional, rateOnly.In(Currency.Usd, Elsewhere).IsProvisional));
    }

    [Fact]
    public void WithoutARateNothingIsConvertedAndThatIsSaid()
    {
        var value = Value(Friday, null, "The rates were not updated.", Of(Vt, Friday, 121.50m), Of(Spy, Friday, 778.57m));

        var inDollars = value.In(Currency.Usd);
        var inShekels = value.In(Currency.Ils);

        Assert.Equal((null, Money.Usd(4030.56m), false), (inDollars.Total, inDollars.Valued, inDollars.IsComplete));
        Assert.Equal([Money.Ils(150m)], inDollars.NotConverted);
        Assert.Equal((null, Money.Ils(150m)), (inShekels.Total, inShekels.Valued));
        Assert.Equal([Money.Usd(4030.56m)], inShekels.NotConverted);
        Assert.Empty(inDollars.NotValued); // every position has its value in dollars; it is the shekels that are left out
        Assert.Equal(["No exchange rate, so nothing is converted: The rates were not updated."], value.Remarks(NameOf));
        // A place that holds dollars only has its dollar total without any rate.
        Assert.Equal(Money.Usd(1008.57m), value.In(Currency.Usd, Elsewhere).Total);
    }

    [Fact]
    public void NothingHeldIsWorthNothingAndCashAloneIsItsOwnValue()
    {
        var empty = PortfolioValue.Of(PortfolioBooks.From([], Friday), new Dictionary<InstrumentId, PriceOnDate>(), Rate(3.055m, Friday), null);
        Assert.Equal((Money.Ils(0m), true), (empty.In(Currency.Ils).Total, empty.In(Currency.Ils).IsComplete));
        Assert.Empty(empty.Places);

        var cash = PortfolioValue.Of(
            PortfolioBooks.From([Entry(TransactionKind.Deposit, Friday, 1000m)], Friday), new Dictionary<InstrumentId, PriceOnDate>(), Rate(3.055m, Friday), null);
        Assert.Equal((Money.Usd(1000m), Money.Ils(3055m)), (cash.In(Currency.Usd).Total, cash.In(Currency.Ils).Total));
        Assert.Equal(Money.Usd(0m), cash.In(Currency.Usd, Elsewhere).Total);

        // Shekels that came and went leave nothing to convert: without a rate the dollars are still the total.
        Transaction[] ledger =
        [
            Entry(TransactionKind.Deposit, Friday, 1000m),
            Entry(TransactionKind.Deposit, Friday, 150m, currency: Currency.Ils),
            Entry(TransactionKind.Withdrawal, Friday, -150m, currency: Currency.Ils),
        ];
        var noRate = PortfolioValue.Of(PortfolioBooks.From(ledger, Friday), new Dictionary<InstrumentId, PriceOnDate>(), null, "none stored");
        Assert.Equal((Money.Usd(1000m), true), (noRate.In(Currency.Usd).Total, noRate.In(Currency.Usd).IsComplete));
        Assert.Equal((null, Money.Ils(0m)), (noRate.In(Currency.Ils).Total, noRate.In(Currency.Ils).Valued));
    }

    [Fact]
    public void WhatDoesNotBelongTogetherIsRefused()
    {
        var books = PortfolioBooks.From(Ledger(), Friday);
        var prices = new Dictionary<InstrumentId, PriceOnDate> { [Vt] = Of(Vt, Friday, 121.50m), [Spy] = Of(Spy, Friday, 778.57m) };
        Dictionary<InstrumentId, PriceOnDate> With(InstrumentId instrument, PriceOnDate price) => new(prices) { [instrument] = price };

        // A price nobody asked for, of another day, of another instrument, in another currency.
        Assert.Throws<ArgumentException>(() => PortfolioValue.Of(books, new Dictionary<InstrumentId, PriceOnDate> { [Vt] = prices[Vt] }, Rate(3.055m, Friday), null));
        Assert.Throws<ArgumentException>(() => PortfolioValue.Of(books, With(Spy, Of(Spy, Friday.PlusDays(-1), 778.57m)), Rate(3.055m, Friday), null));
        Assert.Throws<ArgumentException>(() => PortfolioValue.Of(books, With(Spy, Of(Vt, Friday, 121.50m)), Rate(3.055m, Friday), null));
        var inShekels = PriceOnDate.OfTheSession(new ClosingPrice(Spy, Friday, new Price(2378.5m, Currency.Ils), "yfinance"));
        Assert.Throws<InvalidOperationException>(() => PortfolioValue.Of(books, With(Spy, inShekels), Rate(3.055m, Friday), null));

        // A rate of another day; a rate together with a reason for having none; neither.
        Assert.Throws<ArgumentException>(() => PortfolioValue.Of(books, prices, Rate(3.055m, Friday.PlusDays(-1)), null));
        Assert.Throws<ArgumentException>(() => PortfolioValue.Of(books, prices, Rate(3.055m, Friday), "there is none"));
        Assert.Throws<ArgumentException>(() => PortfolioValue.Of(books, prices, null, " "));
    }

    [Fact]
    public void PlacesAddUpToTheWholeWhateverIsHeld()
    {
        var gen =
            from vt in Gen.Decimal[0.000001m, 500m]
            from spy in Gen.Decimal[0.000001m, 500m]
            from elsewhere in Gen.Decimal[0.000001m, 500m]
            from vtClose in Gen.Int[1, 2_000_000]
            from spyClose in Gen.Int[1, 9_000_000]
            from shekels in Gen.Int[-1_000_000, 1_000_000]
            from rate in Gen.Int[2_500_000, 4_500_000]
            select (Vt: decimal.Round(vt, 6), Spy: decimal.Round(spy, 6), Elsewhere: decimal.Round(elsewhere, 6),
                VtClose: vtClose / 10_000m, SpyClose: spyClose / 10_000m, Shekels: shekels / 100m, Rate: rate / 1_000_000m);

        gen.Where(x => x.Vt > 0m && x.Spy > 0m && x.Elsewhere > 0m).Sample(x =>
        {
            Transaction[] ledger =
            [
                Entry(TransactionKind.Deposit, Friday, 2_000_000m),
                Entry(x.Shekels >= 0m ? TransactionKind.Deposit : TransactionKind.Fee, Friday, x.Shekels == 0m ? 1m : x.Shekels, currency: Currency.Ils),
                Entry(TransactionKind.Buy, Friday, -1000m, x.Vt, Vt),
                Entry(TransactionKind.Buy, Friday, -1000m, x.Spy, Spy),
                Entry(TransactionKind.Buy, Friday, -1000m, x.Elsewhere, Spy, place: Elsewhere),
            ];
            var value = PortfolioValue.Of(
                PortfolioBooks.From(ledger, Friday),
                new Dictionary<InstrumentId, PriceOnDate> { [Vt] = Of(Vt, Friday, x.VtClose), [Spy] = Of(Spy, Friday, x.SpyClose) },
                Rate(x.Rate, Friday), null);
            var shekels = x.Shekels == 0m ? 1m : x.Shekels;

            // Worked out another way: every dollar amount on its own, then the rate.
            var dollars = 2_000_000m - 3000m + (x.Vt * x.VtClose) + (x.Spy * x.SpyClose) + (x.Elsewhere * x.SpyClose);
            return value.In(Currency.Ils).Total == Money.Ils((dollars * x.Rate) + shekels)
                && value.In(Currency.Ils, Ibkr).Valued + value.In(Currency.Ils, Elsewhere).Valued == value.In(Currency.Ils).Valued
                && value.In(Currency.Usd, Ibkr).Valued.Amount + value.In(Currency.Usd, Elsewhere).Valued.Amount - value.In(Currency.Usd).Valued.Amount is > -0.000000000001m and < 0.000000000001m;
        });
    }
}
