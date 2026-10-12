using NodaTime;

namespace ITrade.Domain;

/// <summary>What a fund did to its holders on a day.</summary>
public enum FundEventKind
{
    /// <summary>A distribution per share, as it was paid, in the currency the fund's prices are in.</summary>
    Dividend,

    /// <summary>A split: new shares per old one (2 for a 2:1 split, 0.25 for a 1:4 reverse split).</summary>
    Split,
}

/// <summary>
/// A dividend or a split of a fund on a day, as the data pipeline reports it — what the market knows about
/// the fund, not what the owner received: the owner's own dividends and splits are entries of the ledger and
/// come from the broker's statement. Used to follow an index fund with the owner's contributions, and to
/// notice a split the ledger does not hold. Never for tax.
/// </summary>
public sealed record FundEvent
{
    /// <summary>Digits after the decimal point a stored value keeps; a finer one is refused, not rounded.</summary>
    public const int Scale = 6;

    public FundEvent(InstrumentId instrument, LocalDate day, FundEventKind kind, decimal value, string source)
    {
        if (instrument.Value == Guid.Empty)
        {
            throw new ArgumentException("A fund event belongs to an instrument.", nameof(instrument));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of fund event.");
        }

        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A dividend or a split ratio is more than nothing.");
        }

        if (decimal.Round(value, Scale) != value)
        {
            throw new ArgumentException($"A fund event keeps {Scale} decimal places; {value} would be rounded silently.", nameof(value));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Instrument = instrument;
        Day = day;
        Kind = kind;
        Value = value;
        Source = source;
    }

    public InstrumentId Instrument { get; }

    /// <summary>The ex-date of a dividend; the first day the shares trade split.</summary>
    public LocalDate Day { get; }

    public FundEventKind Kind { get; }

    public decimal Value { get; }

    public string Source { get; }
}
