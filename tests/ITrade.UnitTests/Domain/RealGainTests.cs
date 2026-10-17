using CsCheck;
using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Domain;

/// <summary>
/// P4.1.02: the real gain of a sale of a dollar security (docs/tax-sources.md R2, R3). The acceptance examples are
/// the Tax Authority's own: circular 10/2025 "קיזוז הפסדי הון" (27.11.2025), appendix, examples 1-4 - bought for
/// $1,000 on 1.1.2017, sold on 1.6.2017. Not checked by an accountant.
/// </summary>
public sealed class RealGainTests
{
    private static readonly LocalDate Bought = new(2017, 1, 1);
    private static readonly LocalDate Sold = new(2017, 6, 1);

    private static AppliedFxRate Rate(decimal rate, LocalDate day) =>
        new(new FxRate(Currency.Usd, Currency.Ils, rate, day, "BOI representative"), day);

    private static Disposal Sale(decimal cost, decimal proceeds, decimal purchaseFees = 0m, decimal saleFees = 0m, decimal shares = 10m) =>
        new(TransactionId.New(), TransactionId.New(), HoldingPlaceId.New(), InstrumentId.New(), Bought, Sold,
            new Quantity(shares), Money.Usd(cost), Money.Usd(purchaseFees), Money.Usd(proceeds), Money.Usd(saleFees));

    private static SaleInShekels Of(decimal cost, decimal proceeds, decimal rateBought, decimal rateSold) =>
        RealGain.Of(Sale(cost, proceeds), Rate(rateBought, Bought), Rate(rateSold, Sold));

    [Fact]
    public void Example1TheDollarGainAndTheShekelWeakened()
    {
        // $1,000 at 3.4 = ₪3,400; $1,200 at 3.7 = ₪4,440; inflationary ₪300 exempt; real gain ₪740.
        var sale = Of(1_000m, 1_200m, 3.4m, 3.7m);

        Assert.Equal((3_400m, 4_440m, 1_040m, 300m, 740m, 0m), (sale.Cost, sale.Proceeds, sale.NominalGain, sale.InflationaryAmount, sale.RealGain, sale.OffsettableLoss));
        Assert.Equal(300m, sale.ExemptInflationaryAmount);
    }

    [Fact]
    public void Example2ADollarLossAndTheShekelWeakenedTheWholeNominalLossIsOffsettable()
    {
        // $800 at 3.7 = ₪2,960 against ₪3,400: a loss of ₪440, all of it offsettable.
        var sale = Of(1_000m, 800m, 3.4m, 3.7m);

        Assert.Equal((-440m, 0m, 440m, 0m), (sale.NominalGain, sale.RealGain, sale.OffsettableLoss, sale.LossNotOffsettable));
    }

    [Fact]
    public void Example2ADollarLossTheShekelWeakenedMoreAllOfTheNominalGainIsExempt()
    {
        // The same sale at 5: ₪4,000 against ₪3,400, a nominal gain of ₪600 below an inflationary amount of ₪1,600.
        var sale = Of(1_000m, 800m, 3.4m, 5m);

        Assert.Equal((600m, 1_600m, 0m, 600m), (sale.NominalGain, sale.InflationaryAmount, sale.RealGain, sale.ExemptInflationaryAmount));
    }

    [Fact]
    public void Example3TheDollarGainAndTheShekelStrengthenedTheWholeNominalGainIsReal()
    {
        // $1,200 at 3.4 = ₪4,080 against $1,000 at 3.7 = ₪3,700: ₪380, the negative inflationary amount counts as 0.
        var sale = Of(1_000m, 1_200m, 3.7m, 3.4m);

        Assert.Equal((380m, -300m, 380m, 0m), (sale.NominalGain, sale.InflationaryAmount, sale.RealGain, sale.ExemptInflationaryAmount));
    }

    [Fact]
    public void Example4ADollarLossAndTheShekelStrengthenedOnlyTheDollarLossIsOffsettable()
    {
        // $900 at 3.4 = ₪3,060 against ₪3,700: a nominal loss of ₪640, of it ₪300 the exchange rate - ₪340 offsettable.
        var sale = Of(1_000m, 900m, 3.7m, 3.4m);

        Assert.Equal((-640m, 0m, 340m, 300m), (sale.NominalGain, sale.RealGain, sale.OffsettableLoss, sale.LossNotOffsettable));
    }

    [Fact]
    public void AShekelLossThatIsAllExchangeRateIsNoOffsettableLoss()
    {
        // Our reading of the rule (docs/tax-sources.md R2): a dollar gain, wiped out in shekels by the strong shekel.
        var sale = Of(1_000m, 1_050m, 3.7m, 3.4m);

        Assert.Equal((-130m, 0m, 0m, 130m), (sale.NominalGain, sale.RealGain, sale.OffsettableLoss, sale.LossNotOffsettable));
    }

