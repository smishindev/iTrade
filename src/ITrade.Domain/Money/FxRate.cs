namespace ITrade.Domain;

/// <summary>
/// "1 <see cref="From"/> = <see cref="Rate"/> <see cref="To"/>" on a date, from a named source (e.g. the Bank of
/// Israel representative rate, PLAN §6). Conversion is always explicit and exact; round afterwards if needed.
/// </summary>
public sealed record FxRate
{
    public FxRate(Currency from, Currency to, decimal rate, DateOnly date, string source)
    {
        if (from == to)
        {
            throw new ArgumentException("A rate needs two different currencies.", nameof(to));
        }

        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "A rate must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        From = from;
        To = to;
        Rate = rate;
        Date = date;
        Source = source;
    }

    public Currency From { get; }

    public Currency To { get; }

    public decimal Rate { get; }

    public DateOnly Date { get; }

    public string Source { get; }

    public Money Convert(Money amount)
    {
        if (amount.Currency == From)
        {
            return new Money(amount.Amount * Rate, To);
        }

        if (amount.Currency == To)
        {
            return new Money(amount.Amount / Rate, From);
        }

        throw new InvalidOperationException($"A {From}/{To} rate cannot convert {amount.Currency}.");
    }
}
