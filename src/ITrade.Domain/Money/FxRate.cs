using NodaTime;

namespace ITrade.Domain;

/// <summary>
/// "1 <see cref="From"/> = <see cref="Rate"/> <see cref="To"/>" on a date, from a named source (e.g. the Bank of
/// Israel representative rate, PLAN §6). Conversion is always explicit. In the direction of the quote
/// (<see cref="From"/> → <see cref="To"/>) it is a multiplication and exact; the other way divides, so the
/// result carries as many digits as a decimal holds — round it where the amount is shown or booked.
/// </summary>
public sealed record FxRate
{
    /// <summary>Digits after the decimal point a stored rate keeps (`numeric(12,6)`); official rates have three or four.</summary>
    public const int Scale = 6;

    public FxRate(Currency from, Currency to, decimal rate, LocalDate date, string source)
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

    /// <summary>The calendar date the source published the rate for (a date of the source's country, not UTC).</summary>
    public LocalDate Date { get; }

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

/// <summary>
/// The rate to use on a date: the one the source published for that date, or — on a weekend or a holiday of
/// the source — the latest earlier one, carried forward. A carried rate says so; it is never passed off as the
/// rate of the day. Keep this next to every amount converted with it: once only the converted amount is left,
/// nothing says how good the rate was.
/// </summary>
public sealed record AppliedFxRate
{
    public AppliedFxRate(FxRate rate, LocalDate on, bool isProvisional = false)
    {
        ArgumentNullException.ThrowIfNull(rate);
        if (rate.Date > on)
        {
            throw new ArgumentException($"A rate of {rate.Date:R} cannot be used on the earlier {on:R}.", nameof(rate));
        }

        if (isProvisional && rate.Date == on)
        {
            throw new ArgumentException("The rate of the day itself is not provisional.", nameof(isProvisional));
        }

        Rate = rate;
        On = on;
        IsProvisional = isProvisional;
    }

    public FxRate Rate { get; }

    /// <summary>The date the rate is used on.</summary>
    public LocalDate On { get; }

    /// <summary>Calendar days between the date the rate was published for and the date it is used on.</summary>
    public int DaysCarried => Period.DaysBetween(Rate.Date, On);

    public bool IsCarriedForward => DaysCarried > 0;

    /// <summary>
    /// True for a carried rate while no later rate is known: the source may still publish one for this date
    /// (today, before the afternoon), or the rates were not updated. Such a figure is expected to change. False
    /// for the rate of the day and for a gap the source has moved past (a later rate exists) — which is as final
    /// as a rate gets, though not a promise: a source can publish late for a date it skipped, and an amount that
    /// must not move afterwards (a tax figure) keeps the rate it was computed with.
    /// </summary>
    public bool IsProvisional { get; }
}
