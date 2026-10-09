using System.Globalization;

namespace ITrade.Domain;

/// <summary>
/// An exact amount in one currency (PLAN §4: decimal, never floating point). Amounts are kept exact;
/// rounding to the currency's minor unit happens only where asked, with an explicit rule.
/// </summary>
public readonly record struct Money(decimal Amount, Currency Currency) : IComparable<Money>
{
    public static Money Zero(Currency currency) => new(0m, currency);

    public static Money Usd(decimal amount) => new(amount, Currency.Usd);

    public static Money Ils(decimal amount) => new(amount, Currency.Ils);

    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money left, Money right) =>
        new(left.Amount + right.Amount, SameCurrency(left, right));

    public static Money operator -(Money left, Money right) =>
        new(left.Amount - right.Amount, SameCurrency(left, right));

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    public static Money operator *(Money value, decimal factor) => new(value.Amount * factor, value.Currency);

    public static Money operator *(decimal factor, Money value) => value * factor;

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money other)
    {
        SameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>Rounds to the currency's minor unit with an explicit rule (no hidden banker's rounding).</summary>
    public Money RoundToMinor(MidpointRounding rule) =>
        new(Math.Round(Amount, Currency.MinorUnits, rule), Currency);

    /// <summary>Largest multiple of the minor unit not above the amount.</summary>
    public Money FloorToMinor() => new(Math.Floor(Amount / Currency.MinorUnit) * Currency.MinorUnit, Currency);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Amount} {Currency.Code}");

    private static Currency SameCurrency(Money left, Money right) =>
        left.Currency == right.Currency
            ? left.Currency
            : throw new InvalidOperationException(
                $"Cannot combine {left.Currency} and {right.Currency}; convert explicitly with an FxRate first.");
}
