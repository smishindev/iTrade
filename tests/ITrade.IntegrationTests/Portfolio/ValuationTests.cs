using ITrade.Application.Fx;
using ITrade.Application.Portfolio;
using ITrade.Application.Prices;
using ITrade.Domain;
using ITrade.Host;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Host;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.IntegrationTests.Portfolio;

/// <summary>
/// P3.4.01 through the real Host and a real database: the value of the portfolio on a date comes from the
/// ledger, the stored closing prices with their sessions, and the stored exchange rates — and says what it
/// could not value. October 2026: the 8th is a Thursday, the 9th a Friday, the 12th a Monday.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class ValuationTests(PostgresFixture pg)
{
    private static readonly Instant Stored = Instant.FromUtc(2026, 10, 13, 6, 0);

    private static LocalDate Day(int day) => new(2026, 10, day);

    [Fact]
    public async Task ValueOfADateIsWorkedOutFromTheLedgerThePricesAndTheRates()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var dataDir = RunningHost.NewDataDir();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 13, 14, 0)); // Tuesday the 13th, in New York and in Israel
        await using var app = ITradeHost.Build(RunningHost.Args(database, dataDir), services => services.AddSingleton<IClock>(clock));
        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var ibkr = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var vt = new Instrument(InstrumentId.New(), new Ticker("VT"), "ARCA", Currency.Usd, "Vanguard Total World Stock ETF");
        var spy = new Instrument(InstrumentId.New(), new Ticker("SPY"), "ARCA", Currency.Usd, "SPDR S&P 500 ETF Trust");
        await using (var db = DbSetup.Create(database))
        {
            db.HoldingPlaces.Add(HoldingPlaceRow.From(ibkr));
            db.Instruments.AddRange(InstrumentRow.From(vt), InstrumentRow.From(spy));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Closes of Thursday, Friday and Monday - and nobody loaded SPY's close of Monday.
            ClosingPrice Close(Instrument instrument, int day, decimal close) =>
                new(instrument.Id, Day(day), new Price(close, Currency.Usd), "yfinance");
            await new ClosingPriceStore(db).AddAsync(
                new PricesToStore(
                    [Close(vt, 8, 121.00m), Close(vt, 9, 121.50m), Close(vt, 12, 122.00m), Close(spy, 8, 775.00m), Close(spy, 9, 778.57m)], [],
                    ClosingPrices.Calendar, [Day(8), Day(9), Day(12)], [], []),
                Stored, new PricesNote("test", PricesNote.Imported, "{}"), new ImportedExport(Stored, new string('a', 64)), CancellationToken.None);

            // The bank's rates of the same three days, under the name the Host's own source goes by.
            var bank = services.GetRequiredService<IFxRateSource>();
            FxRate Rate(int day, decimal rate) => new(bank.BaseCurrency, bank.QuoteCurrency, rate, Day(day), bank.Name);
            await new FxRateStore(db).AddAsync(
                [Rate(8, 3.084m), Rate(9, 3.055m), Rate(12, 3.078m)], [], null, Stored, new FxRatesNote("test", FxRatesNote.Updated, "{}"),
                CancellationToken.None);
        }

        // 3,000 dollars paid in on Wednesday; on Thursday 4 VT at 120.13 (commission 1.00) and 2 SPY at 769.64 (0.35).
        Transaction Entry(TransactionKind kind, int day, decimal cash, string id, Instrument? instrument = null, decimal shares = 0m, TransactionId? relatedTo = null) =>
            new(
                TransactionId.New(), ibkr.Id, kind, Day(day), Day(day), Money.Usd(cash), TransactionSource.Flex, clock.GetCurrentInstant(),
                instrument?.Id, shares, kind == TransactionKind.Buy ? new Price(-cash / shares, Currency.Usd) : null, id, relatedTo: relatedTo);
        var buyVt = Entry(TransactionKind.Buy, 8, -480.52m, "T-1", vt, 4m);
        var buySpy = Entry(TransactionKind.Buy, 8, -1539.28m, "T-2", spy, 2m);
        var recorded = await services.GetRequiredService<TransactionLedger>().RecordAsync(
            [
                Entry(TransactionKind.Deposit, 7, 3000m, "D-1"),
                buyVt, Entry(TransactionKind.Fee, 8, -1.00m, "T-1:fee", vt, relatedTo: buyVt.Id),
                buySpy, Entry(TransactionKind.Fee, 8, -0.35m, "T-2:fee", spy, relatedTo: buySpy.Id),
            ],
            CancellationToken.None);
        Assert.Equal(5, recorded.Added);

        var valuation = services.GetRequiredService<PortfolioValuation>();
        Task<PortfolioValue> OnAsync(LocalDate date) => valuation.AsOfAsync(date, CancellationToken.None);
        string NameOf(InstrumentId id) => id == vt.Id ? "VT" : "SPY";

        // Friday: 4 x 121.50 + 2 x 778.57 + cash 978.85 = 3,021.99 dollars; x 3.055 = 9,232.17945 shekels.
        var friday = await OnAsync(Day(9));
        Assert.Equal((Money.Usd(3021.99m), Money.Ils(9232.17945m)), (friday.In(Currency.Usd).Total, friday.In(Currency.Ils).Total));
        Assert.Equal(friday.In(Currency.Ils, ibkr.Id).Total, friday.In(Currency.Ils).Total);
        Assert.Equal((false, 3.055m, Day(9)), (friday.In(Currency.Ils).IsProvisional, friday.Rate!.Rate.Rate, friday.Rate.Rate.Date));
        Assert.Empty(friday.Remarks(NameOf));

        // Thursday, the day of the purchases, at Thursday's closes and Thursday's rate.
        Assert.Equal(Money.Ils((484.00m + 1550.00m + 978.85m) * 3.084m), (await OnAsync(Day(8))).In(Currency.Ils).Total);
        // Wednesday: the money was in, nothing was bought yet - and there is no rate of that day or before it.
        var wednesday = await OnAsync(Day(7));
        Assert.Equal((Money.Usd(3000m), null), (wednesday.In(Currency.Usd).Total, wednesday.In(Currency.Ils).Total));
        Assert.Contains("on or before 2026-10-07", wednesday.WhyNoRate, StringComparison.Ordinal);
        Assert.Equal(Money.Usd(0m), (await OnAsync(Day(6))).In(Currency.Usd).Total);

        // Sunday: the exchange and the bank were closed, both moved on since - Friday's figures, and final.
        var sunday = await OnAsync(Day(11));
        Assert.Equal((Money.Ils(9232.17945m), false, 2), (sunday.In(Currency.Ils).Total, sunday.In(Currency.Ils).IsProvisional, sunday.Rate!.DaysCarried));
        Assert.All(sunday.Positions, p => Assert.Equal((PriceAvailability.CarriedOverAClosedDay, Day(9)), (p.Price.Availability, p.Price.Price!.Session)));

        // Monday: SPY's close is missing. Friday's is not used for it, and what is left is not called the total.
        var monday = await OnAsync(Day(12));
        var inDollars = monday.In(Currency.Usd);
        Assert.Equal((null, Money.Usd(488.00m + 978.85m), spy.Id), (inDollars.Total, inDollars.Valued, Assert.Single(inDollars.NotValued).Position.Instrument));
        Assert.Equal(["SPY: not valued - there is no closing price for the session 2026-10-12 goes by."], monday.Remarks(NameOf));

        // Today, Tuesday: nothing of today is stored yet - Monday's close of VT and Monday's rate, both provisional.
        var today = await OnAsync(Day(13));
        Assert.Equal((null, Money.Ils((488.00m + 978.85m) * 3.078m), true), (today.In(Currency.Ils).Total, today.In(Currency.Ils).Valued, today.In(Currency.Ils).IsProvisional));
        Assert.Equal(
            [
                "VT: provisional - valued at the close of 2026-10-12, 1 day(s) before; the exchange may have been open since.",
                "SPY: not valued - there is no closing price for the session 2026-10-13 goes by.",
                "The exchange rate is provisional: 3.078 of 2026-10-12 (BOI representative), 1 day(s) before; a later one may still be published.",
            ],
            today.Remarks(NameOf));

        // Tomorrow has neither prices nor a rate: the cash is all that can be said, in its own currency.
        var tomorrow = await OnAsync(Day(14));
        Assert.Equal((Money.Usd(978.85m), 2), (tomorrow.In(Currency.Usd).Valued, tomorrow.In(Currency.Usd).NotValued.Count));
        Assert.Equal(Money.Ils(0m), tomorrow.In(Currency.Ils).Valued);
        Assert.Equal([Money.Usd(978.85m)], tomorrow.In(Currency.Ils).NotConverted);
        Assert.Contains("today is 2026-10-13", tomorrow.WhyNoRate, StringComparison.Ordinal);

        // The broker cancels the SPY purchase: the entry is reversed, and Friday is worth what the ledger now says.
        await services.GetRequiredService<TransactionLedger>().ReverseAsync(buySpy.Id, "the broker cancelled the trade", CancellationToken.None);
        Assert.Equal(Money.Usd(486.00m + 3000m - 480.52m - 1.00m), (await OnAsync(Day(9))).In(Currency.Usd).Total);

        // A second holding place with 500 dollars in it: each place has its value, and the whole is both.
        var second = new HoldingPlace(HoldingPlaceId.New(), "Another broker", HoldingPlaceKind.Broker, IntegrationKind.Manual);
        await using (var db = DbSetup.Create(database))
        {
            db.HoldingPlaces.Add(HoldingPlaceRow.From(second));
            await db.SaveChangesAsync();
        }

        await services.GetRequiredService<TransactionLedger>().RecordAsync(
            [new Transaction(TransactionId.New(), second.Id, TransactionKind.Deposit, Day(9), Day(9), Money.Usd(500m), TransactionSource.Manual, clock.GetCurrentInstant())],
            CancellationToken.None);
        var both = await OnAsync(Day(9));
        Assert.Equal(
            (Money.Usd(3004.48m), Money.Usd(500m), Money.Usd(3504.48m), 2),
            (both.In(Currency.Usd, ibkr.Id).Total, both.In(Currency.Usd, second.Id).Total, both.In(Currency.Usd).Total, both.Places.Count));
        Directory.Delete(dataDir, recursive: true);
    }
}
