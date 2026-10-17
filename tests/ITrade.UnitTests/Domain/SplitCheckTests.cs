using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Domain;

/// <summary>
/// P3.4.03: the split days the prices know (`fund_events`) against the splits the ledger applied. The first case
/// is the card's: 2 shares held, the fund splits 2:1, the statement that brings the split is a day behind - the
/// valuation would show 2 × 146.60 = 293.20 as the whole value instead of 586.40, and only this check can tell.
/// </summary>
public sealed class SplitCheckTests
{
    private static readonly HoldingPlaceId Ibkr = HoldingPlaceId.New();
    private static readonly HoldingPlaceId Other = HoldingPlaceId.New();
    private static readonly InstrumentId Voo = InstrumentId.New();
    private static readonly LocalDate Bought = new(2026, 11, 2);
    private static readonly LocalDate SplitDay = new(2026, 11, 16);
    private static readonly LocalDate Today = new(2026, 11, 20);
    private static readonly Instant Recorded = Instant.FromUtc(2026, 11, 20, 6, 0);

    private static Transaction Buy(decimal shares, decimal price, LocalDate day, HoldingPlaceId? place = null) =>
        new(TransactionId.New(), place ?? Ibkr, TransactionKind.Buy, day, day, Money.Usd(-shares * price),
            TransactionSource.Manual, Recorded, Voo, shares, new Price(price, Currency.Usd));

    private static Transaction Split(decimal change, LocalDate day, HoldingPlaceId? place = null) =>
        new(TransactionId.New(), place ?? Ibkr, TransactionKind.CorporateAction, day, day, Money.Usd(0m),
            TransactionSource.Manual, Recorded, Voo, change);

    private static FundEvent Event(FundEventKind kind, decimal value, LocalDate? day = null) =>
        new(Voo, day ?? SplitDay, kind, value, "yfinance");

    private static IReadOnlyList<string> Check(
        IReadOnlyList<Transaction> ledger, IReadOnlyList<FundEvent> events, LocalDate? pricesThrough = null, LocalDate? through = null) =>
        SplitCheck.Of(
            ledger, Ibkr, events, new Dictionary<InstrumentId, LocalDate> { [Voo] = pricesThrough ?? Today }, through ?? Today, _ => "VOO");

    [Fact]
    public void ASplitThePricesShowAndTheLedgerLacksIsSaidWithTheSharesHeld()
    {
        var said = Check([Buy(2m, 293.20m, Bought)], [Event(FundEventKind.Split, 2m)]);

        Assert.Equal(
            "VOO: the prices show a split of 2 for 1 on 2026-11-16, and the ledger holds 2 shares the evening before and no split that day: from then on the shares are valued at the new price as if they were the old number. The broker's statement brings the split - import it; if the statement shows none, the data may be wrong (a spin-off can be reported as a split).",
            Assert.Single(said));
    }

    [Fact]
    public void ASplitInBothAgrees()
    {
        Assert.Empty(Check([Buy(2m, 293.20m, Bought), Split(2m, SplitDay)], [Event(FundEventKind.Split, 2m)]));
        Assert.Empty(Check([Buy(8m, 50m, Bought), Split(-6m, SplitDay)], [Event(FundEventKind.Split, 0.25m)])); // 1:4 reverse
    }

    [Fact]
    public void ARatioStoredToSixPlacesAgreesWithTheSharesItGives()
    {
        // 1 for 3 is stored as 0.333333: 9 shares become 3, not 3.000003.
        Assert.Empty(Check([Buy(9m, 30m, Bought), Split(-6m, SplitDay)], [Event(FundEventKind.Split, 0.333333m)]));
        Assert.Single(Check([Buy(9m, 30m, Bought), Split(-5m, SplitDay)], [Event(FundEventKind.Split, 0.333333m)]));
        // Half a share becomes 0.166667 in the ledger's six places: a change of -0.333333, not -0.3333335.
        Assert.Empty(Check([Buy(0.5m, 30m, Bought), Split(-0.333333m, SplitDay)], [Event(FundEventKind.Split, 0.333333m)]));
    }

    [Fact]
    public void ASplitAtAnotherPlaceIsNotThisPlacesSplit()
    {
        var said = Check([Buy(2m, 293.20m, Bought), Buy(1m, 293.20m, Bought, Other), Split(1m, SplitDay, Other)], [Event(FundEventKind.Split, 2m)]);

        Assert.StartsWith("VOO: the prices show a split of 2 for 1 on 2026-11-16, and the ledger holds 2 shares", Assert.Single(said), StringComparison.Ordinal);
    }

    [Fact]
    public void ASplitOfAnotherRatioIsSaid()
    {
        var said = Check([Buy(3m, 100m, Bought), Split(3m, SplitDay)], [Event(FundEventKind.Split, 1.5m)]);

        Assert.Equal(
            "VOO: the ledger's split of 2026-11-16 changed 3 shares by 3, the prices' 1.5 for 1 would change them by 1.5: a fraction paid in cash, or another ratio - look at the broker's statement.",
            Assert.Single(said));
    }

    [Fact]
    public void ASplitThatWasReversedIsNoSplit()
    {
        var split = Split(2m, SplitDay);

        var said = Check([Buy(2m, 293.20m, Bought), split, split.Reversal(TransactionId.New(), Recorded, "a test")], [Event(FundEventKind.Split, 2m)]);

        Assert.StartsWith("VOO: the prices show a split of 2 for 1 on 2026-11-16", Assert.Single(said), StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsNotHeldOnTheEveningBeforeHasNothingToSplit()
    {
        // Bought on the split day: bought as the new shares. A dividend is no split. A split after the day asked is not looked at.
        Assert.Empty(Check([Buy(4m, 146.60m, SplitDay)], [Event(FundEventKind.Split, 2m)]));
        Assert.Empty(Check([Buy(2m, 293.20m, Bought)], [Event(FundEventKind.Dividend, 1.5m)]));
        Assert.Empty(Check([Buy(2m, 293.20m, Bought)], [Event(FundEventKind.Split, 2m)], through: SplitDay.PlusDays(-1)));
        // Held at another place: the books checked are those of one place.
        Assert.Empty(Check([Buy(2m, 293.20m, Bought, Other)], [Event(FundEventKind.Split, 2m)]));
    }

    [Fact]
    public void ASplitTheLedgerHoldsAndThePricesDoNotShowIsSaidOnlyWhenThePricesReachItsDay()
    {
        Transaction[] ledger = [Buy(2m, 293.20m, Bought), Split(2m, SplitDay)];

        var reached = Check(ledger, [], pricesThrough: Today);
        var onTheDay = Check(ledger, [], pricesThrough: SplitDay);
        var notYet = Check(ledger, [], pricesThrough: SplitDay.PlusDays(-1));

        Assert.Single(onTheDay);

        Assert.Equal(
            "VOO: the ledger holds a split on 2026-11-16 (2 shares) that the prices, stored up to 2026-11-20, do not show: closes before that day are of the old shares. Look at the broker's statement and at the data before trusting a value of that fund.",
            Assert.Single(reached));
        Assert.Empty(notYet);
    }
}
