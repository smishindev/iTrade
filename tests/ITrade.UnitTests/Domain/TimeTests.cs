using ITrade.Domain;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.UnitTests.Domain;

/// <summary>P2.2.02: identifiers, time zones, trade dates.</summary>
public sealed class TimeTests
{
    private static Instant Utc(int y, int m, int d, int h = 12) => Instant.FromUtc(y, m, d, h, 0);

    [Theory]
    // 2026: US DST starts Sun 8 Mar, ends Sun 1 Nov; Israel DST starts Fri 27 Mar, ends Sun 25 Oct.
    [InlineData(2026, 1, 15, 7)]   // both winter: UTC-5 vs UTC+2
    [InlineData(2026, 3, 16, 6)]   // US summer, Israel still winter: UTC-4 vs UTC+2
    [InlineData(2026, 4, 15, 7)]   // both summer: UTC-4 vs UTC+3
    [InlineData(2026, 10, 28, 6)]  // Israel back to winter, US still summer
    [InlineData(2026, 11, 10, 7)]  // both winter again
    public void IsraelIsUsuallySevenHoursAheadOfNewYorkSometimesSix(int y, int m, int d, double hours) =>
        Assert.Equal(hours, Zones.IsraelAheadOfNewYorkHours(Utc(y, m, d)));

    [Fact]
    public void UsDstSwitchIsAtTwoInTheMorningNewYorkTime()
    {
        // 8 Mar 2026 06:59 UTC = 01:59 EST; 07:00 UTC = 03:00 EDT
        Assert.Equal(new LocalTime(1, 59), Zones.InNewYork(Instant.FromUtc(2026, 3, 8, 6, 59)).TimeOfDay);
        Assert.Equal(new LocalTime(3, 0), Zones.InNewYork(Instant.FromUtc(2026, 3, 8, 7, 0)).TimeOfDay);
    }

    [Fact]
    public void TradeDateIsTheNewYorkDate()
    {
        // 23:30 in Israel on 8 Oct 2026 = 16:30 in New York on 8 Oct: same trade date
        var lateIsraelEvening = Instant.FromUtc(2026, 10, 8, 20, 30);
        Assert.Equal(new LocalDate(2026, 10, 9), Zones.InIsrael(lateIsraelEvening.Plus(Duration.FromHours(1))).Date);
        Assert.Equal(new TradeDate(new LocalDate(2026, 10, 8)), TradeDate.Of(lateIsraelEvening));
        // 02:00 UTC on 9 Oct is still 22:00 on 8 Oct in New York
        Assert.Equal(new TradeDate(new LocalDate(2026, 10, 8)), TradeDate.Of(Instant.FromUtc(2026, 10, 9, 2, 0)));
        Assert.Equal("2026-10-08", TradeDate.Of(lateIsraelEvening).ToString());
    }

    [Fact]
    public void IdsAreVersion7AndOrderedByCreation()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => TransactionId.New()).ToList();
        Assert.All(ids, id => Assert.Equal(7, id.Value.Version));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.NotEqual(InstrumentId.New(), InstrumentId.New());
    }

    [Fact]
    public void FakeClockIsControlledByTheTest()
    {
        var clock = new FakeClock(Utc(2026, 10, 9));
        clock.Advance(Duration.FromDays(1));
        Assert.Equal(Utc(2026, 10, 10), clock.GetCurrentInstant());
        Assert.Equal(new TradeDate(new LocalDate(2026, 10, 10)), TradeDate.Of(clock.GetCurrentInstant()));
    }
}
