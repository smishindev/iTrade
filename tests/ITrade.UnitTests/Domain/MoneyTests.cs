using CsCheck;
using ITrade.Domain;

namespace ITrade.UnitTests.Domain;

/// <summary>P2.2.01: money, price, quantity and FX rates — hand examples and properties (CsCheck).</summary>
public sealed class MoneyTests
{
    // Amounts with up to 4 decimals in a realistic range; prices 0.01 … 5 000.
    private static readonly Gen<decimal> Amount = Gen.Long[-10_000_000_000, 10_000_000_000].Select(x => x / 10_000m);
    private static readonly Gen<decimal> Cash = Gen.Long[0, 1_000_000_000].Select(x => x / 100m);
    private static readonly Gen<decimal> PriceValue = Gen.Long[1, 500_000].Select(x => x / 100m);
    private static readonly Gen<decimal> Granularity = Gen.OneOfConst(1m, 0.0001m, 0.001m);
    private static readonly Gen<decimal> Rate = Gen.Long[25_000, 50_000].Select(x => x / 10_000m); // 2.5 … 5.0 ILS/USD

    [Fact]
    public void AddsAndSubtractsInOneCurrency()
    {
        Assert.Equal(Money.Usd(10.05m), Money.Usd(7.5m) + Money.Usd(2.55m));
        Assert.Equal(Money.Ils(-1m), Money.Ils(1m) - Money.Ils(2m));
        Assert.True(Money.Usd(1m) < Money.Usd(1.01m));
    }

    [Fact]
    public void DifferentCurrenciesNeverMix()
    {
        Assert.Throws<InvalidOperationException>(() => Money.Usd(1m) + Money.Ils(1m));
        Assert.Throws<InvalidOperationException>(() => Money.Usd(1m) < Money.Ils(5m));
        Assert.Throws<InvalidOperationException>(
            () => Quantity.Affordable(Money.Ils(1000m), new Price(10m, Currency.Usd), 1m));
    }

    [Fact]
    public void RoundingIsExplicit()
    {
        Assert.Equal(Money.Usd(0.13m), Money.Usd(0.125m).RoundToMinor(MidpointRounding.AwayFromZero));
        Assert.Equal(Money.Usd(0.12m), Money.Usd(0.125m).RoundToMinor(MidpointRounding.ToEven));
        Assert.Equal(Money.Ils(3.04m), Money.Ils(3.0499m).FloorToMinor());
    }

    [Fact]
    public void AdditionIsExactAndReversible() =>
        Gen.Select(Amount, Amount).Sample((a, b) =>
            Money.Usd(a) + Money.Usd(b) - Money.Usd(b) == Money.Usd(a));

    [Fact]
    public void NoNegativeQuantity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Quantity(-0.0001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Quantity(1m) - new Quantity(2m));
        Gen.Select(Cash, PriceValue, Granularity).Sample((cash, price, g) =>
            Quantity.Affordable(Money.Usd(cash), new Price(price, Currency.Usd), g).Value >= 0m);
        Assert.Equal(Quantity.Zero, Quantity.Affordable(Money.Usd(-5m), new Price(10m, Currency.Usd), 1m));
    }

    [Fact]
    public void AffordableNeverCostsMoreThanTheMoneyAndIsTheMaximum() =>
        Gen.Select(Cash, PriceValue, Granularity).Sample((cash, priceValue, g) =>
        {
            var price = new Price(priceValue, Currency.Usd);
            var q = Quantity.Affordable(Money.Usd(cash), price, g);
            var oneMore = new Quantity(q.Value + g);
            return price.Times(q).Amount <= cash                 // never spends more than available
                && price.Times(oneMore).Amount > cash           // and could not buy one more step
                && q.Value % g == 0m;                            // a whole number of steps
        });

    [Fact]
    public void AffordableByHand()
    {
        var spy = new Price(700m, Currency.Usd);
        Assert.Equal(new Quantity(3m), Quantity.Affordable(Money.Usd(2624m), spy, 1m));      // 80% of $3,280
        Assert.Equal(new Quantity(3.7485m), Quantity.Affordable(Money.Usd(2624m), spy, 0.0001m));
    }

    [Fact]
    public void TickRoundingStaysOnTheRightSide() =>
        Gen.Select(Gen.Long[100, 50_000_000].Select(x => x / 10_000m), Gen.OneOfConst(0.01m, 0.05m, 0.0001m))
            .Sample((value, tick) =>
            {
                var p = new Price(value, Currency.Usd);
                var floor = value >= tick ? p.FloorToTick(tick).Value : tick;
                var ceil = p.CeilToTick(tick).Value;
                return (value < tick || (floor <= value && value - floor < tick && floor % tick == 0m))
                    && ceil >= value && ceil - value < tick && ceil % tick == 0m;
            });

    [Fact]
    public void PriceBelowOneTickHasNoFloor() =>
        Assert.Throws<InvalidOperationException>(() => new Price(0.004m, Currency.Usd).FloorToTick(0.01m));

    [Fact]
    public void FxConversionIsExplicitAndRoundTripsToTheMinorUnit()
    {
        var rate = new FxRate(Currency.Usd, Currency.Ils, 3.05m, new DateOnly(2026, 10, 6), "Bank of Israel");
        Assert.Equal(Money.Ils(10_004m), rate.Convert(Money.Usd(3280m)));
        Assert.Throws<ArgumentException>(() => new FxRate(Currency.Usd, Currency.Usd, 1m, default, "x"));
        Gen.Select(Cash, Rate).Sample((usd, r) =>
        {
            var fx = new FxRate(Currency.Usd, Currency.Ils, r, new DateOnly(2026, 1, 1), "test");
            var back = fx.Convert(fx.Convert(Money.Usd(usd)).RoundToMinor(MidpointRounding.AwayFromZero));
            return Math.Abs(back.Amount - usd) <= Currency.Usd.MinorUnit && back.Currency == Currency.Usd;
        });
    }
}
