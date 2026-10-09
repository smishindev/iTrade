using System.Globalization;

namespace ITrade.Domain;

/// <summary>A positive price per share in one currency. Rounding to the exchange tick is always explicit.</summary>
public readonly record struct Price
{
    public Price(decimal value, Currency currency)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A price must be positive.");
        }

        Value = value;
        Currency = currency;
    }

    public decimal Value { get; }

    public Currency Currency { get; }

    /// <summary>Largest multiple of <paramref name="tick"/> not above the price (e.g. a buy limit that never overpays).</summary>
    public Price FloorToTick(decimal tick)
    {
        var floored = Math.Floor(Value / Positive(tick)) * tick;
        return floored > 0m
            ? new Price(floored, Currency)
            : throw new InvalidOperationException($"{this} is below one tick ({tick}); it has no positive floor.");
    }

    /// <summary>Smallest multiple of <paramref name="tick"/> not below the price.</summary>
    public Price CeilToTick(decimal tick) => new(Math.Ceiling(Value / Positive(tick)) * tick, Currency);

    /// <summary>Exact cost of <paramref name="quantity"/> shares at this price (not rounded).</summary>
    public Money Times(Quantity quantity) => new(Value * quantity.Value, Currency);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Value} {Currency.Code}");

    private static decimal Positive(decimal tick) =>
        tick > 0m ? tick : throw new ArgumentOutOfRangeException(nameof(tick), tick, "A tick must be positive.");
}
