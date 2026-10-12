using ITrade.Application.Portfolio;
using ITrade.Application.Prices;
using ITrade.Domain;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.UnitTests.Application;

/// <summary>
/// P3.3.02: closing prices for valuation — imported once however often the export is read, never overwritten,
/// nothing taken that is not the close of a finished session, and a price for any date that says what it is:
/// the session's own, carried over a closed day, or missing.
/// October 2026: the 1st is a Thursday, the 2nd a Friday, the 5th a Monday, the 6th a Tuesday.
/// </summary>
public sealed class ClosingPricesTests
{
    private static readonly Instrument Spy = Etf("SPY");
    private static readonly Instrument Xlk = Etf("XLK");
    private static readonly Instrument Xlc = Etf("XLC");

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 10, 7, 14, 0)); // Wednesday 10:00 in New York
    private readonly MemoryStore _store = new();
    private readonly ClosingPrices _prices;

    public ClosingPricesTests() => _prices = new ClosingPrices(_store, new Catalogue(Spy, Xlk, Xlc), _clock);

    private static Instrument Etf(string ticker) =>
        new(InstrumentId.New(), new Ticker(ticker), "ARCA", Currency.Usd, $"{ticker} fund");

    private static LocalDate Day(int day) => new(2026, 10, day);

    /// <summary>The weekdays of October 2026 from the 1st: every one of them was a session.</summary>
    private static List<LocalDate> Weekdays(int through) =>
        Enumerable.Range(1, through).Select(Day).Where(d => d.DayOfWeek is not (IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday)).ToList();

    /// <summary>
    /// An export as the pipeline writes it: the sessions from 1 October through <paramref name="through"/>,
    /// every ticker fetched in the night after the last of them, written three hours later.
    /// </summary>
    private static ClosingPricesExport Export(
        (string Ticker, int Day, decimal Close)[] lines,
        int through = 6,
        IReadOnlyList<LocalDate>? sessions = null,
        string? currency = "USD",
        (string Ticker, int Day)[]? splits = null,
        Instant? fetchedAt = null,
        Instant? exportedAt = null,
        string calendar = ClosingPrices.Calendar,
        (string Ticker, int Day, FundEventKind Kind, decimal Value)[]? events = null)
    {
        var fetched = fetchedAt ?? Instant.FromUtc(2026, 10, through + 1, 3, 0);
        var tickers = lines.Select(l => l.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
            ticker => ticker,
            ticker => new ExportedTicker(
                fetched, currency, (splits ?? []).Where(s => s.Ticker == ticker).Select(s => Day(s.Day)).ToHashSet()),
            StringComparer.OrdinalIgnoreCase);
        return new ClosingPricesExport(
            "yfinance", "0f0f", exportedAt ?? fetched + Duration.FromHours(3), calendar, sessions ?? Weekdays(through), tickers,
            lines.Select(l => new ClosingPriceLine(l.Ticker, Day(l.Day), l.Close)).ToList(),
            (events ?? []).Select(e => new FundEventLine(e.Ticker, Day(e.Day), e.Kind, e.Value)).ToList());
    }

    private static ClosingPricesExport Export(params (string Ticker, int Day, decimal Close)[] lines) => Export(lines, through: 6);

    private Task<ClosingPricesImport> ImportAsync(ClosingPricesExport export, string? acceptedCode = null) =>
        _prices.ImportAsync(export, acceptedCode, CancellationToken.None);

    private Task<PriceOnDate> OnAsync(Instrument instrument, LocalDate date) =>
        _prices.OnAsync(instrument.Id, date, CancellationToken.None);

    private static readonly (string Ticker, int Day, decimal Close)[] WeekLines =
    [
        ("SPY", 2, 769.64m), ("SPY", 5, 774.83m), ("SPY", 6, 779.09m),
        ("XLK", 2, 301.10m), ("XLK", 6, 304.20m),
        ("XLC", 6, 110.05m),
    ];

    /// <summary>
    /// Sessions Thursday to Tuesday. SPY has Friday, Monday and Tuesday; XLK has no Monday; XLC starts on
    /// Tuesday; nobody has Thursday.
    /// </summary>
    private static ClosingPricesExport Week() => Export(WeekLines);

    /// <summary>The same a day later, when Wednesday's session is over too.</summary>
    private ClosingPricesExport WeekAnd(params (string Ticker, int Day, decimal Close)[] more)
    {
        _clock.Reset(Instant.FromUtc(2026, 10, 8, 14, 0));
        return Export([.. WeekLines, .. more], through: 7);
    }

    [Fact]
    public async Task ExportIsStoredOnceHoweverOftenItIsImported()
    {
        var first = await ImportAsync(Week());
        var again = await ImportAsync(Week());

        Assert.Equal(
            (6, 0, 0, 0, 4, Day(6)),
            (first.Added, first.Unchanged, first.Revised.Count, first.NotRepeated, first.NewSessions, first.LatestSession));
        Assert.Equal(
            (0, 6, 0, 0, 0, Day(6)),
            (again.Added, again.Unchanged, again.Revised.Count, again.NotRepeated, again.NewSessions, again.LatestSession));
        Assert.Equal(6, _store.Rows.Count);
        Assert.Equal([Day(1), Day(2), Day(5), Day(6)], _store.Sessions.Order());
        var close = _store.Rows.Single(r => r.Price.Instrument == Spy.Id && r.Price.Session == Day(5));
        Assert.Equal((new Price(774.83m, Currency.Usd), "yfinance", 0, _clock.GetCurrentInstant()),
            (close.Price.Close, close.Price.Source, close.Revision, close.ImportedAt));
        // One write, one note and one mark of the export for the import that did something; none for the other.
        var note = Assert.Single(_store.Notes);
        Assert.Equal((PricesNote.Imported, "prices"), (note.Kind, note.Actor));
        Assert.Contains("\"sha256\":\"0f0f\"", note.Payload, StringComparison.Ordinal);
        Assert.Equal(new ImportedExport(Instant.FromUtc(2026, 10, 7, 6, 0), "0f0f"), Assert.Single(_store.Imports));
    }

    [Fact]
    public async Task TickersOutsideTheCatalogueAreSkippedAndInstrumentsWithoutPricesAreNamed()
    {
        var result = await ImportAsync(Export(("SPY", 6, 779.09m), ("VWO", 6, 55.10m), ("ILS=X", 6, 3.05m), ("spy", 5, 774.83m)));

        Assert.Equal(2, result.Added); // "spy" is SPY: tickers are matched whatever their case
        Assert.Equal(["ILS=X", "VWO"], result.UnknownTickers);
        Assert.Equal(["XLC", "XLK"], result.WithoutPrices);
        Assert.All(_store.Rows, r => Assert.Equal(Spy.Id, r.Price.Instrument));
    }

    [Fact]
    public async Task SmallCorrectionIsRecordedNextToTheOldPriceAndNamed()
    {
        await ImportAsync(Week());

        // The vendor fixes Monday's close of SPY by 45 cents, and a new session arrives.
        var lines = WeekLines.Select(l => l is ("SPY", 5, _) ? ("SPY", 5, 774.38m) : l).ToArray();
        _clock.Reset(Instant.FromUtc(2026, 10, 8, 14, 0));
        var result = await ImportAsync(Export([.. lines, ("SPY", 7, 781.00m)], through: 7));

        var change = Assert.Single(result.Revised);
        Assert.Equal(("SPY", Day(5), 774.83m, 774.38m), (change.Ticker, change.Stored.Session, change.Stored.Close.Value, change.Stated.Close.Value));
        Assert.Equal((1, 5, 1, Day(7)), (result.Added, result.Unchanged, result.NewSessions, result.LatestSession));
        Assert.Equal(
            [(774.83m, 0), (774.38m, 1)],
            _store.Rows.Where(r => r.Price.Instrument == Spy.Id && r.Price.Session == Day(5)).Select(r => (r.Price.Close.Value, r.Revision)));
        Assert.Equal(774.38m, (await OnAsync(Spy, Day(5))).Price!.Close.Value);
        Assert.Equal((PricesNote.Noted, "prices"), (_store.Notes[^1].Kind, _store.Notes[^1].Actor));
        Assert.Contains("SPY 2026-10-05: 774.83 stored, 774.38 stated", _store.Notes[^1].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyChangedPricesAreNotTakenWithoutTheOwner()
    {
        // 21 sessions stored; then an export that states every one of them a cent differently - as a vendor
        // re-basing its history would. Each change is tiny; together they are not a corrected tick.
        var days = Enumerable.Range(0, ClosingPrices.MostRevisionsTakenUnasked + 1).Select(i => new LocalDate(2026, 8, 3).PlusDays(i)).ToList();
        var fetched = Instant.FromUtc(2026, 10, 7, 3, 0);
        ClosingPricesExport August(Func<int, decimal> close) => new(
            "yfinance", "0f0f", fetched, ClosingPrices.Calendar, days,
            new Dictionary<string, ExportedTicker> { ["XLK"] = new(fetched, "USD", new HashSet<LocalDate>()) },
            days.Select((day, i) => new ClosingPriceLine("XLK", day, close(i))).ToList(), []);
        await ImportAsync(August(i => 300m + i));

        var refused = await Assert.ThrowsAsync<PricesDisagreeException>(() => ImportAsync(August(i => 300.01m + i)));

        Assert.Contains("states 21 stored closing price(s) differently", refused.Message, StringComparison.Ordinal);
        Assert.Contains("XLK 2026-08-03: 300 stored, 300.01 stated", refused.Message, StringComparison.Ordinal);
        Assert.Matches("^[0-9A-F]{8}$", refused.Code);
        Assert.Contains($"(acceptance code {refused.Code})", refused.Message, StringComparison.Ordinal);
        Assert.Equal(21, _store.Rows.Count);

        // Another set of changes is another code; a code for something else accepts nothing.
        var other = await Assert.ThrowsAsync<PricesDisagreeException>(() => ImportAsync(August(i => 300.02m + i), refused.Code));
        Assert.NotEqual(refused.Code, other.Code);
        Assert.Equal(21, _store.Rows.Count);

        var accepted = await ImportAsync(August(i => 300.01m + i), refused.Code.ToLowerInvariant());
        Assert.Equal(21, accepted.Revised.Count);
        Assert.Equal(42, _store.Rows.Count);
        Assert.Equal(("owner", PricesNote.Noted), (_store.Notes[^1].Actor, _store.Notes[^1].Kind));
        Assert.Contains(refused.Code, _store.Notes[^1].Payload, StringComparison.Ordinal);

        // With nothing to accept, an acceptance is refused; twenty small changes are still taken unasked.
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(August(i => 300.01m + i), refused.Code));
        var twenty = await ImportAsync(August(i => i == 0 ? 300.01m : 300.03m + i));
        Assert.Equal(ClosingPrices.MostRevisionsTakenUnasked, twenty.Revised.Count);
        Assert.Equal("prices", _store.Notes[^1].Actor);
    }

    [Theory]
    [InlineData(790.33, false)] // 2.00004% above the stored 774.83
    [InlineData(790.32, true)]
    [InlineData(759.33, false)] // as far below
    [InlineData(759.34, true)]
    public async Task OneStoredPriceRestatedByMoreThanACorrectionIsNotTakenUnasked(double stated, bool taken)
    {
        await ImportAsync(Week());
        var restated = Export(WeekLines.Select(l => l is ("SPY", 5, _) ? ("SPY", 5, (decimal)stated) : l).ToArray());

        if (taken)
        {
            Assert.Single((await ImportAsync(restated)).Revised);
            return;
        }

        var refused = await Assert.ThrowsAsync<PricesDisagreeException>(() => ImportAsync(restated));
        Assert.Contains("states 1 stored closing price(s) differently", refused.Message, StringComparison.Ordinal);
        Assert.Contains("within 2%", refused.Message, StringComparison.Ordinal);
        Assert.Equal(6, _store.Rows.Count);
        Assert.Single((await ImportAsync(restated, refused.Code)).Revised);
        Assert.Equal("owner", _store.Notes[^1].Actor);
    }

    [Fact]
    public async Task NewCloseFarFromTheOneBeforeItIsNotTakenUnasked()
    {
        await ImportAsync(Week()); // SPY closed Tuesday at 779.09

        // 30.0004% up, and as far down: more likely a wrong number than a price.
        var up = await Assert.ThrowsAsync<PricesDisagreeException>(() => ImportAsync(WeekAnd(("SPY", 7, 1012.82m))));
        Assert.Contains("1 new close(s) move more than 30% from the close before", up.Message, StringComparison.Ordinal);
        Assert.Contains("SPY 2026-10-07: 1012.82 after 779.09 on 2026-10-06", up.Message, StringComparison.Ordinal);
        var down = await Assert.ThrowsAsync<PricesDisagreeException>(() => ImportAsync(WeekAnd(("SPY", 7, 545.36m))));
        Assert.NotEqual(up.Code, down.Code);
        Assert.Equal(6, _store.Rows.Count);

        // The close before may be a stored one the export does not repeat.
        await Assert.ThrowsAsync<PricesDisagreeException>(() => ImportAsync(Export([("XLC", 7, 200m)], through: 7)));

        // On the day of a split the price rightly halves: the export names the day, and nothing is asked.
        var split = await ImportAsync(Export([.. WeekLines, ("SPY", 7, 389.55m)], through: 7, splits: [("SPY", 7)]));
        Assert.Equal((1, 0), (split.Added, split.Unusual.Count));
        // Just inside the usual: taken.
        Assert.Equal(1, (await ImportAsync(Export([.. WeekLines, ("SPY", 7, 389.55m), ("XLK", 7, 395.46m)], through: 7, splits: [("SPY", 7)]))).Added);

        // What the owner looked up and accepts is stored and noted as theirs.
        var lines = WeekLines.Append(("SPY", 7, 389.55m)).Append(("XLK", 7, 395.46m)).Append(("XLC", 7, 200m)).ToArray();
        var refused = await Assert.ThrowsAsync<PricesDisagreeException>(() => ImportAsync(Export(lines, through: 7, splits: [("SPY", 7)])));
        var accepted = await ImportAsync(Export(lines, through: 7, splits: [("SPY", 7)]), refused.Code);
        var unusual = Assert.Single(accepted.Unusual);
        Assert.Equal(("XLC", Day(7), 200m, Day(6), 110.05m),
            (unusual.Ticker, unusual.Close.Session, unusual.Close.Close.Value, unusual.Before.Session, unusual.Before.Close.Value));
        Assert.Equal(("owner", PricesNote.Noted), (_store.Notes[^1].Actor, _store.Notes[^1].Kind));
        Assert.Contains("1 unusual new close(s) accepted, e.g. XLC 2026-10-07: 200 after 110.05 on 2026-10-06", _store.Notes[^1].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportOlderThanTheOneImportedLastIsRefused()
    {
        await ImportAsync(Week()); // written on 7 October at 06:00 UTC
        var corrected = WeekLines.Select(l => l is ("SPY", 5, _) ? ("SPY", 5, 774.38m) : l).ToArray();

        // It would put a price back that a later export corrected.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImportAsync(Export(corrected, exportedAt: Instant.FromUtc(2026, 10, 7, 5, 59))));

        Assert.Contains("written on 2026-10-07 05:59 UTC, before the one imported last (2026-10-07 06:00 UTC)", refused.Message, StringComparison.Ordinal);
        Assert.Equal(6, _store.Rows.Count);
        Assert.Single((await ImportAsync(Export(corrected, exportedAt: Instant.FromUtc(2026, 10, 7, 6, 0)))).Revised);
    }

    [Fact]
    public async Task OnlyTheCloseOfAFinishedSessionIsTaken()
    {
        // A Saturday: not a session of the export.
        var saturday = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export(("SPY", 2, 769.64m), ("SPY", 3, 770m))));
        Assert.Contains("a close of SPY for 2026-10-03, which is not one of its finished sessions", saturday.Message, StringComparison.Ordinal);

        // A session of tomorrow: today in New York is Wednesday the 7th.
        var tomorrow = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export([("SPY", 6, 779.09m)], through: 8)));
        Assert.Contains("2026-10-08 as a finished session, and today in New York is 2026-10-07", tomorrow.Message, StringComparison.Ordinal);

        // Fetched a minute before Tuesday's close in New York: that bar is the last trade so far.
        var open = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImportAsync(Export([("SPY", 5, 774.83m), ("SPY", 6, 778.40m)], fetchedAt: Instant.FromUtc(2026, 10, 6, 19, 59))));
        Assert.Contains(
            "a close of SPY for 2026-10-06, and its data was fetched at 2026-10-06 15:59 in New York, before that session ended",
            open.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export([("SPY", 6, 779.09m)], sessions: [Day(6), Day(5)])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export([("SPY", 6, 779.09m)], sessions: [Day(6), Day(6)])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export([("SPY", 6, 779.09m)], calendar: "XLON")));
        Assert.Empty(_store.Rows);
        Assert.Empty(_store.Sessions);

        // At the bell it is a close.
        Assert.Equal(2, (await ImportAsync(Export([("SPY", 5, 774.83m), ("SPY", 6, 779.09m)], fetchedAt: Instant.FromUtc(2026, 10, 6, 20, 0)))).Added);
    }

    [Fact]
    public async Task PricesInAnotherCurrencyThanTheInstrumentsAreRefused()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export([("SPY", 6, 779.09m)], currency: "EUR")));

        Assert.Contains("prices of SPY are in EUR; the instrument is kept in USD", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_store.Rows);
        // An export that does not know the currency is taken as the instrument's own.
        Assert.Equal(1, (await ImportAsync(Export([("SPY", 6, 779.09m)], currency: null))).Added);

        // The price is in the instrument's currency, whichever that is - not in dollars by habit.
        var shekels = new Instrument(InstrumentId.New(), new Ticker("TA35"), "TASE", Currency.Ils, "an index fund in shekels");
        var prices = new ClosingPrices(_store, new Catalogue(Spy, shekels), _clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            prices.ImportAsync(Export([("TA35", 6, 2950.10m)], currency: "USD"), null, CancellationToken.None));
        await prices.ImportAsync(Export([("TA35", 6, 2950.10m)], currency: "ILS"), null, CancellationToken.None);
        Assert.Equal(new Price(2950.10m, Currency.Ils), _store.Rows.Single(r => r.Price.Instrument == shekels.Id).Price.Close);
    }

    [Fact]
    public async Task SessionsThatContradictTheStoredOnesAreRefused()
    {
        await ImportAsync(Week()); // sessions 1, 2, 5 and 6 October

        var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export(WeekLines.Where(l => l.Day != 5).ToArray(), sessions: [Day(1), Day(2), Day(6)])));
        Assert.Contains("2026-10-05 is stored as a session of XNYS and the export does not list it", gone.Message, StringComparison.Ordinal);
        var extra = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export(WeekLines, sessions: [Day(1), Day(2), Day(3), Day(5), Day(6)])));
        Assert.Contains("The export lists 2026-10-03 as a session of XNYS, and the stored sessions around it do not", extra.Message, StringComparison.Ordinal);
        Assert.Equal(4, _store.Sessions.Count);

        // A stretch that reaches further back, or further on, adds its sessions.
        var earlier = await ImportAsync(Export(WeekLines, sessions: [new LocalDate(2026, 9, 30), Day(1), Day(2), Day(5), Day(6)]));
        Assert.Equal((1, 0), (earlier.NewSessions, earlier.Added));
        Assert.Equal(PricesNote.Imported, _store.Notes[^1].Kind);
        _clock.Reset(Instant.FromUtc(2026, 10, 8, 14, 0));
        Assert.Equal(1, (await ImportAsync(Export(WeekLines.Where(l => l.Day >= 5).ToArray(), through: 7, sessions: [Day(5), Day(6), Day(7)]))).NewSessions);
        Assert.Equal(6, _store.Sessions.Count);
    }

    [Fact]
    public async Task StoredPriceTheExportLeavesOutIsKeptAndCounted()
    {
        await ImportAsync(Week());

        var result = await ImportAsync(Export(("SPY", 2, 769.64m), ("SPY", 6, 779.09m), ("XLK", 2, 301.10m), ("XLK", 6, 304.20m)));

        Assert.Equal((0, 4, 1), (result.Added, result.Unchanged, result.NotRepeated)); // SPY's Monday; XLC is not in the export at all
        Assert.Equal(["XLC"], result.WithoutPrices);
        Assert.Equal(6, _store.Rows.Count);
        Assert.Equal(774.83m, (await OnAsync(Spy, Day(5))).Price!.Close.Value);
        Assert.Single(_store.Notes); // nothing was stored, so nothing is written about it - however often it is run
    }

    [Fact]
    public async Task InstrumentWhoseLastCloseIsBehindTheLatestSessionIsNamed()
    {
        var result = await ImportAsync(Export(("SPY", 5, 774.83m), ("SPY", 6, 779.09m), ("XLK", 2, 301.10m), ("XLK", 5, 303.00m)));

        Assert.Equal([("XLK", Day(5))], result.Behind);
        Assert.Equal(Day(6), result.LatestSession);
    }

    [Fact]
    public async Task DividendsAndSplitsOfTheFundsAreStoredWithThePrices()
    {
        (string, int, FundEventKind, decimal)[] happened = [("SPY", 5, FundEventKind.Dividend, 1.889m), ("XLK", 6, FundEventKind.Split, 2m), ("VWO", 6, FundEventKind.Dividend, 0.4m)];
        var first = await ImportAsync(Export([.. WeekLines, ("VWO", 6, 55.10m)], events: happened));
        var again = await ImportAsync(Export([.. WeekLines, ("VWO", 6, 55.10m)], events: happened));

        // VWO is not in the catalogue: neither its price nor its dividend is taken.
        Assert.Equal((2, 0, 0, 0), (first.EventsAdded, first.EventsRevised.Count, again.EventsAdded, again.EventsRevised.Count));
        Assert.Empty(again.EventsNotRepeated);
        Assert.Equal(
            [(Spy.Id, Day(5), FundEventKind.Dividend, 1.889m, "yfinance", 0), (Xlk.Id, Day(6), FundEventKind.Split, 2m, "yfinance", 0)],
            _store.Events.Select(e => (e.Event.Instrument, e.Event.Day, e.Event.Kind, e.Event.Value, e.Event.Source, e.Revision)));
        Assert.Single(_store.Notes); // the second import had nothing to store
        Assert.Contains("\"eventsAdded\":2", _store.Notes[0].Payload, StringComparison.Ordinal);

        // What a holder was dealt between two evenings: after the first, up to and including the second.
        Assert.Equal(1.889m, Assert.Single(await _prices.EventsAsync(Spy.Id, Day(2), Day(5), CancellationToken.None)).Value);
        Assert.Empty(await _prices.EventsAsync(Spy.Id, Day(5), Day(6), CancellationToken.None));
        Assert.Empty(await _prices.EventsAsync(Xlc.Id, Day(1), Day(6), CancellationToken.None));

        // A new event alone is stored too, though every price is known.
        var later = await ImportAsync(Export(WeekLines, events: [.. happened[..2], ("XLC", 6, FundEventKind.Dividend, 0.25m)]));
        Assert.Equal((0, 1, 2), (later.Added, later.EventsAdded, _store.Notes.Count));
    }

    [Fact]
    public async Task RestatedFundEventIsRecordedNextToTheOldOneAndNamed()
    {
        await ImportAsync(Export(WeekLines, events: [("SPY", 5, FundEventKind.Dividend, 1.889m), ("XLK", 6, FundEventKind.Split, 2m)]));

        // The vendor now says the split was 3:1.
        var result = await ImportAsync(Export(WeekLines, events: [("SPY", 5, FundEventKind.Dividend, 1.889m), ("XLK", 6, FundEventKind.Split, 3m)]));

        var change = Assert.Single(result.EventsRevised);
        Assert.Equal(("XLK", Day(6), FundEventKind.Split, 2m, 3m), (change.Ticker, change.Stored.Day, change.Stored.Kind, change.Stored.Value, change.Stated.Value));
        Assert.Equal((0, 0), (result.EventsAdded, result.Added));
        Assert.Equal([(2m, 0), (3m, 1)], _store.Events.Where(e => e.Event.Instrument == Xlk.Id).Select(e => (e.Event.Value, e.Revision)));
        Assert.Equal(3m, Assert.Single(await _prices.EventsAsync(Xlk.Id, Day(5), Day(6), CancellationToken.None)).Value);
        Assert.Equal((PricesNote.Noted, "prices"), (_store.Notes[^1].Kind, _store.Notes[^1].Actor));
        Assert.Contains("1 stored dividend(s) or split(s) revised, e.g. XLK 2026-10-06 split: 2 stored, 3 stated", _store.Notes[^1].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredFundEventTheExportNoLongerStatesIsKeptAndNamed()
    {
        await ImportAsync(Export(WeekLines, events: [("SPY", 5, FundEventKind.Dividend, 1.889m), ("XLK", 2, FundEventKind.Split, 2m), ("XLK", 6, FundEventKind.Dividend, 0.2m)]));

        // The vendor moves SPY's ex-date from Monday to Tuesday, and no longer knows XLK's split.
        var moved = await ImportAsync(Export(WeekLines, events: [("SPY", 6, FundEventKind.Dividend, 1.889m), ("XLK", 6, FundEventKind.Dividend, 0.2m)]));

        Assert.Equal(1, moved.EventsAdded);
        Assert.Equal(
            [("SPY", Day(5), FundEventKind.Dividend, 1.889m), ("XLK", Day(2), FundEventKind.Split, 2m)],
            moved.EventsNotRepeated.Select(e => (e.Ticker, e.Event.Day, e.Event.Kind, e.Event.Value)));
        // Both are still there: SPY's dividend now counts twice for whoever follows the fund, and that is said.
        Assert.Equal(2, (await _prices.EventsAsync(Spy.Id, Day(1), Day(6), CancellationToken.None)).Count);
        Assert.Contains("\"eventsNotRepeated\":[{\"ticker\":\"SPY\",\"day\":\"2026-10-05\",\"kind\":\"dividend\",\"value\":1.889}", _store.Notes[^1].Payload, StringComparison.Ordinal);

        // An export that covers a later stretch of a fund says nothing about the days before it: XLK's split
        // of Friday is not missed by an export that starts on Tuesday. Nothing is stored by it, either.
        var notes = _store.Notes.Count;
        var later = await ImportAsync(Export(
            WeekLines.Where(l => l.Day == 6).ToArray(), events: [("SPY", 6, FundEventKind.Dividend, 1.889m), ("XLK", 6, FundEventKind.Dividend, 0.2m)]));
        Assert.Empty(later.EventsNotRepeated);
        Assert.Equal(notes, _store.Notes.Count);
        // Nor does an export that ends on Monday say anything about Tuesday: only what lies inside its days is missed.
        var earlier = await ImportAsync(Export(WeekLines.Where(l => l.Day <= 5).ToArray()));
        Assert.Equal([("SPY", Day(5)), ("XLK", Day(2))], earlier.EventsNotRepeated.Select(e => (e.Ticker, e.Event.Day)));
    }

    [Fact]
    public async Task FundEventThatCannotBeOneIsRefusedWithTheWholeExport()
    {
        // On a Saturday; the same one twice; finer than is kept; nothing.
        var saturday = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export(WeekLines, events: [("SPY", 3, FundEventKind.Dividend, 1.889m)])));
        Assert.Contains("a dividend of SPY for 2026-10-03, which is not one of its finished sessions", saturday.Message, StringComparison.Ordinal);
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImportAsync(Export(WeekLines, events: [("XLK", 6, FundEventKind.Split, 2m), ("XLK", 6, FundEventKind.Split, 3m)])));
        Assert.Contains("two splits of XLK for 2026-10-06", twice.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => ImportAsync(Export(WeekLines, events: [("SPY", 5, FundEventKind.Dividend, 1.8890001m)])));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ImportAsync(Export(WeekLines, events: [("SPY", 5, FundEventKind.Dividend, 0m)])));

        Assert.Empty(_store.Rows);
        Assert.Empty(_store.Events);
        // A dividend and a split on one day are two events.
        Assert.Equal(2, (await ImportAsync(Export(WeekLines, events: [("XLK", 6, FundEventKind.Dividend, 0.1m), ("XLK", 6, FundEventKind.Split, 2m)]))).EventsAdded);
    }

    [Fact]
    public async Task ExportThatContradictsItselfIsRefusedWhole()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(Export(("SPY", 5, 774.83m), ("SPY", 5, 774.84m))));
        await Assert.ThrowsAsync<ArgumentException>(() => ImportAsync(Export(("SPY", 5, 774.8300001m)))); // finer than is kept
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ImportAsync(Export(("SPY", 5, 0m))));

        Assert.Empty(_store.Rows);
        var twins = new ClosingPrices(_store, new Catalogue(Spy, Etf("SPY")), _clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => twins.ImportAsync(Week(), null, CancellationToken.None));
    }

    [Fact]
    public async Task SessionHasItsOwnCloseAndAWeekendCarriesFridays()
    {
        await ImportAsync(Week());

        var monday = await OnAsync(Spy, Day(5));
        var saturday = await OnAsync(Spy, Day(3));
        var sunday = await OnAsync(Spy, Day(4));

        Assert.Equal((PriceAvailability.OfTheSession, 774.83m, 0, false), (monday.Availability, monday.Price!.Close.Value, monday.DaysCarried, monday.IsProvisional));
        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 769.64m, Day(2), 1), (saturday.Availability, saturday.Price!.Close.Value, saturday.Price.Session, saturday.DaysCarried));
        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 2), (sunday.Availability, sunday.DaysCarried));
        // The calendar says the exchange was closed on both days: nothing about these figures will change.
        Assert.False(saturday.IsProvisional);
        Assert.False(sunday.IsProvisional);
    }

    [Fact]
    public async Task GapInTheDataIsSaidAndNotFilledWithAnOlderPrice()
    {
        await ImportAsync(Week());

        // Monday the 5th was a session, and XLK has no close for it.
        var monday = await OnAsync(Xlk, Day(5));

        Assert.Equal((PriceAvailability.MissingForTheSession, false, null), (monday.Availability, monday.HasPrice, monday.Price));
        Assert.Equal(304.20m, (await OnAsync(Xlk, Day(6))).Price!.Close.Value); // the next session is fine again
    }

    [Fact]
    public async Task SessionNobodyHasAPriceForIsAGapAndAHolidayIsNot()
    {
        // The vendor gave nobody a price for Monday the 5th. Whether it was a session is the calendar's to
        // say, not the prices': it was, so Friday's close is not quietly used for it.
        await ImportAsync(Export(("SPY", 2, 769.64m), ("SPY", 6, 779.09m), ("XLK", 2, 301.10m), ("XLK", 6, 304.20m)));

        Assert.Equal(PriceAvailability.MissingForTheSession, (await OnAsync(Spy, Day(5))).Availability);
        Assert.Equal(PriceAvailability.MissingForTheSession, (await OnAsync(Xlk, Day(5))).Availability);

        // The same prices where the calendar says the exchange was closed that Monday.
        var holiday = new ClosingPrices(new MemoryStore(), new Catalogue(Spy, Xlk, Xlc), _clock);
        await holiday.ImportAsync(
            Export([("SPY", 2, 769.64m), ("SPY", 6, 779.09m)], sessions: [Day(1), Day(2), Day(6)]), null, CancellationToken.None);
        var monday = await holiday.OnAsync(Spy.Id, Day(5), CancellationToken.None);
        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 769.64m, 3, false), (monday.Availability, monday.Price!.Close.Value, monday.DaysCarried, monday.IsProvisional));
    }

    [Fact]
    public async Task CloseIsCarriedOverHoweverLongTheCalendarSaysTheExchangeWasClosed()
    {
        // The exchange stayed closed from Monday to Thursday - as it did for six days in September 2001. The
        // calendar says so, so Friday's close is the price of every one of those days, and nothing about it
        // is provisional. (Only past the last stored session is a close carried for a few days at most.)
        _clock.Reset(Instant.FromUtc(2026, 10, 12, 14, 0));
        await ImportAsync(Export([("SPY", 2, 769.64m), ("SPY", 9, 775m)], through: 9, sessions: [Day(1), Day(2), Day(9)]));

        var thursday = await OnAsync(Spy, Day(8));

        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 769.64m, 6, false), (thursday.Availability, thursday.Price!.Close.Value, thursday.DaysCarried, thursday.IsProvisional));
    }

    [Fact]
    public async Task GapOnTheLastSessionBeforeAClosedDayIsNotHiddenByCarrying()
    {
        // XLK has Thursday's close and not Friday's. On Saturday the last session was Friday - with no price
        // for XLK; Thursday's close is not carried across it.
        await ImportAsync(Export(("SPY", 1, 760m), ("SPY", 2, 769.64m), ("SPY", 5, 774.83m), ("XLK", 1, 300m), ("XLK", 5, 303m)));

        Assert.Equal(PriceAvailability.MissingForTheSession, (await OnAsync(Xlk, Day(3))).Availability);
        Assert.Equal(PriceAvailability.MissingForTheSession, (await OnAsync(Xlk, Day(2))).Availability);
        Assert.Equal(PriceAvailability.CarriedOverAClosedDay, (await OnAsync(Spy, Day(3))).Availability);
    }

    [Fact]
    public async Task PriceCarriedPastTheLastStoredSessionIsProvisional()
    {
        // Wednesday morning in New York, sessions and prices up to Tuesday: today may be a session whose
        // close is not in yet.
        await ImportAsync(Export(("SPY", 5, 774.83m), ("SPY", 6, 779.09m), ("XLK", 2, 301.10m), ("XLK", 5, 303.00m)));

        var today = await OnAsync(Spy, Day(7));

        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 779.09m, 1, true), (today.Availability, today.Price!.Close.Value, today.DaysCarried, today.IsProvisional));
        // XLK has no close of that last session: Monday's is not carried across the gap.
        Assert.Equal(PriceAvailability.MissingForTheSession, (await OnAsync(Xlk, Day(7))).Availability);

        _clock.Reset(Instant.FromUtc(2026, 10, 8, 14, 0));
        await ImportAsync(Export([("SPY", 5, 774.83m), ("SPY", 6, 779.09m), ("SPY", 7, 781m)], through: 7));
        var known = await OnAsync(Spy, Day(7));
        Assert.Equal((PriceAvailability.OfTheSession, 781m, false), (known.Availability, known.Price!.Close.Value, known.IsProvisional));
    }

    [Fact]
    public async Task NoPriceForTomorrowNorBeforeTheFirstOneNorFarPastTheLastStoredSession()
    {
        await ImportAsync(Week());

        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Spy, Day(8))).Availability); // tomorrow in New York
        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Xlc, Day(5))).Availability); // a session before its first close
        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Xlc, Day(3))).Availability);
        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Spy, Day(1))).Availability);
        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Spy, new LocalDate(2026, 9, 30))).Availability); // before any stored session
        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Etf("VT"), Day(6))).Availability); // not priced at all

        // Nobody loaded prices for two weeks. Tuesday's close serves for a long weekend and no further: sessions
        // have passed since, and nothing stored says what happened on them.
        _clock.AdvanceDays(14);
        var lastUsable = await OnAsync(Spy, Day(6).PlusDays(ClosingPrices.MostDaysCarriedUnconfirmed));
        Assert.Equal((PriceAvailability.CarriedOverAClosedDay, 4, true), (lastUsable.Availability, lastUsable.DaysCarried, lastUsable.IsProvisional));
        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Spy, Day(6).PlusDays(ClosingPrices.MostDaysCarriedUnconfirmed + 1))).Availability);
        // What is stored stays what it was.
        Assert.Equal(PriceAvailability.OfTheSession, (await OnAsync(Spy, Day(6))).Availability);
        Assert.False((await OnAsync(Spy, Day(4))).IsProvisional);
    }

    [Fact]
    public async Task TodayIsTheDateInNewYork()
    {
        await ImportAsync(Week());
        _clock.Reset(Instant.FromUtc(2026, 10, 8, 3, 0)); // Thursday morning in Israel, still Wednesday evening in New York

        Assert.Equal(PriceAvailability.CarriedOverAClosedDay, (await OnAsync(Spy, Day(7))).Availability);
        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Spy, Day(8))).Availability);
    }

    [Fact]
    public async Task PricesStoredWithoutTheirSessionsServeOnlyTheirOwnDay()
    {
        // Prices loaded before the sessions were kept: nothing says which days the exchange was closed.
        _store.Rows.Add((new ClosingPrice(Spy.Id, Day(2), new Price(769.64m, Currency.Usd), "yfinance"), 0, _clock.GetCurrentInstant()));

        Assert.Equal(PriceAvailability.OfTheSession, (await OnAsync(Spy, Day(2))).Availability);
        Assert.Equal(PriceAvailability.Unavailable, (await OnAsync(Spy, Day(3))).Availability);

        // The next import brings the sessions, and changes no price.
        var result = await ImportAsync(Export(("SPY", 2, 769.64m)));
        Assert.Equal((0, 1, 4), (result.Added, result.Unchanged, result.NewSessions));
        Assert.Equal(PriceAvailability.CarriedOverAClosedDay, (await OnAsync(Spy, Day(3))).Availability);
    }

    [Fact]
    public void PriceOnADateIsOnlyWhatItSaysItIs()
    {
        var friday = new ClosingPrice(Spy.Id, Day(2), new Price(769.64m, Currency.Usd), "yfinance");

        Assert.Throws<ArgumentException>(() => PriceOnDate.Carried(friday, Day(2), false)); // the session's own close is not "carried"
        Assert.Throws<ArgumentException>(() => PriceOnDate.Carried(friday, Day(1), false));
        Assert.Throws<ArgumentException>(() => new ClosingPrice(Spy.Id, Day(2), new Price(1.0000001m, Currency.Usd), "yfinance"));
        Assert.Throws<ArgumentException>(() => new ClosingPrice(Spy.Id, Day(2), new Price(1m, Currency.Usd), " "));
        Assert.Throws<ArgumentException>(() => new ClosingPrice(default, Day(2), new Price(1m, Currency.Usd), "yfinance"));
        Assert.Equal((Day(2), 0, true), (PriceOnDate.OfTheSession(friday).Date, PriceOnDate.OfTheSession(friday).DaysCarried, PriceOnDate.OfTheSession(friday).HasPrice));
        Assert.Equal((false, 0), (PriceOnDate.Missing(Spy.Id, Day(5)).HasPrice, PriceOnDate.Missing(Spy.Id, Day(5)).DaysCarried));
    }

    /// <summary>The instruments the import matches tickers against.</summary>
    private sealed class Catalogue(params Instrument[] instruments) : IInstrumentStore
    {
        public Task<IReadOnlyList<Instrument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Instrument>>(instruments);

        public Task<Instrument?> FindAsync(InstrumentId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Instrument?> FindByConIdAsync(int ibkrConId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Instrument?> FindByTickerAsync(Ticker ticker, string exchange, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Instrument?> FindByTickerOnAsync(Ticker ticker, string exchange, LocalDate day, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TickerHistory?> TickersOfAsync(InstrumentId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddAsync(Instrument instrument, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateDetailsAsync(Instrument instrument, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RenameAsync(InstrumentId id, Ticker newTicker, LocalDate effective, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// The store's contract, in memory: rows are only added, the latest revision of a session is in use, and a
    /// close belongs to a stored session.
    /// </summary>
    private sealed class MemoryStore : IClosingPriceStore
    {
        public List<(ClosingPrice Price, int Revision, Instant ImportedAt)> Rows { get; } = [];

        public HashSet<LocalDate> Sessions { get; } = [];

        public List<PricesNote> Notes { get; } = [];

        public List<ImportedExport> Imports { get; } = [];

        private IEnumerable<ClosingPrice> InUse =>
            Rows.GroupBy(r => (r.Price.Instrument, r.Price.Session)).Select(g => g.MaxBy(r => r.Revision).Price);

        public Task<IReadOnlyList<ClosingPrice>> ListAsync(InstrumentId instrument, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClosingPrice>>(InUse.Where(p => p.Instrument == instrument).OrderBy(p => p.Session).ToList());

        public Task<ClosingPrice?> FindAsync(InstrumentId instrument, LocalDate session, CancellationToken cancellationToken) =>
            Task.FromResult(InUse.SingleOrDefault(p => p.Instrument == instrument && p.Session == session));

        public Task<ClosingPrice?> LatestBeforeAsync(InstrumentId instrument, LocalDate day, CancellationToken cancellationToken) =>
            Task.FromResult(InUse.Where(p => p.Instrument == instrument && p.Session < day).MaxBy(p => p.Session));

        public Task<IReadOnlyDictionary<InstrumentId, LocalDate>> LatestPerInstrumentAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<InstrumentId, LocalDate>>(
                Rows.GroupBy(r => r.Price.Instrument).ToDictionary(g => g.Key, g => g.Max(r => r.Price.Session)));

        public Task<IReadOnlyList<LocalDate>> ListSessionsAsync(string calendar, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalDate>>(calendar == ClosingPrices.Calendar ? Sessions.Order().ToList() : []);

        public Task<LocalDate?> LatestSessionOnOrBeforeAsync(string calendar, LocalDate day, CancellationToken cancellationToken) =>
            Task.FromResult(calendar == ClosingPrices.Calendar ? Sessions.Where(s => s <= day).Select(s => (LocalDate?)s).Max() : null);

        public Task<ImportedExport?> LastImportAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Imports.MaxBy(i => i.ExportedAt));

        public List<(FundEvent Event, int Revision)> Events { get; } = [];

        public Task<IReadOnlyList<FundEvent>> ListEventsAsync(InstrumentId instrument, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FundEvent>>(
                Events.Where(e => e.Event.Instrument == instrument)
                    .GroupBy(e => (e.Event.Day, e.Event.Kind)).Select(g => g.MaxBy(e => e.Revision).Event)
                    .OrderBy(e => e.Day).ThenBy(e => e.Kind).ToList());

        public Task AddAsync(
            PricesToStore what, Instant importedAt, PricesNote note, ImportedExport importedExport, CancellationToken cancellationToken)
        {
            var (added, revised, sessions) = (what.Added, what.Revised, what.Sessions);
            bool Happened(FundEvent e) => Events.Exists(s => s.Event.Instrument == e.Instrument && s.Event.Day == e.Day && s.Event.Kind == e.Kind);
            if (what.Events.Any(Happened) || !what.RevisedEvents.All(Happened)
                || what.Events.Concat(what.RevisedEvents).Any(e => !Sessions.Contains(e.Day) && !sessions.Contains(e.Day)))
            {
                throw new InvalidOperationException("A fund event stored twice, a revision of none, or one on a day that is no session.");
            }

            Events.AddRange(what.Events.Select(e => (e, 0)));
            Events.AddRange(what.RevisedEvents.Select(e => (e, Events.Where(s => s.Event.Instrument == e.Instrument && s.Event.Day == e.Day && s.Event.Kind == e.Kind).Max(s => s.Revision) + 1)));
            bool Stored(ClosingPrice p) => Rows.Exists(r => r.Price.Instrument == p.Instrument && r.Price.Session == p.Session);
            if (added.Any(Stored) || !revised.All(Stored))
            {
                throw new InvalidOperationException("A first close for a session that has one, or a revision of nothing.");
            }

            if (sessions.Any(Sessions.Contains) || added.Any(p => !Sessions.Contains(p.Session) && !sessions.Contains(p.Session)))
            {
                throw new InvalidOperationException("A session stored twice, or a close of a day that is no session.");
            }

            Sessions.UnionWith(sessions);
            Rows.AddRange(added.Select(p => (p, 0, importedAt)));
            foreach (var price in revised)
            {
                var latest = Rows.Where(r => r.Price.Instrument == price.Instrument && r.Price.Session == price.Session).Max(r => r.Revision);
                Rows.Add((price, latest + 1, importedAt));
            }

            Notes.Add(note);
            Imports.Add(importedExport);
            return Task.CompletedTask;
        }
    }
}
