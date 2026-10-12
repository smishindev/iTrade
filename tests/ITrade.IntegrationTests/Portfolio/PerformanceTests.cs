using ITrade.Application.Fx;
using ITrade.Application.Portfolio;
using ITrade.Application.Prices;
using ITrade.Domain;
using ITrade.Host;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Host;
using ITrade.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.IntegrationTests.Portfolio;

/// <summary>
/// P3.4.02 through the real Host and a real database: the returns of a period come from the ledger's
/// contributions and withdrawals and from the value of the portfolio around each of them, in dollars and in
/// shekels. Today is Tuesday 13 October 2026.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class PerformanceTests(PostgresFixture pg)
{
    private static readonly Instant Stored = Instant.FromUtc(2026, 10, 13, 6, 0);

    private static LocalDate Day(int day) => new(2026, 10, day);

    private static readonly LocalDate September30 = new(2026, 9, 30);

    /// <summary>The closes of VT and the rates of the bank on the sessions from 30 September to 12 October.</summary>
    private static readonly (LocalDate Session, decimal Close, decimal Rate)[] Market =
    [
        (September30, 149.00m, 3.100m), (Day(1), 150.00m, 3.100m), (Day(2), 151.00m, 3.095m), (Day(5), 152.50m, 3.090m),
        (Day(6), 153.00m, 3.085m), (Day(7), 154.00m, 3.080m), (Day(8), 155.00m, 3.084m), (Day(9), 158.00m, 3.055m),
        (Day(12), 159.50m, 3.078m),
    ];

    private sealed record World(IServiceProvider Services, FakeClock Clock, HoldingPlace Ibkr, Instrument Vt, string DataDir);

    private async Task<(World World, IAsyncDisposable App, IAsyncDisposable Scope)> NewAsync()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var dataDir = RunningHost.NewDataDir();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 13, 14, 0));
        var app = ITradeHost.Build(RunningHost.Args(database, dataDir), services => services.AddSingleton<IClock>(clock));
        await ITradeHost.MigrateAsync(app);
        var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var ibkr = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var vt = new Instrument(InstrumentId.New(), new Ticker("VT"), "ARCA", Currency.Usd, "Vanguard Total World Stock ETF");
        await using var db = DbSetup.Create(database);
        db.HoldingPlaces.Add(HoldingPlaceRow.From(ibkr));
        db.Instruments.Add(InstrumentRow.From(vt));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await new ClosingPriceStore(db).AddAsync(
            [.. Market.Select(m => new ClosingPrice(vt.Id, m.Session, new Price(m.Close, Currency.Usd), "yfinance"))], [],
            ClosingPrices.Calendar, [.. Market.Select(m => m.Session)], Stored, new PricesNote("test", PricesNote.Imported, "{}"),
            new ImportedExport(Stored, new string('a', 64)), CancellationToken.None);
        var bank = services.GetRequiredService<IFxRateSource>();
        await new FxRateStore(db).AddAsync(
            [.. Market.Select(m => new FxRate(bank.BaseCurrency, bank.QuoteCurrency, m.Rate, m.Session, bank.Name))], [], null, Stored,
            new FxRatesNote("test", FxRatesNote.Updated, "{}"), CancellationToken.None);
        return (new World(services, clock, ibkr, vt, dataDir), app, scope);
    }

    private static Transaction Entry(World w, TransactionKind kind, LocalDate day, Money cash, string id, decimal shares = 0m) =>
        new(
            TransactionId.New(), w.Ibkr.Id, kind, day, day, cash, TransactionSource.Flex, w.Clock.GetCurrentInstant(),
            shares == 0m ? null : w.Vt.Id, shares, shares == 0m ? null : new Price(-cash.Amount / shares, cash.Currency), id);

    [Fact]
    public async Task ReturnsOfAPeriodComeFromTheLedgersFlowsAndTheValuesAroundThem()
    {
        var (w, app, scope) = await NewAsync();
        await using var disposeApp = app;
        await using var disposeScope = scope;
        var recorder = w.Services.GetRequiredService<TransactionLedger>();

        // 3,000 dollars paid in on 1 October and 20 VT bought at 150.00; 1,500 more on the 8th and 9 VT at
        // 155.00; 100 taken out on the 9th.
        await recorder.RecordAsync(
            [
                Entry(w, TransactionKind.Deposit, Day(1), Money.Usd(3000m), "D-1"),
                Entry(w, TransactionKind.Buy, Day(1), Money.Usd(-3000m), "T-1", 20m),
                Entry(w, TransactionKind.Deposit, Day(8), Money.Usd(1500m), "D-2"),
                Entry(w, TransactionKind.Buy, Day(8), Money.Usd(-1395m), "T-2", 9m),
                Entry(w, TransactionKind.Withdrawal, Day(9), Money.Usd(-100m), "W-1"),
            ],
            CancellationToken.None);
        var performance = w.Services.GetRequiredService<PortfolioPerformance>();

        var october = await performance.OverAsync(Day(1), Day(12), CancellationToken.None);

        // The portfolio is valued on six days only: around each flow, and at both ends.
        Assert.Equal([September30, Day(1), Day(7), Day(8), Day(9), Day(12)], october.ValuedOn);
        Assert.Empty(october.Remarks);

        // In dollars: 20 x 150 = 3,000 on the 1st; 20 x 154 = 3,080 on the 7th; 29 x 155 + 105 cash = 4,600 on the
        // 8th; 29 x 158 + 5 = 4,587 on the 9th, after 100 went out; 29 x 159.50 + 5 = 4,630.50 on the 12th.
        ValuePoint[] dollars =
        [
            new(September30, 0m), new(Day(1), 3000m, Inflow: 3000m), new(Day(7), 3080m), new(Day(8), 4600m, Inflow: 1500m),
            new(Day(9), 4587m, Outflow: 100m), new(Day(12), 4630.50m),
        ];
        var inDollars = october.In(Currency.Usd);
        Assert.Equal(PeriodReturns.Of(dollars), inDollars.Returns);
        Assert.Equal((false, 0, 0), (inDollars.IsProvisional, inDollars.WhyNone.Count, inDollars.Notes.Count));
        // 3,080/3,000 x 4,600/4,580 x 4,687/4,600 x 4,630.50/4,587 - what a dollar made that was there all the time.
        Assert.Equal(0.060616m, decimal.Round(inDollars.Returns!.TimeWeighted, 6)); // 1.026667 x 1.004367 x 1.018913 x 1.009483
        Assert.Equal((230.50m, 4500m, 100m, September30, 12), (inDollars.Returns.Gain, inDollars.Returns.Contributed, inDollars.Returns.Withdrawn, inDollars.Returns.InvestedSince, inDollars.Returns.DaysAtWork));
        Assert.Equal((null, null), (inDollars.Returns.TimeWeightedAnnual, inDollars.Returns.MoneyWeightedAnnual)); // twelve days are not a year
        Assert.True(inDollars.Returns.MoneyWeighted > 0m);

        // In shekels every value and every flow is taken at the bank's rate of its own day.
        ValuePoint[] shekels =
        [
            new(September30, 0m), new(Day(1), 3000m * 3.100m, Inflow: 3000m * 3.100m), new(Day(7), 3080m * 3.080m),
            new(Day(8), 4600m * 3.084m, Inflow: 1500m * 3.084m), new(Day(9), 4587m * 3.055m, Outflow: 100m * 3.055m), new(Day(12), 4630.50m * 3.078m),
        ];
        var inShekels = october.In(Currency.Ils);
        Assert.Equal(PeriodReturns.Of(shekels), inShekels.Returns);
        Assert.Equal(4630.50m * 3.078m + 305.50m - 9300m - 4626m, inShekels.Returns!.Gain);
        Assert.True(inShekels.Returns.TimeWeighted < inDollars.Returns.TimeWeighted); // the dollar lost against the shekel meanwhile

        // A period inside the month starts from what was there: 3,080 on the 7th is its opening value.
        var second = await performance.OverAsync(Day(8), Day(12), CancellationToken.None);
        Assert.Equal([Day(7), Day(8), Day(9), Day(12)], second.ValuedOn);
        Assert.Equal((3080m, 150.50m), (second.In(Currency.Usd).Returns!.OpeningValue, second.In(Currency.Usd).Returns!.Gain)); // 4,630.50 + 100 - 3,080 - 1,500
        // A period that ends on the day of a flow has that flow in it.
        var toTheNinth = await performance.OverAsync(Day(8), Day(9), CancellationToken.None);
        Assert.Equal((1500m, 100m, 4587m), (toTheNinth.In(Currency.Usd).Returns!.Contributed, toTheNinth.In(Currency.Usd).Returns!.Withdrawn, toTheNinth.In(Currency.Usd).Returns!.ClosingValue));
        // And one without a flow needs its two ends only.
        var quiet = await performance.OverAsync(Day(2), Day(7), CancellationToken.None);
        Assert.Equal([Day(1), Day(7)], quiet.ValuedOn);
        Assert.Equal(3080m / 3000m - 1m, quiet.In(Currency.Usd).Returns!.TimeWeighted);

        // Up to today: Monday's close and Monday's rate stand in for Tuesday's - the figures are provisional, and said so.
        var toToday = await performance.OverAsync(Day(1), Day(13), CancellationToken.None);
        Assert.Equal((true, true), (toToday.In(Currency.Usd).IsProvisional, toToday.In(Currency.Ils).IsProvisional));
        Assert.NotNull(toToday.In(Currency.Usd).Returns);
        Assert.Contains(toToday.Remarks, r => r.StartsWith("2026-10-13: VT: provisional - valued at the close of 2026-10-12", StringComparison.Ordinal));

        // Up to tomorrow there is no value to end with: no return is made up.
        var toTomorrow = await performance.OverAsync(Day(1), Day(14), CancellationToken.None);
        Assert.Null(toTomorrow.In(Currency.Usd).Returns);
        Assert.Equal(["2026-10-14: the value in USD is not complete - 1 position(s) have no usable price."], toTomorrow.In(Currency.Usd).WhyNone);
        Assert.Contains("USD could not be converted", Assert.Single(toTomorrow.In(Currency.Ils).WhyNone), StringComparison.Ordinal);

        // A deposit the broker took back is no contribution: reversed, it leaves the returns as they were.
        var mistaken = Entry(w, TransactionKind.Deposit, Day(5), Money.Usd(700m), "D-x");
        await recorder.RecordAsync([mistaken], CancellationToken.None);
        Assert.Equal(5200m, (await performance.OverAsync(Day(1), Day(12), CancellationToken.None)).In(Currency.Usd).Returns!.Contributed);
        await recorder.ReverseAsync(mistaken.Id, "recorded by mistake", CancellationToken.None);
        Assert.Equal(PeriodReturns.Of(dollars), (await performance.OverAsync(Day(1), Day(12), CancellationToken.None)).In(Currency.Usd).Returns);

        // Everything is taken out on the 12th, to the cent; a dollar of interest arrives on the 13th. It is a
        // gain, it is in neither return, and that is said - together with why the period has no money-weighted figure.
        await recorder.RecordAsync(
            [
                Entry(w, TransactionKind.Sell, Day(12), Money.Usd(4625.50m), "T-3", -29m),
                Entry(w, TransactionKind.Withdrawal, Day(12), Money.Usd(-4630.50m), "W-2"),
                Entry(w, TransactionKind.Interest, Day(13), Money.Usd(1m), "I-1"),
            ],
            CancellationToken.None);
        var afterTheExit = (await performance.OverAsync(Day(1), Day(13), CancellationToken.None)).In(Currency.Usd);
        Assert.Equal((231.50m, 1m, 12, null), (afterTheExit.Returns!.Gain, afterTheExit.Returns.GainedOnNothing, afterTheExit.Returns.DaysAtWork, afterTheExit.Returns.MoneyWeighted));
        Assert.Equal(0.060616m, decimal.Round(afterTheExit.Returns.TimeWeighted, 6));
        Assert.Equal(2, afterTheExit.Notes.Count);
        Assert.Contains("turned up while nothing was invested", afterTheExit.Notes[0], StringComparison.Ordinal);
        Assert.Contains("stood empty for a while", afterTheExit.Notes[1], StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => performance.OverAsync(Day(12), Day(1), CancellationToken.None));
        Directory.Delete(w.DataDir, recursive: true);
    }

    [Fact]
    public async Task ContributionInShekelsIsToldInDollarsAtTheRateOfItsDay()
    {
        var (w, app, scope) = await NewAsync();
        await using var disposeApp = app;
        await using var disposeScope = scope;

        // 3,090 shekels paid in on Monday the 5th and left as they are; nothing else.
        await w.Services.GetRequiredService<TransactionLedger>().RecordAsync(
            [Entry(w, TransactionKind.Deposit, Day(5), Money.Ils(3090m), "D-1")], CancellationToken.None);

        var report = await w.Services.GetRequiredService<PortfolioPerformance>().OverAsync(Day(1), Day(12), CancellationToken.None);

        // In shekels nothing happened to them.
        var inShekels = report.In(Currency.Ils).Returns!;
        Assert.Equal((0m, 0m, 3090m, Day(4)), (inShekels.TimeWeighted, inShekels.Gain, inShekels.Contributed, inShekels.InvestedSince));
        // In dollars they were 1,000 at the 3.090 of that day and are 3,090 / 3.078 a week later: the shekel gained.
        var inDollars = report.In(Currency.Usd).Returns!;
        Assert.Equal((1000m, 3090m / 3.078m), (inDollars.Contributed, inDollars.ClosingValue));
        Assert.Equal(3090m / 3.078m / 1000m - 1m, inDollars.TimeWeighted);
        Assert.Equal(0.003899m, decimal.Round(inDollars.TimeWeighted, 6));
        // Sunday the 4th, the day before the money came, is valued too - with nothing in it.
        Assert.Equal([September30, Day(4), Day(5), Day(12)], report.ValuedOn);
        Directory.Delete(w.DataDir, recursive: true);
    }

    [Fact]
    public async Task FlowToldAtAProvisionalRateMakesTheReturnsProvisional()
    {
        var (w, app, scope) = await NewAsync();
        await using var disposeApp = app;
        await using var disposeScope = scope;

        // 1,000 dollars since the 1st. Today, Tuesday the 13th, 3,078 shekels come in and go out again: by the
        // evening only dollars are there, but what those shekels were in dollars rests on Monday's rate - the
        // bank has not published today's yet.
        await w.Services.GetRequiredService<TransactionLedger>().RecordAsync(
            [
                Entry(w, TransactionKind.Deposit, Day(1), Money.Usd(1000m), "D-1"),
                Entry(w, TransactionKind.Deposit, Day(13), Money.Ils(3078m), "D-2"),
                Entry(w, TransactionKind.Withdrawal, Day(13), Money.Ils(-3078m), "W-1"),
            ],
            CancellationToken.None);
        var performance = w.Services.GetRequiredService<PortfolioPerformance>();

        var report = await performance.OverAsync(Day(1), Day(13), CancellationToken.None);

        var inDollars = report.In(Currency.Usd);
        Assert.Equal((2000m, 1000m, 1000m, 0m), (inDollars.Returns!.Contributed, inDollars.Returns.Withdrawn, inDollars.Returns.ClosingValue, inDollars.Returns.TimeWeighted));
        Assert.True(inDollars.IsProvisional);
        // Up to Monday nothing provisional is in it.
        Assert.False((await performance.OverAsync(Day(1), Day(12), CancellationToken.None)).In(Currency.Usd).IsProvisional);
        Directory.Delete(w.DataDir, recursive: true);
    }
}
