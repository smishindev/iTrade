using System.Globalization;
using CsCheck;
using ITrade.Application.Fx;
using ITrade.Domain;
using ITrade.Infrastructure.Fx;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.UnitTests.Application;

/// <summary>
/// P3.3.01: the official exchange rates — loaded once from the start of the history, kept up to date without
/// ever overwriting a stored rate, and carried over weekends and holidays with a mark.
/// October 2026: the 9th is a Friday, the 12th a Monday; the source has no rate on Saturday and Sunday.
/// </summary>
public sealed class FxRatesTests
{
    private const string Boi = "BOI representative";

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 10, 12, 16, 0)); // 19:00 in Israel
    private readonly FakeSource _source = new();
    private readonly MemoryStore _store = new();
    private readonly FxRates _rates;

    public FxRatesTests()
    {
        _rates = new FxRates(_source, _store, _clock);
        _source.Publish((2005, 1, 3, 4.356m), (2026, 10, 7, 3.074m), (2026, 10, 8, 3.084m), (2026, 10, 9, 3.055m));
    }

    private static LocalDate Day(int day) => new(2026, 10, day);

    private Task<FxRatesUpdate> UpdateAsync(FxAcceptance? accepted = null) =>
        _rates.UpdateAsync(accepted, CancellationToken.None);

    /// <summary>The plain update is refused; this is the code its message carries.</summary>
    private async Task<string> RefusedCodeAsync()
    {
        var refusal = await Assert.ThrowsAsync<FxRatesDisagreeException>(() => UpdateAsync());
        Assert.Matches("^[0-9A-F]{8}$", refusal.Code);
        Assert.Contains($"(acceptance code {refusal.Code})", refusal.Message, StringComparison.Ordinal);
        return refusal.Code;
    }

    private Task<AppliedFxRate> OnAsync(LocalDate date) => _rates.OnAsync(date, CancellationToken.None);

    private decimal Stored(int day) => _store.InUse.Single(r => r.Date == Day(day)).Rate;

    [Fact]
    public async Task FirstUpdateLoadsTheHistoryFromItsStart()
    {
        var update = await UpdateAsync();

        Assert.Equal((4, 0, Day(9)), (update.Added, update.Compared, update.Latest));
        Assert.Equal((FxRates.HistoryStart, Day(12)), Assert.Single(_source.Asked));
        Assert.Equal([4.356m, 3.074m, 3.084m, 3.055m], _store.InUse.Select(r => r.Rate));
        Assert.All(_store.Rows, r => Assert.Equal(
            (Currency.Usd, Currency.Ils, Boi, 0, null, _clock.GetCurrentInstant()),
            (r.Rate.From, r.Rate.To, r.Rate.Source, r.Revision, r.Reason, r.FetchedAt)));
    }

    [Fact]
    public async Task UpdatingAgainAddsNothingAndReadsOnlyTheLastDaysAgain()
    {
        await UpdateAsync();

        var again = await UpdateAsync();

        Assert.Equal((0, 3, Day(9)), (again.Added, again.Compared, again.Latest)); // the three rates of the last two weeks
        Assert.Equal((Day(9).PlusDays(-FxRates.OverlapDays), Day(12)), _source.Asked[1]);
        Assert.Equal(4, _store.Rows.Count);
        Assert.Equal(1, _store.Batches);
    }

    [Fact]
    public async Task NewRatesAreAddedAsTheyArePublishedAndALateOneForAnEarlierDayIsNamed()
    {
        _source.Withdraw(Day(8)); // Thursday's rate is not out yet when Friday's already is
        await UpdateAsync();
        Assert.Equal(3.074m, (await OnAsync(Day(8))).Rate.Rate); // so Thursday uses Wednesday's, carried
        _source.Publish((2026, 10, 8, 3.084m), (2026, 10, 12, 3.041m));
        _clock.AdvanceHours(1);

        var update = await UpdateAsync();

        Assert.Equal((2, 2, Day(12)), (update.Added, update.Compared, update.Latest));
        Assert.Equal(_clock.GetCurrentInstant(), _store.Rows.Single(r => r.Rate.Date == Day(12)).FetchedAt);
        // The figure of that Thursday has just changed, and the update says so.
        Assert.Equal((Day(8), 3.084m), (Assert.Single(update.Late).Date, update.Late[0].Rate));
        Assert.Equal(3.084m, (await OnAsync(Day(8))).Rate.Rate);
    }

    [Fact]
    public async Task TodayIsTheDateInIsraelNotInUtc()
    {
        _clock.Reset(Instant.FromUtc(2026, 10, 12, 21, 30)); // already the 13th in Israel

        await UpdateAsync();

        Assert.Equal(Day(13), Assert.Single(_source.Asked).Last);
    }

    [Fact]
    public async Task ChangedRateStopsTheUpdateAndIsNotStored()
    {
        await UpdateAsync();
        _source.Publish((2026, 10, 8, 3.085m), (2026, 10, 12, 3.041m)); // the source "corrects" Thursday

        var error = await Assert.ThrowsAsync<FxRatesDisagreeException>(() => UpdateAsync());

        Assert.Contains("now states 3.085 for 2026-10-08; 3.084 is stored", error.Message, StringComparison.Ordinal);
        Assert.Contains($"accept exactly this (acceptance code {error.Code})", error.Message, StringComparison.Ordinal);
        Assert.Equal(3.084m, Stored(8));
        Assert.DoesNotContain(_store.Rows, r => r.Rate.Date == Day(12)); // nothing was added either
    }

    [Fact]
    public async Task AcceptedChangeIsRecordedNextToTheOldRateAndTheUpdatesGoOn()
    {
        // Without a way to accept, one corrected rate would stop every later update for good.
        await UpdateAsync();
        _source.Publish((2026, 10, 8, 3.085m), (2026, 10, 12, 3.041m));
        var code = await RefusedCodeAsync();

        var accepted = await UpdateAsync(new FxAcceptance(code, "the bank's page shows 3.085 for the 8th"));

        var change = Assert.Single(accepted.Revised);
        Assert.Equal((Day(8), 3.084m, 3.085m), (change.Stored.Date, change.Stored.Rate, change.Stated.Rate));
        Assert.Equal((1, 2), (accepted.Added, accepted.Compared));
        Assert.Equal(3.085m, Stored(8));
        Assert.Equal(3.085m, (await OnAsync(Day(8))).Rate.Rate);
        // The old rate is still there, as the earlier revision; the new one carries the reason.
        Assert.Equal(
            [(3.084m, 0, null), (3.085m, 1, "the bank's page shows 3.085 for the 8th")],
            _store.Rows.Where(r => r.Rate.Date == Day(8)).Select(r => (r.Rate.Rate, r.Revision, r.Reason)));
        Assert.Null(_store.Rows.Single(r => r.Rate.Date == Day(12)).Reason); // Monday's rate needed nobody's acceptance

        var next = await UpdateAsync(); // and the daily update is back to normal
        Assert.Equal((0, 4, 0), (next.Added, next.Compared, next.Revised.Count));
    }

    [Fact]
    public async Task AcceptanceCoversExactlyWhatTheOwnerSawAndNothingElse()
    {
        await UpdateAsync();
        _source.Publish((2026, 10, 8, 3.085m));
        var seen = await RefusedCodeAsync();
        Assert.Equal(seen, await RefusedCodeAsync()); // the same finding has the same code

        // Between the message and the acceptance the bank publishes a slip for today: 3.550 for 3.055.
        _source.Publish((2026, 10, 12, 3.550m));
        var more = await Assert.ThrowsAsync<FxRatesDisagreeException>(() => UpdateAsync(new FxAcceptance(seen, "3.085 is on the bank's page")));
        Assert.Contains($"is not what was accepted with code {seen}", more.Message, StringComparison.Ordinal);
        Assert.Contains("the new rate 3.55 of 2026-10-12", more.Message, StringComparison.Ordinal);
        Assert.NotEqual(seen, more.Code);

        // Or the changed rate itself reads differently by then: 3.850, under a reason that says 3.085.
        _source.Withdraw(Day(12));
        _source.Publish((2026, 10, 8, 3.850m));
        var other = await Assert.ThrowsAsync<FxRatesDisagreeException>(() => UpdateAsync(new FxAcceptance(seen, "3.085 is on the bank's page")));
        Assert.Contains("now states 3.85 for 2026-10-08", other.Message, StringComparison.Ordinal);

        Assert.Equal(4, _store.Rows.Count); // nothing of it got in
        Assert.Equal(3.084m, Stored(8));

        // And with nothing to accept, an acceptance is refused: the flag cannot be passed by habit.
        _source.Publish((2026, 10, 8, 3.084m));
        var nothing = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(new FxAcceptance(seen, "habit")));
        Assert.Contains("There is nothing to accept", nothing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewRateThatMovesUnusuallyFarIsHeldBackUntilAccepted()
    {
        // The largest real move between two published rates since 2005 is 3.76%. 3.550 instead of 3.055 is a
        // slip of the keyboard - and once stored, a rate stays.
        await UpdateAsync();
        _source.Publish((2026, 10, 12, 3.550m));

        var error = await Assert.ThrowsAsync<FxRatesDisagreeException>(() => UpdateAsync());

        Assert.Contains("the new rate 3.55 of 2026-10-12 moves more than 8% from 3.055 of 2026-10-09", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_store.Rows, r => r.Rate.Date == Day(12));

        _source.Publish((2026, 10, 12, 3.290m)); // +7.7%: a wild day, and within what is let through
        Assert.Equal(1, (await UpdateAsync()).Added);

        _clock.AdvanceDays(1);
        _source.Publish((2026, 10, 13, 2.95m)); // -10.3%, and this time it is real: the owner accepts it
        var accepted = await UpdateAsync(new FxAcceptance(await RefusedCodeAsync(), "confirmed on the bank's page"));
        Assert.Equal((Day(13), 2.95m), (Assert.Single(accepted.Unusual).Date, accepted.Unusual[0].Rate));
        // The first rate of its date, not a revision - and it says why it was let in.
        var row = _store.Rows.Single(r => r.Rate.Date == Day(13));
        Assert.Equal((0, "confirmed on the bank's page"), (row.Revision, row.Reason));
        Assert.Equal(0, (await UpdateAsync()).Added); // accepted once, not asked about again
    }

    [Fact]
    public async Task TheWholeRealHistoryLoadsAndBearsOutTheLimits()
    {
        // The bank's own answer for 2005-01-01 … 2026-10-12, saved as it came (5332 rates).
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "boi-rer-usd-ils-2005-01-01_2026-10-12.csv");
        var history = BankOfIsraelRates.Parse(await File.ReadAllTextAsync(file));
        var source = new FakeSource();
        source.Publish([.. history.Select(r => (r.Date.Year, r.Date.Month, r.Date.Day, r.Rate))]);
        var store = new MemoryStore();

        var update = await new FxRates(source, store, _clock).UpdateAsync(null, CancellationToken.None);

        Assert.Equal((5332, new LocalDate(2026, 10, 9)), (update.Added, update.Latest));
        Assert.Equal((new LocalDate(2005, 1, 3), 4.356m), (history[0].Date, history[0].Rate));
        // What the constants in FxRates claim about this history.
        var steps = history.Zip(history.Skip(1)).ToList();
        Assert.Equal(7, steps.Max(s => Period.DaysBetween(s.First.Date, s.Second.Date))); // the longest silence
        Assert.Equal(FxRates.MaxDaysCarried, 7);
        var largest = steps.MaxBy(s => Math.Abs((s.Second.Rate / s.First.Rate) - 1m));
        Assert.Equal((new LocalDate(2020, 3, 18), 3.827m, new LocalDate(2020, 3, 19), 3.683m),
            (largest.First.Date, largest.First.Rate, largest.Second.Date, largest.Second.Rate)); // -3.76%
        Assert.True(Math.Abs((3.683m / 3.827m) - 1m) < FxRates.LargestUsualMove / 2m);
        Assert.All(history, r => Assert.Equal(r.Rate, decimal.Round(r.Rate, 3)));
        Assert.DoesNotContain(history, r => r.Date.DayOfWeek is IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday);
    }

    [Fact]
    public async Task AnswerThatLeavesDaysOutCannotCarryASlipPastTheCheck()
    {
        // The answer repeats only Wednesday and adds Monday as 3.550 (a slip for 3.055). Judged against the row
        // before it in the answer it would be five days and +15% - but the rate before it is Friday's, stored.
        await UpdateAsync();
        _source.Withdraw(Day(8), Day(9));
        _source.Publish((2026, 10, 12, 3.550m));

        var held = await Assert.ThrowsAsync<FxRatesDisagreeException>(() => UpdateAsync());

        Assert.Contains("the new rate 3.55 of 2026-10-12 moves more than 8% from 3.055 of 2026-10-09", held.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_store.Rows, r => r.Rate.Date == Day(12));
    }

    [Fact]
    public async Task NewRateWithNothingNearToJudgeItByIsHeldForTheOwner()
    {
        // Only the rate of 2005 is stored, and the answer brings one of this week: whatever it is, nothing
        // says whether it is plausible. On the first load of an empty table there is nothing to hold it against.
        var source = new FakeSource();
        source.Publish((2005, 1, 3, 4.356m));
        var store = new MemoryStore();
        var rates = new FxRates(source, store, _clock);
        _clock.Reset(Instant.FromUtc(2005, 1, 4, 16, 0));
        await rates.UpdateAsync(null, CancellationToken.None);
        source.Publish((2005, 1, 14, 4.36m)); // eleven days later

        var held = await Assert.ThrowsAsync<FxRatesDisagreeException>(() =>
        {
            _clock.Reset(Instant.FromUtc(2005, 1, 14, 16, 0));
            return rates.UpdateAsync(null, CancellationToken.None);
        });

        Assert.Contains("the new rate 4.36 of 2005-01-14 cannot be judged: no rate of the 7 days before it is known", held.Message, StringComparison.Ordinal);
        var accepted = await rates.UpdateAsync(new FxAcceptance(held.Code, "the platform was off; 4.36 is on the bank's page"), CancellationToken.None);
        Assert.Equal(1, accepted.Added);
        Assert.Equal("the platform was off; 4.36 is on the bank's page", store.Rows.Single(r => r.Rate.Date == new LocalDate(2005, 1, 14)).Reason);
    }

    [Fact]
    public async Task EveryUpdateThatDidOrNoticedSomethingWritesItselfDownAlsoTheDailyJob()
    {
        var job = new FxRatesJob(_rates);
        _source.Withdraw(Day(8));
        await job.RunAsync(new TradeDate(Day(12)), CancellationToken.None); // the first load
        await job.RunAsync(new TradeDate(Day(12)), CancellationToken.None); // nothing new: nothing to write down
        Assert.Equal([(FxRatesNote.Updated, "fx-rates")], _store.Notes.Select(n => (n.Kind, n.Actor)));

        // Thursday's rate arrives late, and Wednesday's is left out of the answer: nobody watches the job, so
        // the job itself leaves the note.
        _source.Publish((2026, 10, 8, 3.084m));
        _source.Withdraw(Day(7));
        await job.RunAsync(new TradeDate(Day(12)), CancellationToken.None);
        await job.RunAsync(new TradeDate(Day(12)), CancellationToken.None); // Wednesday is still left out

        Assert.Equal([FxRatesNote.Updated, FxRatesNote.Noted, FxRatesNote.Noted], _store.Notes.Select(n => n.Kind));
        Assert.Contains("3.084 for 2026-10-08 arrived after later days were stored", _store.Notes[1].Payload, StringComparison.Ordinal);
        Assert.Contains("the source did not repeat the stored rate of 2026-10-07 (3.074); it is kept as it is", _store.Notes[1].Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("arrived after", _store.Notes[2].Payload, StringComparison.Ordinal);

        _source.Publish((2026, 10, 7, 3.074m), (2026, 10, 9, 3.056m));
        var code = await RefusedCodeAsync();
        await UpdateAsync(new FxAcceptance(code, "checked"));
        Assert.Equal((FxRatesNote.ChangesAccepted, "owner"), (_store.Notes[^1].Kind, _store.Notes[^1].Actor));
        Assert.Contains(code, _store.Notes[^1].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredRateTheSourceLeavesOutIsKeptAndNamedAndDoesNotStopTheUpdates()
    {
        // A dead end here would stop the rates for good over something that can store nothing wrong.
        await UpdateAsync();
        _source.Withdraw(Day(8));
        _source.Publish((2026, 10, 12, 3.041m));

        var update = await UpdateAsync();
        var next = await UpdateAsync(); // the daily job, the day after: still no dead end

        Assert.Equal((1, 2), (update.Added, update.Compared));
        Assert.Equal((Day(8), 3.084m), (Assert.Single(update.NotRepeated).Date, update.NotRepeated[0].Rate));
        Assert.Equal(3.084m, Stored(8));
        Assert.Equal((0, 3, 1), (next.Added, next.Compared, next.NotRepeated.Count));
    }

    [Fact]
    public async Task AnswerThatIsEmptyOrRepeatsNothingOfWhatIsStoredIsAFault()
    {
        await UpdateAsync();

        // The stored days are gone from the answer and one new day is in it: nothing to compare it with.
        _source.Withdraw(Day(7), Day(8), Day(9));
        _source.Publish((2026, 10, 12, 3.041m));
        var foreign = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync());
        Assert.Contains("repeated none of the 3 rates stored", foreign.Message, StringComparison.Ordinal);

        _source.Withdraw(Day(12));
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync());
        Assert.Contains("returned no rate", empty.Message, StringComparison.Ordinal);
        Assert.Equal(4, _store.Rows.Count);
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("source")]
    [InlineData("early")]
    [InlineData("late")]
    [InlineData("twice")]
    public async Task AnswerWithSomethingThatWasNotAskedForIsRefusedWhole(string wrong)
    {
        await UpdateAsync();
        var good = new FxRate(Currency.Usd, Currency.Ils, 3.041m, Day(12), Boi);
        _source.Extra = wrong switch
        {
            "pair" => [new FxRate(Currency.Ils, Currency.Usd, 3.041m, Day(12), Boi)],
            "source" => [new FxRate(Currency.Usd, Currency.Ils, 3.041m, Day(12), "somebody else")],
            "early" => [new FxRate(Currency.Usd, Currency.Ils, 3.9m, new LocalDate(2026, 9, 1), Boi)],
            "late" => [new FxRate(Currency.Usd, Currency.Ils, 3.041m, Day(13), Boi)],
            _ => [good, good],
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync());

        Assert.Contains("was not asked for", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, _store.Rows.Count);
    }

    [Fact]
    public async Task FaultsReadTheSameWhateverTheLanguageOfTheSystem()
    {
        await UpdateAsync();
        _source.Publish((2026, 10, 8, 3.085m));
        var before = CultureInfo.CurrentCulture;
        try
        {
            // A culture that writes 3,085, as Russian does (the tests run without the system's culture data).
            var commas = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commas.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = commas;
            Assert.Equal("3,085", $"{3.085m}");

            var error = await Assert.ThrowsAsync<FxRatesDisagreeException>(() => UpdateAsync());

            Assert.Contains("now states 3.085 for 2026-10-08; 3.084 is stored", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task RateOfTheDayIsUsedAsItIsAndAWeekendCarriesFridaysRateWithAMark()
    {
        _source.Publish((2026, 10, 12, 3.041m));
        await UpdateAsync();

        var friday = await OnAsync(Day(9));
        var saturday = await OnAsync(Day(10));
        var sunday = await OnAsync(Day(11));
        var monday = await OnAsync(Day(12));

        Assert.Equal((3.055m, Day(9), false, 0, false), (friday.Rate.Rate, friday.On, friday.IsCarriedForward, friday.DaysCarried, friday.IsProvisional));
        Assert.Equal((3.055m, Day(9), Day(10), true, 1), (saturday.Rate.Rate, saturday.Rate.Date, saturday.On, saturday.IsCarriedForward, saturday.DaysCarried));
        Assert.Equal((true, 2), (sunday.IsCarriedForward, sunday.DaysCarried));
        // Monday's rate is stored: the source demonstrably skipped the weekend, the carried rate will not change.
        Assert.False(saturday.IsProvisional);
        Assert.False(sunday.IsProvisional);
        Assert.Equal((3.041m, false), (monday.Rate.Rate, monday.IsCarriedForward));
        Assert.Equal(Money.Ils(305.5m), saturday.Rate.Convert(Money.Usd(100m)));
    }

    [Fact]
    public async Task CarriedRateIsProvisionalWhileNothingLaterIsKnown()
    {
        // Monday evening, and the last stored rate is Friday's: the bank may yet publish for today, or the rates
        // were not updated. Either way the figure can change, and it says so.
        await UpdateAsync();

        var saturday = await OnAsync(Day(10));
        var today = await OnAsync(Day(12));

        Assert.Equal((3.055m, 1, true), (saturday.Rate.Rate, saturday.DaysCarried, saturday.IsProvisional));
        Assert.Equal((3.055m, 3, true), (today.Rate.Rate, today.DaysCarried, today.IsProvisional));
        Assert.False((await OnAsync(Day(9))).IsProvisional); // the rate of the day itself is what it is

        _source.Publish((2026, 10, 12, 3.041m)); // the rate arrives: the same question now has its final answer
        await UpdateAsync();
        Assert.Equal((3.055m, false), ((await OnAsync(Day(10))).Rate.Rate, (await OnAsync(Day(10))).IsProvisional));
        Assert.Equal((3.041m, false), ((await OnAsync(Day(12))).Rate.Rate, (await OnAsync(Day(12))).IsCarriedForward));
    }

    [Fact]
    public async Task ThereIsNoRateForTomorrowNorOneThatIsTooOld()
    {
        await UpdateAsync();

        var tomorrow = await Assert.ThrowsAsync<InvalidOperationException>(() => OnAsync(Day(13)));
        Assert.Contains("no USD/ILS rate for 2026-10-13 yet: today is 2026-10-12", tomorrow.Message, StringComparison.Ordinal);

        _clock.AdvanceDays(30); // nobody updated the rates for a month
        var lastUsable = await OnAsync(Day(9).PlusDays(FxRates.MaxDaysCarried));
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => OnAsync(Day(9).PlusDays(FxRates.MaxDaysCarried + 1)));
        var before = await Assert.ThrowsAsync<InvalidOperationException>(() => OnAsync(new LocalDate(2004, 12, 31)));
        // The rate of 2005-01-03 is not stretched over the 21 years to the next stored one.
        var gap = await Assert.ThrowsAsync<InvalidOperationException>(() => OnAsync(new LocalDate(2015, 6, 1)));

        Assert.Equal((FxRates.MaxDaysCarried, true), (lastUsable.DaysCarried, lastUsable.IsProvisional));
        Assert.Equal(7, FxRates.MaxDaysCarried); // the longest silence of the source since 2005
        Assert.Contains("A rate that old is not used", stale.Message, StringComparison.Ordinal);
        Assert.Contains("no USD/ILS rate", before.Message, StringComparison.Ordinal);
        Assert.Contains("2005-01-03", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RateCannotBeUsedBeforeItsOwnDateAndTheRateOfTheDayIsNeverProvisional()
    {
        var rate = new FxRate(Currency.Usd, Currency.Ils, 3.055m, Day(9), Boi);

        Assert.Throws<ArgumentException>(() => new AppliedFxRate(rate, Day(8)));
        Assert.Throws<ArgumentException>(() => new AppliedFxRate(rate, Day(9), isProvisional: true));
        Assert.False(new AppliedFxRate(rate, Day(9)).IsCarriedForward);
        Assert.True(new AppliedFxRate(rate, Day(10), isProvisional: true).IsProvisional);
    }

    [Fact]
    public async Task DailyJobBringsTheRatesUpToDate()
    {
        var job = new FxRatesJob(_rates);

        await job.RunAsync(new TradeDate(Day(12)), CancellationToken.None);
        await job.RunAsync(new TradeDate(Day(12)), CancellationToken.None); // repeated after a crash: nothing twice

        Assert.Equal(("fx-rates", new LocalTime(11, 0)), (job.Name, job.DueAt));
        Assert.Equal(4, _store.Rows.Count);
        // The job never accepts a change on the owner's behalf.
        _source.Publish((2026, 10, 8, 3.085m));
        await Assert.ThrowsAsync<FxRatesDisagreeException>(() => job.RunAsync(new TradeDate(Day(12)), CancellationToken.None));
    }

    [Fact]
    public void ForAnyHistoryUpdatingInStepsEqualsOneUpdateAndNoStoredRateEverChanges()
    {
        // Rates are published one business day after another; the updates happen on arbitrary days in between,
        // some of them twice. What is stored in the end is exactly what was published.
        var history = Gen.Select(Gen.Int[2_900, 3_100], Gen.Bool).List[1, 60];
        history.Sample(days =>
        {
            var source = new FakeSource();
            var store = new MemoryStore();
            var start = new LocalDate(2026, 1, 5);
            var clock = new FakeClock(start.AtMidnight().InZoneStrictly(Zones.Israel).ToInstant() + Duration.FromHours(19));
            var rates = new FxRates(source, store, clock);
            var published = new List<(LocalDate Date, decimal Rate)>();
            var date = start;
            foreach (var (thousandths, updateToday) in days)
            {
                while (date.DayOfWeek is IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday)
                {
                    date = date.PlusDays(1);
                }

                var rate = thousandths / 1000m; // 2.900 … 3.100: no step is an unusual move
                source.Publish((date.Year, date.Month, date.Day, rate));
                published.Add((date, rate));
                clock.Reset(date.AtMidnight().InZoneStrictly(Zones.Israel).ToInstant() + Duration.FromHours(19));
                if (updateToday)
                {
                    rates.UpdateAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                    rates.UpdateAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                }

                date = date.PlusDays(1);
            }

            rates.UpdateAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            return store.Rows.All(r => r.Revision == 0)
                && store.InUse.Select(r => (r.Date, r.Rate)).SequenceEqual(published);
        });
    }

    /// <summary>A source that answers from what it was told to publish.</summary>
    private sealed class FakeSource : IFxRateSource
    {
        private readonly SortedDictionary<LocalDate, decimal> _published = [];

        public string Name => Boi;

        public Currency BaseCurrency => Currency.Usd;

        public Currency QuoteCurrency => Currency.Ils;

        public List<(LocalDate First, LocalDate Last)> Asked { get; } = [];

        /// <summary>Rows added to every answer as they are, to play a source that misbehaves.</summary>
        public IReadOnlyList<FxRate> Extra { get; set; } = [];

        public void Publish(params (int Year, int Month, int Day, decimal Rate)[] rates)
        {
            foreach (var (year, month, day, rate) in rates)
            {
                _published[new LocalDate(year, month, day)] = rate;
            }
        }

        public void Withdraw(params LocalDate[] dates)
        {
            foreach (var date in dates)
            {
                _published.Remove(date);
            }
        }

        public Task<IReadOnlyList<FxRate>> FetchAsync(LocalDate first, LocalDate last, CancellationToken cancellationToken)
        {
            Asked.Add((first, last));
            var rates = _published
                .Where(p => p.Key >= first && p.Key <= last)
                .Select(p => new FxRate(Currency.Usd, Currency.Ils, p.Value, p.Key, Boi))
                .Concat(Extra)
                .ToList();
            return Task.FromResult<IReadOnlyList<FxRate>>(rates);
        }
    }

    /// <summary>The store's contract, in memory: rows are only added, and the latest revision of a date is in use.</summary>
    private sealed class MemoryStore : IFxRateStore
    {
        public List<(FxRate Rate, int Revision, string? Reason, Instant FetchedAt)> Rows { get; } = [];

        public int Batches { get; private set; }

        /// <summary>What the updates wrote to the audit trail.</summary>
        public List<FxRatesNote> Notes { get; } = [];

        /// <summary>The rate in use for each date, earliest first.</summary>
        public IReadOnlyList<FxRate> InUse =>
            Rows.GroupBy(r => r.Rate.Date).OrderBy(g => g.Key).Select(g => g.MaxBy(r => r.Revision).Rate).ToList();

        public Task<IReadOnlyList<FxRate>> ListAsync(
            Currency baseCurrency, Currency quoteCurrency, string source, LocalDate first, LocalDate last,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FxRate>>(
                Of(baseCurrency, quoteCurrency, source).Where(r => r.Date >= first && r.Date <= last).ToList());

        public Task<FxRate?> LatestOnOrBeforeAsync(
            Currency baseCurrency, Currency quoteCurrency, string source, LocalDate day, CancellationToken cancellationToken) =>
            Task.FromResult(Of(baseCurrency, quoteCurrency, source).LastOrDefault(r => r.Date <= day));

        public Task AddAsync(
            IReadOnlyList<FxRate> added, IReadOnlyList<FxRate> accepted, string? reason, Instant fetchedAt, FxRatesNote note,
            CancellationToken cancellationToken)
        {
            if (added.Any(r => Rows.Exists(x => x.Rate.Date == r.Date)) || (accepted.Count > 0 && reason is null))
            {
                throw new InvalidOperationException("A first rate for a date that has one, or an accepted rate without a reason.");
            }

            Rows.AddRange(added.Select(r => (r, 0, (string?)null, fetchedAt)));
            foreach (var rate in accepted)
            {
                var revisions = Rows.Where(x => x.Rate.Date == rate.Date).Select(x => x.Revision).ToList();
                Rows.Add((rate, revisions.Count == 0 ? 0 : revisions.Max() + 1, reason, fetchedAt));
            }

            Notes.Add(note);
            Batches += added.Count + accepted.Count > 0 ? 1 : 0;
            return Task.CompletedTask;
        }

        private IEnumerable<FxRate> Of(Currency baseCurrency, Currency quoteCurrency, string source) =>
            InUse.Where(r => r.From == baseCurrency && r.To == quoteCurrency && r.Source == source);
    }
}