    [Fact]
    public void CommissionsBelongToTheCostAndTheProceedsEachAtItsOwnDaysRate()
    {
        // 5 VT bought for $788.35 + $1.000015 at 3.08, sold for $800 - $1.000021 at 3.10 (form 1325 nets the fees).
        var sale = RealGain.Of(Sale(788.35m, 800m, 1.000015m, 1.000021m, 5m), Rate(3.08m, Bought), Rate(3.10m, Sold));

        Assert.Equal(789.350015m * 3.08m, sale.Cost);
        Assert.Equal(798.999979m * 3.10m, sale.Proceeds);
        Assert.Equal(789.350015m * 0.02m, sale.InflationaryAmount);
        Assert.Equal((798.999979m - 789.350015m) * 3.10m, sale.RealGain); // the dollar gain at the sale rate
        Assert.Equal(29.91m, Math.Round(sale.RealGain, 2, MidpointRounding.AwayFromZero)); // to the agora, only where shown
    }

    [Fact]
    public void NoGainEqualRatesAndCommissionsAboveTheProceedsComeOutAsTheRuleSays()
    {
        var even = Of(1_000m, 1_000m, 3.5m, 3.5m);
        var gainAtOneRate = Of(1_000m, 1_100m, 3.5m, 3.5m);
        var lossAtOneRate = Of(1_000m, 900m, 3.5m, 3.5m);
        // A sale for $1 with a $2 commission: proceeds of -$1 - all of the $11 paid and received is a loss.
        var tiny = RealGain.Of(Sale(10m, 1m, 0m, 2m), Rate(3.4m, Bought), Rate(3.7m, Sold));

        Assert.Equal((0m, 0m, 0m), (even.NominalGain, even.RealGain, even.OffsettableLoss));
        Assert.Equal((0m, 350m), (gainAtOneRate.InflationaryAmount, gainAtOneRate.RealGain));
        Assert.Equal(350m, lossAtOneRate.OffsettableLoss);
        Assert.Equal((-3.7m - 34m, 37.7m), (tiny.NominalGain, tiny.OffsettableLoss));
    }

    [Fact]
    public void ALotBoughtBefore2006OrARateOfAnotherSourceIsRefused()
    {
        var old = new Disposal(TransactionId.New(), TransactionId.New(), HoldingPlaceId.New(), InstrumentId.New(), new LocalDate(2005, 12, 30), Sold,
            new Quantity(1m), Money.Usd(100m), Money.Usd(0m), Money.Usd(120m), Money.Usd(0m));
        var yahoo = new AppliedFxRate(new FxRate(Currency.Usd, Currency.Ils, 3.7m, Sold, "yfinance"), Sold);

        Assert.Contains("transitional rules", Assert.Throws<InvalidOperationException>(() => RealGain.Of(old, Rate(4.5m, old.Acquired), Rate(3.7m, Sold))).Message, StringComparison.Ordinal);
        Assert.Contains("Bank of Israel's representative rate", Assert.Throws<InvalidOperationException>(() => RealGain.Of(Sale(1_000m, 1_200m), Rate(3.4m, Bought), yahoo)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsNotADollarSaleOrNotAFinalRateIsRefused()
    {
        var sale = Sale(1_000m, 1_200m);
        var ils = new Disposal(sale.Sale, sale.Lot, sale.HoldingPlace, sale.Instrument, Bought, Sold, sale.Quantity,
            Money.Ils(1_000m), Money.Ils(0m), Money.Ils(1_200m), Money.Ils(0m));
        var provisional = new AppliedFxRate(new FxRate(Currency.Usd, Currency.Ils, 3.7m, Sold.PlusDays(-1), "BOI representative"), Sold, isProvisional: true);

        Assert.Throws<InvalidOperationException>(() => RealGain.Of(ils, Rate(3.4m, Bought), Rate(3.7m, Sold)));
        Assert.Contains("provisional", Assert.Throws<InvalidOperationException>(() => RealGain.Of(sale, Rate(3.4m, Bought), provisional)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RealGain.Of(sale, Rate(3.4m, Bought.PlusDays(1)), Rate(3.7m, Sold))); // a rate of another day
        Assert.Throws<ArgumentException>(() => RealGain.Of(sale, Rate(3.4m, Bought), new AppliedFxRate(new FxRate(Currency.Ils, Currency.Usd, 0.27m, Sold, "x"), Sold)));
    }

    [Fact]
    public void TheRulesHoldForAnySale()
    {
        // Rates and amounts as they come: six-place rates, up to 12 places of a part of a lot.
        var amounts = Gen.Decimal[0.01m, 100_000m].Select(d => Math.Round(d, 6));
        var rates = Gen.Decimal[2.5m, 5m].Select(d => Math.Round(d, 4));
        Gen.Select(amounts, amounts, rates, rates).Sample((cost, proceeds, bought, sold) =>
        {
            var sale = Of(cost, proceeds, bought, sold);
            var nominal = sale.NominalGain;

            // A gain is never taxed beyond its nominal amount, a loss never offsets more than it lost, and one of the two is 0.
            Assert.InRange(sale.RealGain, 0m, Math.Max(0m, nominal));
            Assert.InRange(sale.OffsettableLoss, 0m, Math.Max(0m, -nominal));
            Assert.True(sale.RealGain == 0m || sale.OffsettableLoss == 0m);

            // Taken in two halves the sale gives the same, to the last digit.
            var half = Of(cost / 2m, proceeds / 2m, bought, sold);
            var other = Of(cost - (cost / 2m), proceeds - (proceeds / 2m), bought, sold);
            Assert.Equal(sale.RealGain, half.RealGain + other.RealGain);
            Assert.Equal(sale.OffsettableLoss, half.OffsettableLoss + other.OffsettableLoss);
        });
    }
}
