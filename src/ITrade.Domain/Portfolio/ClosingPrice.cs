using NodaTime;

namespace ITrade.Domain;

/// <summary>
/// What an instrument closed at on one exchange session — the price as it traded that day, not a figure
/// adjusted afterwards for splits. For valuation only: never for tax and never for an order.
/// </summary>
public sealed record ClosingPrice
{
    /// <summary>Digits after the decimal point a stored price keeps; a finer one is refused, not rounded.</summary>
    public const int Scale = 6;

    public ClosingPrice(InstrumentId instrument, LocalDate session, Price close, string source)
    {
        if (instrument.Value == Guid.Empty)
        {
            throw new ArgumentException("A closing price belongs to an instrument.", nameof(instrument));
        }

        if (decimal.Round(close.Value, Scale) != close.Value)
        {
            throw new ArgumentException(
                $"A closing price keeps {Scale} decimal places; {close} would be rounded silently.", nameof(close));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Instrument = instrument;
        Session = session;
        Close = close;
        Source = source;
    }

    public InstrumentId Instrument { get; }

    /// <summary>The trading day on the exchange's own calendar.</summary>
    public LocalDate Session { get; }

    public Price Close { get; }

    public string Source { get; }
}

/// <summary>Why a date has, or has not, a closing price to value with.</summary>
public enum PriceAvailability
{
    /// <summary>The date is a session and this is its close.</summary>
    OfTheSession,

    /// <summary>The exchange was closed on the date (a weekend, a holiday): the close of the last session before it.</summary>
    CarriedOverAClosedDay,

    /// <summary>
    /// The latest session up to the date has no price for this instrument although other instruments have
    /// one. A gap in the data: it is said, not filled with an older price.
    /// </summary>
    MissingForTheSession,

    /// <summary>Nothing to go by: the date is in the future, before the instrument's first price, or the prices are too old.</summary>
    Unavailable,
}

/// <summary>
/// The closing price to value an instrument with on a date — or the reason there is none. Keep it next to
/// every amount computed from it: once only the amount is left, nothing says how good the price was.
/// </summary>
public sealed record PriceOnDate
{
    private PriceOnDate(
        InstrumentId instrument, LocalDate date, PriceAvailability availability, ClosingPrice? price, bool isProvisional)
    {
        Instrument = instrument;
        Date = date;
        Availability = availability;
        Price = price;
        IsProvisional = isProvisional;
    }

    public InstrumentId Instrument { get; }

    public LocalDate Date { get; }

    public PriceAvailability Availability { get; }

    /// <summary>The price, when there is one to use (<see cref="HasPrice"/>).</summary>
    public ClosingPrice? Price { get; }

    /// <summary>
    /// True for a carried price while no later session is known: the day may yet turn out to be a session
    /// whose prices were not loaded (today, before the data arrives). Such a figure is expected to change.
    /// </summary>
    public bool IsProvisional { get; }

    public bool HasPrice => Price is not null;

    /// <summary>Calendar days between the session the price is of and the date it is used on.</summary>
    public int DaysCarried => Price is null ? 0 : Period.DaysBetween(Price.Session, Date);

    public static PriceOnDate OfTheSession(ClosingPrice price)
    {
        ArgumentNullException.ThrowIfNull(price);
        return new PriceOnDate(price.Instrument, price.Session, PriceAvailability.OfTheSession, price, false);
    }

    public static PriceOnDate Carried(ClosingPrice price, LocalDate date, bool isProvisional)
    {
        ArgumentNullException.ThrowIfNull(price);
        return price.Session < date
            ? new PriceOnDate(price.Instrument, date, PriceAvailability.CarriedOverAClosedDay, price, isProvisional)
            : throw new ArgumentException(
                $"A close of {price.Session:R} is not carried to {date:R}: it is carried forward only.", nameof(date));
    }

    public static PriceOnDate Missing(InstrumentId instrument, LocalDate date) =>
        new(instrument, date, PriceAvailability.MissingForTheSession, null, false);

    public static PriceOnDate Unavailable(InstrumentId instrument, LocalDate date) =>
        new(instrument, date, PriceAvailability.Unavailable, null, false);
}
