using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Domain;

/// <summary>
/// P3.4.04: the portfolio that puts the owner's money, on the owner's days, into one index fund — hand-worked
/// examples with a dividend, a split and a withdrawal, and what it refuses.
/// </summary>
public sealed class IndexPortfolioTests
{
    private static readonly InstrumentId Fund = InstrumentId.New();

    private static LocalDate Day(int year, int month, int day) => new(year, month, day);

    private static ValuePoint At(LocalDate date, decimal value, decimal inflow = 0m, decimal outflow = 0m) => new(date, value, inflow, outflow);

    private static FundEvent Dividend(LocalDate day, decimal perShare) => new(Fund, day, FundEventKind.Dividend, perShare, "yfinance");

    private static FundEvent Split(LocalDate day, decimal ratio) => new(Fund, day, FundEventKind.Split, ratio, "yfinance");

    private static Dictionary<LocalDate, decimal> Closes(params (LocalDate Day, decimal Close)[] closes) => closes.ToDictionary(c => c.Day, c => c.Close);

    /// <summary>
    /// The owner pays in 1,000 on 1 January and 1,000 on 1 April; what the real portfolio was worth is of no
    /// matter here - only the days and the money.
    /// </summary>
    private static readonly ValuePoint[] Owner =
    [
        At(Day(2025, 12, 31), 0m),
        At(Day(2026, 1, 1), 990m, inflow: 1000m),
        At(Day(2026, 3, 31), 1010m),
        At(Day(2026, 4, 1), 2015m, inflow: 1000m),
        At(Day(2026, 6, 30), 2100m),
    ];

    [Fact]
    public void SameMoneyOnTheSameDaysBuysTheFundAtTheCloseAndItsDividendsBuyMore()
    {
        // The fund closes at 100 on 1 January. On 20 March, at 100 again, it pays 1.00 a share, of which 0.75
        // are kept. It closes at 125 at the end of March and on 1 April, and at 130 at the end of June.
        var closes = Closes(
            (Day(2025, 12, 31), 99m), (Day(2026, 1, 1), 100m), (Day(2026, 3, 20), 100m), (Day(2026, 3, 31), 125m), (Day(2026, 4, 1), 125m),
            (Day(2026, 6, 30), 130m));

        var index = IndexPortfolio.Following(Owner, [Dividend(Day(2026, 3, 20), 1.00m)], closes, 0.75m);

        // 10 shares; the dividend buys 7.50 / 100 = 0.075 more; 1,000 at 125 buy 8: 18.075 shares.
        Assert.Equal(
            [
                At(Day(2025, 12, 31), 0m),
                At(Day(2026, 1, 1), 1000m, inflow: 1000m),
                At(Day(2026, 3, 31), 1259.375m),
                At(Day(2026, 4, 1), 2259.375m, inflow: 1000m),
                At(Day(2026, 6, 30), 2349.75m),
            ],
            index);
        // The fund went from 100 to 130 and paid 0.75% that was put back: 1.30 x 1.0075.
        var returns = PeriodReturns.Of(index);
        Assert.Equal((0.30975m, 349.75m, 2000m), (returns.TimeWeighted, returns.Gain, returns.Contributed));

        // With nothing withheld the whole 1.00 buys shares; with no dividend there are 18.
        Assert.Equal(18.1m * 130m, IndexPortfolio.Following(Owner, [Dividend(Day(2026, 3, 20), 1.00m)], closes, 1m)[^1].Value);
        Assert.Equal(18m * 130m, IndexPortfolio.Following(Owner, [], closes, 0.75m)[^1].Value);
    }

    [Fact]
    public void WhatWasThereAtTheOpeningBuysTheFundAndASplitChangesSharesNotValue()
    {
        // 3,000 are there when the period opens, with the fund at 300: 10 shares. On 5 December it splits 2:1
        // and closes at 151; that day it also pays 0.50 on each new share. A reverse split 1:4 follows in March.
        ValuePoint[] owner = [At(Day(2025, 11, 28), 3000m), At(Day(2025, 12, 5), 3100m), At(Day(2026, 3, 31), 3300m)];
        var closes = Closes((Day(2025, 11, 28), 300m), (Day(2025, 12, 5), 151m), (Day(2026, 3, 31), 640m));

        var split = IndexPortfolio.Following(owner, [Split(Day(2025, 12, 5), 2m)], closes, 0.75m);
        Assert.Equal([3000m, 20m * 151m, 20m * 640m], split.Select(p => p.Value));

        // The dividend of the split day is paid on the 20 new shares: 20 x 0.50 x 0.75 = 7.50 buy 7.50 / 151.
        var both = IndexPortfolio.Following(owner, [Dividend(Day(2025, 12, 5), 0.50m), Split(Day(2025, 12, 5), 2m)], closes, 0.75m);
        Assert.Equal((20m + (20m * 0.50m * 0.75m / 151m)) * 151m, both[1].Value);
        Assert.Equal(3027.5m, decimal.Round(both[1].Value, 10));

        var reverse = IndexPortfolio.Following(owner, [Split(Day(2025, 12, 5), 2m), Split(Day(2026, 3, 16), 0.25m)], closes, 0.75m);
        Assert.Equal(5m * 640m, reverse[^1].Value);

        // What the fund did on the opening day itself, or after the last day, is not in the period.
        var outside = IndexPortfolio.Following(owner, [Split(Day(2025, 11, 28), 3m), Dividend(Day(2026, 4, 1), 9m)], closes, 0.75m);
        Assert.Equal([3000m, 10m * 151m, 10m * 640m], outside.Select(p => p.Value));
    }

