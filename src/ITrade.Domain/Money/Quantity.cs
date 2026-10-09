using System.Globalization;

namespace ITrade.Domain;

/// <summary>A non-negative number of shares. Granularity is 1 for whole shares or e.g. 0.0001 for fractional.</summary>
public readonly record struct Quantity : IComparable<Quantity>
{
    public static readonly Quantity Zero = new(0m);

    public Quantity(decimal value)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A quantity cannot be negative.");
        }

        Value = value;
    }

    public decimal Value { get; }

    /// <summary>Largest multiple of <paramref name="granularity"/> not above this quantity.</summary>
    public Quantity FloorTo(decimal granularity) => new(Math.Floor(Value / Positive(granularity)) * granularity);

    /// <summary>
    /// The most shares (a multiple of <paramref name="granularity"/>) whose cost at <paramref name="price"/> fits in
    /// <paramref name="available"/>. Never more than the money allows; zero when nothing fits.
    /// </summary>
    public static Quantity Affordable(Money available, Price price, decimal granularity)
    {
        if (available.Currency != price.Currency)
        {
            throw new InvalidOperationException(
                $"Cash in {available.Currency} cannot buy at a price in {price.Currency}; convert first.");
        }

        if (available.Amount <= 0m)
        {
            return Zero;
        }

        return new Quantity(available.Amount / price.Value).FloorTo(granularity);
    }

    public static Quantity operator +(Quantity left, Quantity right) => new(left.Value + right.Value);

    /// <summary>Throws instead of going negative (selling more than held is a bug, not a short).</summary>
    public static Quantity operator -(Quantity left, Quantity right) => new(left.Value - right.Value);

    public static bool operator <(Quantity left, Quantity right) => left.Value < right.Value;

    public static bool operator >(Quantity left, Quantity right) => left.Value > right.Value;

    public static bool operator <=(Quantity left, Quantity right) => left.Value <= right.Value;

    public static bool operator >=(Quantity left, Quantity right) => left.Value >= right.Value;

    public int CompareTo(Quantity other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    private static decimal Positive(decimal granularity) =>
        granularity > 0m
            ? granularity
            : throw new ArgumentOutOfRangeException(nameof(granularity), granularity, "Granularity must be positive.");
}
