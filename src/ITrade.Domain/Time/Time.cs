using NodaTime;

namespace ITrade.Domain;

// The current instant comes from NodaTime.IClock (SystemClock in Host, FakeClock in tests); Domain never reads
// the system clock itself (architecture test).

/// <summary>Time zones the platform works in: the exchange (New York) and the owner / tax authority (Israel).</summary>
public static class Zones
{
    public static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];
    public static readonly DateTimeZone Israel = DateTimeZoneProviders.Tzdb["Asia/Jerusalem"];

    public static ZonedDateTime InNewYork(Instant instant) => instant.InZone(NewYork);

    public static ZonedDateTime InIsrael(Instant instant) => instant.InZone(Israel);

    /// <summary>Hours Israel is ahead of New York at this instant (7 usually, 6 when only one has switched DST).</summary>
    public static double IsraelAheadOfNewYorkHours(Instant instant) =>
        (Israel.GetUtcOffset(instant) - NewYork.GetUtcOffset(instant)).ToTimeSpan().TotalHours;
}

/// <summary>A trading session's date on the exchange calendar (America/New_York), not a UTC date.</summary>
public readonly record struct TradeDate(LocalDate Value) : IComparable<TradeDate>
{
    /// <summary>The New York calendar date of an instant (a 23:30 Israel evening is still the same NY date).</summary>
    public static TradeDate Of(Instant instant) => new(Zones.InNewYork(instant).Date);

    public int CompareTo(TradeDate other) => Value.CompareTo(other.Value);

    public static bool operator <(TradeDate left, TradeDate right) => left.Value < right.Value;

    public static bool operator >(TradeDate left, TradeDate right) => left.Value > right.Value;

    public static bool operator <=(TradeDate left, TradeDate right) => left.Value <= right.Value;

    public static bool operator >=(TradeDate left, TradeDate right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Exchange sessions (XNYS). Implemented in P3 from the session calendar the Python pipeline exports.</summary>
public interface IExchangeCalendar
{
    bool IsSession(TradeDate day);

    TradeDate NextSession(TradeDate day);

    /// <summary>The date cash from a trade on <paramref name="tradeDate"/> settles (T+1 since 2024-05-28).</summary>
    TradeDate SettlementDate(TradeDate tradeDate);
}
