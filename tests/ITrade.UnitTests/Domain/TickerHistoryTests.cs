using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Domain;

/// <summary>P3.1.01: the ticker of an instrument on a date, across renames.</summary>
public sealed class TickerHistoryTests
{
    private static readonly Ticker Fb = new("FB");
    private static readonly Ticker Meta = new("META");
    private static readonly Ticker Mvrs = new("MVRS");
    private static readonly LocalDate Renamed = new(2022, 6, 9);

    [Fact]
    public void InstrumentThatWasNeverRenamedHasOneTickerOnEveryDate()
    {
        var history = new TickerHistory(Fb);

        Assert.Equal(Fb, history.On(new LocalDate(2012, 5, 18)));
        Assert.Equal(Fb, history.On(new LocalDate(2030, 1, 1)));
        Assert.Empty(history.Former);
    }

    [Fact]
    public void OldTickerHoldsUpToTheDayBeforeTheRenameAndTheNewOneFromThatDay()
    {
        var history = new TickerHistory(Fb).Renamed(Meta, Renamed);

        Assert.Equal(Meta, history.Current);
        Assert.Equal(Fb, history.On(new LocalDate(2012, 5, 18)));
        Assert.Equal(Fb, history.On(Renamed.PlusDays(-1)));
        Assert.Equal(Meta, history.On(Renamed));
        Assert.Equal(Meta, history.On(new LocalDate(2026, 10, 12)));
        Assert.Equal([new FormerTicker(Fb, Renamed)], history.Former);
    }

    [Fact]
    public void SeveralRenamesAreKeptInOrderWhateverOrderTheyAreGivenIn()
    {
        var later = new LocalDate(2024, 3, 1);
        var history = new TickerHistory(Mvrs, [new FormerTicker(Meta, later), new FormerTicker(Fb, Renamed)]);

        Assert.Equal(Fb, history.On(Renamed.PlusDays(-1)));
        Assert.Equal(Meta, history.On(Renamed));
        Assert.Equal(Meta, history.On(later.PlusDays(-1)));
        Assert.Equal(Mvrs, history.On(later));
        Assert.Equal([Fb, Meta], history.Former.Select(f => f.Ticker));
    }

    [Fact]
    public void TickerMayComeBackAfterAnotherOneInBetween()
    {
        var back = new LocalDate(2024, 3, 1);
        var history = new TickerHistory(Fb).Renamed(Meta, Renamed).Renamed(Fb, back);

        Assert.Equal(Fb, history.On(Renamed.PlusDays(-1)));
        Assert.Equal(Meta, history.On(back.PlusDays(-1)));
        Assert.Equal(Fb, history.On(back));
    }

    [Fact]
    public void RenameMustChangeTheTickerAndComeAfterThePreviousOne()
    {
        var history = new TickerHistory(Fb).Renamed(Meta, Renamed);

        Assert.Throws<ArgumentException>(() => history.Renamed(Meta, Renamed.PlusDays(30)));
        Assert.Throws<ArgumentException>(() => history.Renamed(Mvrs, Renamed));
        Assert.Throws<ArgumentException>(() => history.Renamed(Mvrs, Renamed.PlusDays(-1)));
        Assert.Equal(Mvrs, history.Renamed(Mvrs, Renamed.PlusDays(1)).Current);
    }

    [Fact]
    public void HistoryThatIsNotASequenceOfRenamesIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new TickerHistory(default));
        Assert.Throws<ArgumentException>(() => new TickerHistory(Meta, [new FormerTicker(Meta, Renamed)]));
        Assert.Throws<ArgumentException>(() =>
            new TickerHistory(Mvrs, [new FormerTicker(Fb, Renamed), new FormerTicker(Meta, Renamed)]));
        Assert.Throws<ArgumentException>(() =>
            new TickerHistory(Mvrs, [new FormerTicker(Fb, Renamed), new FormerTicker(Fb, Renamed.PlusDays(5))]));
    }
}
