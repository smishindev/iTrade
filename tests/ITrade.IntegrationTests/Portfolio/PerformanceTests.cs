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

    private sealed record World(IServiceProvider Services, FakeClock Clock, HoldingPlace Ibkr, Instrument Vt, string DataDir, string Database);

    private Task<(World World, IAsyncDisposable App, IAsyncDisposable Scope)> NewAsync(params string[] hostArgs) =>
        NewAsync(Day(12), hostArgs);

    /// <param name="ratesThrough">The last day the bank's rate is stored for.</param>
    /// <param name="hostArgs">Settings of the Host.</param>
    private async Task<(World World, IAsyncDisposable App, IAsyncDisposable Scope)> NewAsync(LocalDate ratesThrough, params string[] hostArgs)
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var dataDir = RunningHost.NewDataDir();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 13, 14, 0));
        var app = ITradeHost.Build([.. RunningHost.Args(database, dataDir), .. hostArgs], services => services.AddSingleton<IClock>(clock));
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
            new PricesToStore(
                [.. Market.Select(m => new ClosingPrice(vt.Id, m.Session, new Price(m.Close, Currency.Usd), "yfinance"))], [],
                ClosingPrices.Calendar, [.. Market.Select(m => m.Session)], [], []),
            Stored, new PricesNote("test", PricesNote.Imported, "{}"), new ImportedExport(Stored, new string('a', 64)), CancellationToken.None);
        var bank = services.GetRequiredService<IFxRateSource>();
        await new FxRateStore(db).AddAsync(
            [.. Market.Where(m => m.Session <= ratesThrough).Select(m => new FxRate(bank.BaseCurrency, bank.QuoteCurrency, m.Rate, m.Session, bank.Name))], [], null, Stored,
            new FxRatesNote("test", FxRatesNote.Updated, "{}"), CancellationToken.None);
        return (new World(services, clock, ibkr, vt, dataDir, database), app, scope);
    }

    /// <summary>Stores what a fund did, and - for a fund of its own - its closes on sessions that are already stored.</summary>
    private static Task<Instrument> AddFundAsync(World w, string ticker, (LocalDate Session, decimal Close)[] closes, params (LocalDate Day, FundEventKind Kind, decimal Value)[] events) =>
        AddFundAsync(w, ticker, closes, [], events);

    private static async Task<Instrument> AddFundAsync(
        World w, string ticker, (LocalDate Session, decimal Close)[] closes, LocalDate[] newSessions, (LocalDate Day, FundEventKind Kind, decimal Value)[] events)
    {
        await using var db = DbSetup.Create(w.Database);
        var fund = ticker == "VT" ? w.Vt : new Instrument(InstrumentId.New(), new Ticker(ticker), "ARCA", Currency.Usd, $"{ticker} fund");
        if (ticker != "VT")
        {
            db.Instruments.Add(InstrumentRow.From(fund));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        await new ClosingPriceStore(db).AddAsync(
            new PricesToStore(
                [.. closes.Select(c => new ClosingPrice(fund.Id, c.Session, new Price(c.Close, Currency.Usd), "yfinance"))], [],
                ClosingPrices.Calendar, newSessions, [.. events.Select(e => new FundEvent(fund.Id, e.Day, e.Kind, e.Value, "yfinance"))], []),
            Stored, new PricesNote("test", PricesNote.Imported, "{}"), new ImportedExport(Stored, new string('b', 64)), CancellationToken.None);
        return fund;
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

        // The same money in the index fund (VT itself here): 3,000 buy 20 shares at the close of the 1st; 1,500
        // buy 1,500 / 155 more at the close of the 8th; 100 are sold out at the close of the 9th. Nothing lies
        // idle there, so the index does a little better than a portfolio that kept 105 dollars in cash.
        var shares = 3000m / 150m;
        var onThe8th = shares + (1500m / 155m);
        var onThe9th = onThe8th + ((0m - 100m) / 158m);
        ValuePoint[] indexDollars =
        [
            new(September30, 0m), new(Day(1), shares * 150m, Inflow: 3000m), new(Day(7), shares * 154m), new(Day(8), onThe8th * 155m, Inflow: 1500m),
            new(Day(9), onThe9th * 158m, Outflow: 100m), new(Day(12), onThe9th * 159.50m),
        ];
        Assert.Equal(("VT", 0), (october.Index.Ticker, october.Index.WhyNone.Count));
        var indexInDollars = october.Index.In(Currency.Usd)!;
        Assert.Equal(PeriodReturns.Of(indexDollars), indexInDollars.Returns);
        Assert.Equal(0.061087m, decimal.Round(indexInDollars.Returns!.TimeWeighted, 6)); // 1.026667 x 1.004367 x 158/155 x 159.5/158
        Assert.True(indexInDollars.Returns.TimeWeighted > inDollars.Returns.TimeWeighted);
        Assert.Equal((4500m, 100m, false), (indexInDollars.Returns.Contributed, indexInDollars.Returns.Withdrawn, indexInDollars.IsProvisional));
        // In shekels: the index's value of each day at that day's rate, against the same shekel flows.
        decimal[] rates = [3.100m, 3.100m, 3.080m, 3.084m, 3.055m, 3.078m];
        var indexShekels = indexDollars.Select((p, i) => shekels[i] with { Value = p.Value * rates[i] }).ToList();
        Assert.Equal(PeriodReturns.Of(indexShekels), october.Index.In(Currency.Ils)!.Returns);

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

        Assert.True(toToday.Index.In(Currency.Usd)!.IsProvisional); // the index is valued at Monday's close too

        // Up to tomorrow there is no value to end with: no return is made up.
        var toTomorrow = await performance.OverAsync(Day(1), Day(14), CancellationToken.None);
        Assert.Null(toTomorrow.In(Currency.Usd).Returns);
        Assert.Contains("The portfolio's own values in USD are not complete", Assert.Single(toTomorrow.Index.WhyNone), StringComparison.Ordinal);
        Assert.Empty(toTomorrow.Index.ByCurrency);
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
    public async Task PortfolioThatHoldsOnlyTheIndexEarnsWhatTheIndexDoes()
    {
        var (w, app, scope) = await NewAsync();
        await using var disposeApp = app;
        await using var disposeScope = scope;
        // VT pays 1.00 a share on Tuesday the 6th, when it closes at 153.00.
        await AddFundAsync(w, "VT", [], (Day(6), FundEventKind.Dividend, 1.00m));

        // The owner does what the index portfolio does: every dollar into VT at the close of the day it came -
        // 3,000 on the 1st, 1,550 on the 8th - and the dividend, a quarter of it withheld, back into VT at the
        // close of its day (as nearly as shares of six decimals allow).
        var dividend = new Transaction(
            TransactionId.New(), w.Ibkr.Id, TransactionKind.Dividend, Day(6), Day(6), Money.Usd(20m), TransactionSource.Flex,
            w.Clock.GetCurrentInstant(), w.Vt.Id, externalId: "DIV-1");
        await w.Services.GetRequiredService<TransactionLedger>().RecordAsync(
            [
                Entry(w, TransactionKind.Deposit, Day(1), Money.Usd(3000m), "D-1"),
                Entry(w, TransactionKind.Buy, Day(1), Money.Usd(-3000m), "T-1", 20m),
                dividend,
                new Transaction(
                    TransactionId.New(), w.Ibkr.Id, TransactionKind.TaxWithheld, Day(6), Day(6), Money.Usd(-5m), TransactionSource.Flex,
                    w.Clock.GetCurrentInstant(), w.Vt.Id, externalId: "DIV-1:tax", relatedTo: dividend.Id),
                new Transaction(
                    TransactionId.New(), w.Ibkr.Id, TransactionKind.Buy, Day(6), Day(6), Money.Usd(-14.999967m), TransactionSource.Flex,
                    w.Clock.GetCurrentInstant(), w.Vt.Id, 0.098039m, new Price(153m, Currency.Usd), "T-2"),
                Entry(w, TransactionKind.Deposit, Day(8), Money.Usd(1550m), "D-2"),
                Entry(w, TransactionKind.Buy, Day(8), Money.Usd(-1550m), "T-3", 10m),
            ],
            CancellationToken.None);

        var report = await w.Services.GetRequiredService<PortfolioPerformance>().OverAsync(Day(1), Day(12), CancellationToken.None);

        foreach (var currency in new[] { Currency.Usd, Currency.Ils })
        {
            var (own, index) = (report.In(currency).Returns!, report.Index.In(currency)!.Returns!);
            Assert.InRange(own.TimeWeighted - index.TimeWeighted, -0.0000001m, 0.0000001m);
            Assert.InRange(own.MoneyWeighted!.Value - index.MoneyWeighted!.Value, -0.0000001m, 0.0000001m);
            Assert.InRange(own.Gain - index.Gain, -0.001m, 0.001m);
            Assert.Equal((own.Contributed, own.Withdrawn, own.DaysAtWork), (index.Contributed, index.Withdrawn, index.DaysAtWork));
        }

        // 30.098039 shares at 159.50 and a rounding crumb of cash: 250.64 earned on 4,550 - the 15.00 of the
        // dividend are in it, the 5.00 withheld are not.
        Assert.Equal(250.637254m, decimal.Round(report.In(Currency.Usd).Returns!.Gain, 6));
        // Without the dividend the index would have 30 shares: 4,785 instead of 4,800.64.
        Assert.Equal(4800.637255m, decimal.Round(report.Index.In(Currency.Usd)!.Returns!.ClosingValue, 6));
        Directory.Delete(w.DataDir, recursive: true);
    }

    [Fact]
    public async Task SplitOfTheIndexFundIsNoFallOfTheIndex()
    {
        var (w, app, scope) = await NewAsync("--ITrade:IndexTicker=WRLD");
        await using var disposeApp = app;
        await using var disposeScope = scope;
        // The index fund closes at 100 when the money comes, splits 2:1 on Thursday the 8th and closes at 55 on
        // Monday - 110 of the old shares. The owner keeps 1,000 dollars in cash all the while.
        (LocalDate, decimal)[] closes = [(September30, 100m), (Day(1), 100m), (Day(7), 104m), (Day(8), 52m), (Day(12), 55m)];
        await AddFundAsync(w, "WRLD", closes, (Day(8), FundEventKind.Split, 2m));
        var recorder = w.Services.GetRequiredService<TransactionLedger>();
        await recorder.RecordAsync([Entry(w, TransactionKind.Deposit, Day(1), Money.Usd(1000m), "D-1")], CancellationToken.None);
        var performance = w.Services.GetRequiredService<PortfolioPerformance>();

        var report = await performance.OverAsync(Day(1), Day(12), CancellationToken.None);

        // 10 shares became 20; 20 x 55 = 1,100. Read without the split it would be 10 x 55: a loss of 45%.
        var index = report.Index.In(Currency.Usd)!.Returns!;
        Assert.Equal(("WRLD", 0.1m, 1100m, 100m), (report.Index.Ticker, index.TimeWeighted, index.ClosingValue, index.Gain));
        Assert.Equal((0m, 0m), (report.In(Currency.Usd).Returns!.TimeWeighted, report.In(Currency.Usd).Returns!.Gain));

        // A period whose last day has no close of the fund: no comparison, and the day is named. The
        // portfolio's own returns do not depend on the index.
        var toFriday = await performance.OverAsync(Day(1), Day(9), CancellationToken.None);
        Assert.Equal(["2026-10-09: there is no closing price of WRLD to use."], toFriday.Index.WhyNone);
        Assert.NotNull(toFriday.In(Currency.Usd).Returns);

        // The owner takes the 1,000 out on Monday - and 110 more that came as interest. The index portfolio is
        // worth 1,100 at that close: it can still follow, to the cent.
        await recorder.RecordAsync(
            [
                Entry(w, TransactionKind.Interest, Day(12), Money.Usd(110m), "I-1"),
                Entry(w, TransactionKind.Withdrawal, Day(12), Money.Usd(-1100m), "W-1"),
            ],
            CancellationToken.None);
        var emptied = await performance.OverAsync(Day(1), Day(12), CancellationToken.None);
        Assert.Equal((0m, 0.1m), (emptied.Index.In(Currency.Usd)!.Returns!.ClosingValue, emptied.Index.In(Currency.Usd)!.Returns!.TimeWeighted));
        // One dollar more than that it cannot give.
        await recorder.RecordAsync(
            [
                Entry(w, TransactionKind.Interest, Day(12), Money.Usd(1m), "I-2"),
                Entry(w, TransactionKind.Withdrawal, Day(12), Money.Usd(-1m), "W-2"),
            ],
            CancellationToken.None);
        var overdrawn = await performance.OverAsync(Day(1), Day(12), CancellationToken.None);
        Assert.Contains("On 2026-10-12 more is taken out than the index portfolio holds", Assert.Single(overdrawn.Index.WhyNone), StringComparison.Ordinal);
        Assert.Equal(111m, overdrawn.In(Currency.Usd).Returns!.Gain);
        Directory.Delete(w.DataDir, recursive: true);
    }

    [Fact]
    public async Task IndexToldInShekelsAtAProvisionalRateIsProvisional()
    {
        var (w, app, scope) = await NewAsync();
        await using var disposeApp = app;
        await using var disposeScope = scope;
        // 3,100 shekels since 1 October, kept as shekels; today's close of the index fund is already stored, the
        // bank's rate of today is not.
        await AddFundAsync(w, "VT", [(Day(13), 160m)], [Day(13)], []);
        await w.Services.GetRequiredService<TransactionLedger>().RecordAsync(
            [Entry(w, TransactionKind.Deposit, Day(1), Money.Ils(3100m), "D-1")], CancellationToken.None);
        var performance = w.Services.GetRequiredService<PortfolioPerformance>();

        var toToday = await performance.OverAsync(Day(1), Day(13), CancellationToken.None);

        // The owner's shekels are shekels: nothing provisional about them. Everything else rests on Monday's rate.
        Assert.False(toToday.In(Currency.Ils).IsProvisional);
        Assert.True(toToday.In(Currency.Usd).IsProvisional);
        Assert.Equal((true, true), (toToday.Index.In(Currency.Usd)!.IsProvisional, toToday.Index.In(Currency.Ils)!.IsProvisional));
        // 3,100 shekels were 1,000 dollars at 3.100: 1,000 / 150 shares, at 160 today, at Monday's 3.078.
        Assert.Equal(1000m / 150m * 160m * 3.078m, toToday.Index.In(Currency.Ils)!.Returns!.ClosingValue);

        // Up to Monday every figure is final.
        var toMonday = await performance.OverAsync(Day(1), Day(12), CancellationToken.None);
        Assert.Equal((false, false), (toMonday.Index.In(Currency.Usd)!.IsProvisional, toMonday.Index.In(Currency.Ils)!.IsProvisional));
        Directory.Delete(w.DataDir, recursive: true);
    }

    [Fact]
    public async Task IndexValuedAtACarriedCloseIsProvisionalThoughThePortfolioIsNot()
    {
        var (w, app, scope) = await NewAsync();
        await using var disposeApp = app;
        await using var disposeScope = scope;
        // 1,000 dollars in cash: the owner's dollars need no price and no rate. The index fund's close of today
        // is not stored yet - Monday's stands in for it.
        await w.Services.GetRequiredService<TransactionLedger>().RecordAsync(
            [Entry(w, TransactionKind.Deposit, Day(1), Money.Usd(1000m), "D-1")], CancellationToken.None);

        var toToday = await w.Services.GetRequiredService<PortfolioPerformance>().OverAsync(Day(1), Day(13), CancellationToken.None);

        Assert.False(toToday.In(Currency.Usd).IsProvisional);
        Assert.True(toToday.Index.In(Currency.Usd)!.IsProvisional);
        Assert.Equal(1000m / 150m * 159.50m, toToday.Index.In(Currency.Usd)!.Returns!.ClosingValue);
        Directory.Delete(w.DataDir, recursive: true);
    }

    [Fact]
    public async Task DayWithoutARateUnderTheIndexIsAReasonAndTheRestOfTheReportStands()
    {
        // The bank's rates stop on Friday the 2nd. The owner pays in 1,000 dollars on the 1st and takes them out
        // on the 2nd: from then on the portfolio is empty and needs no rate. The index fund rose from 150 to 151
        // meanwhile, so the index portfolio keeps a few dollars - and those cannot be told in shekels on the 12th.
        var (w, app, scope) = await NewAsync(Day(2));
        await using var disposeApp = app;
        await using var disposeScope = scope;
        await w.Services.GetRequiredService<TransactionLedger>().RecordAsync(
            [
                Entry(w, TransactionKind.Deposit, Day(1), Money.Usd(1000m), "D-1"),
                Entry(w, TransactionKind.Withdrawal, Day(2), Money.Usd(-1000m), "W-1"),
            ],
            CancellationToken.None);

        var report = await w.Services.GetRequiredService<PortfolioPerformance>().OverAsync(Day(1), Day(12), CancellationToken.None);

        Assert.Equal((0m, 0m), (report.In(Currency.Usd).Returns!.TimeWeighted, report.In(Currency.Ils).Returns!.ClosingValue));
        Assert.Empty(report.Index.WhyNone);
        var left = (1000m / 150m) + ((0m - 1000m) / 151m);
        Assert.Equal(left * 159.50m, report.Index.In(Currency.Usd)!.Returns!.ClosingValue);
        var inShekels = report.Index.In(Currency.Ils)!;
        Assert.Null(inShekels.Returns);
        Assert.Equal(["2026-10-12: there is no exchange rate to tell the index's value in ILS."], inShekels.WhyNone);
        Directory.Delete(w.DataDir, recursive: true);
    }

    [Fact]
    public async Task IndexFundThatIsNotInTheCatalogueGivesNoComparison()
    {
        var (w, app, scope) = await NewAsync("--ITrade:IndexTicker=NOPE");
        await using var disposeApp = app;
        await using var disposeScope = scope;
        await w.Services.GetRequiredService<TransactionLedger>().RecordAsync(
            [Entry(w, TransactionKind.Deposit, Day(1), Money.Usd(1000m), "D-1")], CancellationToken.None);

        var report = await w.Services.GetRequiredService<PortfolioPerformance>().OverAsync(Day(1), Day(12), CancellationToken.None);

        Assert.Equal(["NOPE, the fund that stands for the index, is not in the instrument catalogue."], report.Index.WhyNone);
        Assert.Null(report.Index.In(Currency.Usd));
        Assert.Equal(0m, report.In(Currency.Usd).Returns!.TimeWeighted);
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
        // The index follows those flows: its dollar figures are provisional for the same reason, and its shekel
        // ones because Monday's close - carried, the 13th not being stored - stands in for today's.
        Assert.Equal((true, true), (report.Index.In(Currency.Usd)!.IsProvisional, report.Index.In(Currency.Ils)!.IsProvisional));

        // Once today's close of the index fund is stored, only the rate is still Monday's: the shekel flows are
        // what they are, and so are the owner's shekels - but the index's dollars told in shekels are provisional.
        await AddFundAsync(w, "VT", [(Day(13), 160m)], [Day(13)], []);
        var withTheClose = await performance.OverAsync(Day(1), Day(13), CancellationToken.None);
        Assert.Equal((true, true), (withTheClose.Index.In(Currency.Usd)!.IsProvisional, withTheClose.Index.In(Currency.Ils)!.IsProvisional));
        Assert.Equal(1000m / 150m * 160m, withTheClose.Index.In(Currency.Usd)!.Returns!.ClosingValue);
        // Up to Monday nothing provisional is in it.
        Assert.False((await performance.OverAsync(Day(1), Day(12), CancellationToken.None)).In(Currency.Usd).IsProvisional);
        Directory.Delete(w.DataDir, recursive: true);
    }
}