    [Fact]
    public void WithdrawalSellsAtTheCloseOfItsDayAndTheIndexCanBeEmptied()
    {
        ValuePoint[] owner =
        [
            At(Day(2025, 12, 31), 1000m), At(Day(2026, 2, 27), 900m, outflow: 250m), At(Day(2026, 3, 31), 0m, outflow: 1050m),
            At(Day(2026, 6, 30), 0m),
        ];
        var closes = Closes((Day(2025, 12, 31), 100m), (Day(2026, 2, 27), 125m), (Day(2026, 3, 31), 131.25m), (Day(2026, 6, 30), 140m));

        var index = IndexPortfolio.Following(owner, [], closes, 0.75m);

        // 10 shares; 250 at 125 sell 2; the 8 left are worth 1,050 at 131.25 - exactly what is taken out.
        Assert.Equal([1000m, 1000m, 0m, 0m], index.Select(p => decimal.Round(p.Value, 20)));
        Assert.Equal([0m, 250m, 1050m, 0m], index.Select(p => p.Outflow));

        // One dollar more than the index holds: it cannot follow.
        owner[2] = owner[2] with { Outflow = 1051m };
        var overdrawn = Assert.Throws<InvalidOperationException>(() => IndexPortfolio.Following(owner, [], closes, 0.75m));
        Assert.Contains("On 2026-03-31 more is taken out than the index portfolio holds", overdrawn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatCannotBeFollowedIsRefused()
    {
        var closes = Closes((Day(2026, 1, 1), 100m), (Day(2026, 3, 31), 125m), (Day(2026, 4, 1), 125m), (Day(2026, 6, 30), 130m));

        // A close is missing for the day of a contribution, for the day of a dividend, for the opening value.
        var noClose = Assert.Throws<InvalidOperationException>(() =>
            IndexPortfolio.Following(Owner, [], closes.Where(c => c.Key != Day(2026, 4, 1)).ToDictionary(), 0.75m));
        Assert.Contains("There is no close of the index fund for 2026-04-01", noClose.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => IndexPortfolio.Following(Owner, [Dividend(Day(2026, 3, 20), 1m)], closes, 0.75m));
        Assert.Throws<InvalidOperationException>(() =>
            IndexPortfolio.Following([At(Day(2025, 12, 31), 500m), At(Day(2026, 6, 30), 600m)], [], closes, 0.75m));
        Assert.Throws<InvalidOperationException>(() =>
            IndexPortfolio.Following(Owner, [], new Dictionary<LocalDate, decimal>(closes) { [Day(2026, 4, 1)] = 0m }, 0.75m));

        // While nothing is held a dividend needs no price, and an opening of nothing needs none either.
        Assert.Equal(2340m, IndexPortfolio.Following(Owner, [Dividend(Day(2025, 12, 31), 1m)], closes, 0.75m)[^1].Value);

        Assert.Throws<ArgumentException>(() => IndexPortfolio.Following([Owner[0]], [], closes, 0.75m));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexPortfolio.Following(Owner, [], closes, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexPortfolio.Following(Owner, [], closes, 1.01m));
    }

    [Fact]
    public void FundEventIsOnlyWhatItSaysItIs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Dividend(Day(2026, 3, 20), 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Split(Day(2026, 3, 20), -2m));
        Assert.Throws<ArgumentException>(() => Dividend(Day(2026, 3, 20), 0.5673001m));
        Assert.Throws<ArgumentException>(() => new FundEvent(default, Day(2026, 3, 20), FundEventKind.Dividend, 1m, "yfinance"));
        Assert.Throws<ArgumentException>(() => new FundEvent(Fund, Day(2026, 3, 20), FundEventKind.Dividend, 1m, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FundEvent(Fund, Day(2026, 3, 20), (FundEventKind)7, 1m, "yfinance"));
        Assert.Equal(Dividend(Day(2026, 3, 20), 0.5673m), Dividend(Day(2026, 3, 20), 0.56730m));
    }
}
