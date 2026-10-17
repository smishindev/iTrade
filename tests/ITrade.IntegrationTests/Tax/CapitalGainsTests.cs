using ITrade.Application.Fx;
using ITrade.Application.Portfolio;
using ITrade.Application.Tax;
using ITrade.Domain;
using ITrade.Host;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Host;
using ITrade.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.IntegrationTests.Tax;

/// <summary>
/// P4.1.02 through the real Host and a real database: the sales of a tax year from the ledger's lots (FIFO) and
/// the stored Bank of Israel rates, in shekels, with the real gain of each part (docs/tax-sources.md R2-R4).
/// Hand-worked: 5 VT bought at $100 (+$1) on 8 October at 3.4, 5 at $110 (+$1) on the 9th at 3.5; 7 sold at $120
/// (-$1.40) on the 12th at 3.7. Estimates, not checked by an accountant.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class CapitalGainsTests(PostgresFixture pg)
{
    private static readonly Instant Stored = Instant.FromUtc(2026, 10, 13, 6, 0);

    private static LocalDate Day(int day) => new(2026, 10, day);

    [Fact]
    public async Task TheSalesOfAYearAreTheLotsTheySoldAtTheRatesOfTheirDays()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 13, 14, 0));
        await using var app = ITradeHost.Build(RunningHost.Args(database, RunningHost.NewDataDir()), services => services.AddSingleton<IClock>(clock));
        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var ibkr = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var vt = new Instrument(InstrumentId.New(), new Ticker("VT"), "ARCA", Currency.Usd, "Vanguard Total World Stock ETF");
        var bank = services.GetRequiredService<IFxRateSource>();
        FxRate Rate(int day, decimal rate) => new(bank.BaseCurrency, bank.QuoteCurrency, rate, Day(day), bank.Name);
        await using (var db = DbSetup.Create(database))
        {
            db.HoldingPlaces.Add(HoldingPlaceRow.From(ibkr));
            db.Instruments.Add(InstrumentRow.From(vt));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await new FxRateStore(db).AddAsync(
                [Rate(8, 3.4m), Rate(9, 3.5m)], [], null, Stored, new FxRatesNote("test", FxRatesNote.Updated, "{}"), CancellationToken.None);
        }

        Transaction Entry(TransactionKind kind, int day, decimal cash, string id, decimal shares = 0m, decimal? price = null, TransactionId? relatedTo = null) =>
            new(TransactionId.New(), ibkr.Id, kind, Day(day), Day(day), Money.Usd(cash), TransactionSource.Flex, clock.GetCurrentInstant(),
                kind == TransactionKind.Deposit ? null : vt.Id, shares, price is { } p ? new Price(p, Currency.Usd) : null, id, relatedTo: relatedTo);
        var first = Entry(TransactionKind.Buy, 8, -500m, "B-1", 5m, 100m);
        var second = Entry(TransactionKind.Buy, 9, -550m, "B-2", 5m, 110m);
        var sale = Entry(TransactionKind.Sell, 12, 840m, "S-1", -7m, 120m);
        await services.GetRequiredService<TransactionLedger>().RecordAsync(
            [
                Entry(TransactionKind.Deposit, 7, 2000m, "D-1"),
                first, Entry(TransactionKind.Fee, 8, -1m, "B-1/commission", relatedTo: first.Id),
                second, Entry(TransactionKind.Fee, 9, -1m, "B-2/commission", relatedTo: second.Id),
                sale, Entry(TransactionKind.Fee, 12, -1.40m, "S-1/commission", relatedTo: sale.Id),
            ],
            CancellationToken.None);
        var gains = services.GetRequiredService<CapitalGains>();

        // The rate of the 12th is not in yet: the 9th's would be carried, provisional - the sale is not computed.
        var early = await gains.OfYearAsync(2026, CancellationToken.None);
        Assert.Equal((false, (decimal?)null), (early.IsComplete, early.RealGain));
        Assert.Equal(2, early.NotComputed.Count);
        Assert.Contains("is provisional; a tax figure is not computed with it", early.NotComputed[0], StringComparison.Ordinal);

        await using (var db = DbSetup.Create(database))
        {
            await new FxRateStore(db).AddAsync(
                [Rate(12, 3.7m)], [], null, Stored, new FxRatesNote("test", FxRatesNote.Updated, "{}"), CancellationToken.None);
        }

        var year = await gains.OfYearAsync(2026, CancellationToken.None);

        // FIFO: the 5 shares of the 8th, then 2 of the 9th; the sale's $840 and $1.40 shared 5 : 2.
        // Every sale recorded was computed; the year itself runs, so these are not its figures.
        Assert.Equal((true, false, false), (year.AllComputed, year.YearOver, year.IsComplete));
        Assert.Equal([(first.Id, 5m), (second.Id, 2m)], year.Sales.Select(s => (s.Disposal.Lot, s.Disposal.Quantity.Value)));
        // Paid $501 at 3.4, received $599 at 3.7: the dollar gain $98 at the sale rate, ₪362.60.
        var lotOfThe8th = year.Sales[0];
        Assert.Equal((1_703.4m, 2_216.3m, 150.3m, 362.6m), (lotOfThe8th.Cost, lotOfThe8th.Proceeds, lotOfThe8th.InflationaryAmount, lotOfThe8th.RealGain));
        // Paid $220.40 at 3.5, received $239.60 at 3.7: $19.20 at 3.7, ₪71.04.
        Assert.Equal(71.04m, year.Sales[1].RealGain);
        Assert.Equal((433.64m, 0m), (year.RealGain, year.OffsettableLoss));
        Assert.Equal(
            ["real_gain", "lot_matching", "lot_matching_abroad", "exchange_rate", "exchange_rate_day_of_a_trade", "repurchase_after_a_loss", "fractional_shares"],
            year.Assumptions.Select(a => a.Name));
        Assert.Equal(["2026 is not over (2026-10-13): these are the sales recorded so far, not the year's."], year.Remarks);
        Assert.All(year.Assumptions, a => Assert.False(a.Source.CheckedByAccountant));

        // A year without sales has none.
        Assert.Empty((await gains.OfYearAsync(2025, CancellationToken.None)).Sales);
        Assert.EndsWith(Path.Combine("config", "tax.toml"), year.ParametersFrom, StringComparison.Ordinal);

        // Parameters that name a lot rule the code does not follow, or leave it open, give no figures at all.
        var tax = services.GetRequiredService<TaxParameters>();
        CapitalGains With(TaxParameters other) => new(
            services.GetRequiredService<IHoldingPlaceStore>(), services.GetRequiredService<ITransactionStore>(), services.GetRequiredService<FxRates>(), other, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => With(tax with { LotMatching = new("lot_matching", (LotMatching)1, tax.LotMatching.Source) }).OfYearAsync(2026, CancellationToken.None));
        await Assert.ThrowsAsync<TaxRuleOpenException>(
            () => With(tax with { LotMatching = new("lot_matching", tax.LotMatching.Source, "O2") }).OfYearAsync(2026, CancellationToken.None));
        // An answer to O9 is a rule the code does not know yet: it refuses rather than keeps calling it open.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => With(tax with { RepurchaseAfterALoss = new("repurchase_after_a_loss", 0m, tax.CapitalGainsRate.Source) }).OfYearAsync(2026, CancellationToken.None));
    }

    [Fact]
    public async Task EachYearHasItsOwnSalesAndWhatCannotBeComputedOrRestsOnAnOpenQuestionIsSaid()
    {
        // IBKR: 10 VT bought 3.3.2025 at $100 (3.6); 4 sold 2.6.2025 at $110 (3.5); the other 6 sold 2.2.2026 at $90
        // (3.7), 6 bought again 10.2.2026 at $91 (3.65), half a share of them sold 2.3.2026 at $90 (3.6).
        // 1 of them sold 1.10.2026 at $85 (3.7), 12 days before today.
        // Another broker: 2 SPY bought 2.2.2026 at $500, $10 cash in lieu on 10.2.2026, sold 2.3.2026 at $510; 2 EFA
        // bought 1.6.2005 at $50, sold 2.3.2026 at $60.
        var database = await pg.NewEmptyDatabaseAsync();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 13, 14, 0));
        await using var app = ITradeHost.Build(RunningHost.Args(database, RunningHost.NewDataDir()), services => services.AddSingleton<IClock>(clock));
        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var ibkr = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var other = new HoldingPlace(HoldingPlaceId.New(), "Another broker", HoldingPlaceKind.Broker, IntegrationKind.Manual);
        var vt = new Instrument(InstrumentId.New(), new Ticker("VT"), "ARCA", Currency.Usd, "Vanguard Total World Stock ETF");
        var spy = new Instrument(InstrumentId.New(), new Ticker("SPY"), "ARCA", Currency.Usd, "SPDR S&P 500 ETF Trust");
        var efa = new Instrument(InstrumentId.New(), new Ticker("EFA"), "ARCA", Currency.Usd, "iShares MSCI EAFE ETF");
        var bank = services.GetRequiredService<IFxRateSource>();
        FxRate Rate(LocalDate day, decimal rate) => new(bank.BaseCurrency, bank.QuoteCurrency, rate, day, bank.Name);
        LocalDate On(int year, int month, int day) => new(year, month, day);
        await using (var db = DbSetup.Create(database))
        {
            db.HoldingPlaces.AddRange(HoldingPlaceRow.From(ibkr), HoldingPlaceRow.From(other));
            db.Instruments.AddRange(InstrumentRow.From(vt), InstrumentRow.From(spy), InstrumentRow.From(efa));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await new FxRateStore(db).AddAsync(
                [
                    Rate(On(2025, 3, 3), 3.6m), Rate(On(2025, 6, 2), 3.5m), Rate(On(2026, 2, 2), 3.7m), Rate(On(2026, 2, 10), 3.65m), Rate(On(2026, 3, 2), 3.6m),
                    Rate(On(2026, 10, 1), 3.7m), Rate(On(2026, 10, 2), 3.72m),
                ],
                [], null, Stored, new FxRatesNote("test", FxRatesNote.Updated, "{}"), CancellationToken.None);
        }

        var n = 0;
        Transaction Entry(HoldingPlace place, TransactionKind kind, LocalDate day, decimal cash, Instrument? instrument = null, decimal shares = 0m, decimal? price = null) =>
            new(TransactionId.New(), place.Id, kind, day, day, Money.Usd(cash), TransactionSource.Flex, clock.GetCurrentInstant(),
                instrument?.Id, shares, price is { } p ? new Price(p, Currency.Usd) : null, $"E-{++n}");
        await services.GetRequiredService<TransactionLedger>().RecordAsync(
            [
                Entry(ibkr, TransactionKind.Deposit, On(2025, 3, 1), 2_000m),
                Entry(ibkr, TransactionKind.Buy, On(2025, 3, 3), -1_000m, vt, 10m, 100m),
                Entry(ibkr, TransactionKind.Sell, On(2025, 6, 2), 440m, vt, -4m, 110m),
                Entry(ibkr, TransactionKind.Sell, On(2026, 2, 2), 540m, vt, -6m, 90m),
                Entry(ibkr, TransactionKind.Buy, On(2026, 2, 10), -546m, vt, 6m, 91m),
                Entry(ibkr, TransactionKind.Sell, On(2026, 3, 2), 45m, vt, -0.5m, 90m),
                Entry(ibkr, TransactionKind.Sell, On(2026, 10, 1), 85m, vt, -1m, 85m),
                Entry(other, TransactionKind.Deposit, On(2005, 5, 31), 100m),
                Entry(other, TransactionKind.Buy, On(2005, 6, 1), -100m, efa, 2m, 50m),
                Entry(other, TransactionKind.Deposit, On(2026, 2, 1), 1_200m),
                Entry(other, TransactionKind.Buy, On(2026, 2, 2), -1_000m, spy, 2m, 500m),
                Entry(other, TransactionKind.CorporateAction, On(2026, 2, 10), 10m, spy),
                Entry(other, TransactionKind.Sell, On(2026, 3, 2), 1_020m, spy, -2m, 510m),
                Entry(other, TransactionKind.Sell, On(2026, 3, 2), 120m, efa, -2m, 60m),
            ],
            CancellationToken.None);
        var gains = services.GetRequiredService<CapitalGains>();

        var year2025 = await gains.OfYearAsync(2025, CancellationToken.None);
        var year2026 = await gains.OfYearAsync(2026, CancellationToken.None);

        // 2025: $400 at 3.6 against $440 at 3.5 - the shekel strengthened, the whole nominal ₪100 is real.
        Assert.Equal((true, 100m, 0m), (year2025.IsComplete, year2025.RealGain, year2025.OffsettableLoss));
        Assert.Empty(year2025.Remarks);

        // 2026, IBKR: the 2025 lot's other 6 at 3.6 (₪2,160) for $540 at 3.7 (₪1,998): a loss of ₪162, all offsettable;
        // half a share of the February lot, $45.50 at 3.65 (₪166.075) for $45 at 3.6 (₪162): the shekel strengthened, only
        // the dollar loss of $0.50 at 3.6, ₪1.80, is offsettable. Its own purchase, 20 days before, is no repurchase.
        // One more of that lot, $91 at 3.65 (₪332.15) for $85 at 3.7 (₪314.50): a loss of ₪17.65, all offsettable.
        var ibkrSales = year2026.Sales.Where(s => s.Disposal.HoldingPlace == ibkr.Id).ToList();
        Assert.Equal([(On(2025, 3, 3), 0m, 162m), (On(2026, 2, 10), 0m, 1.8m), (On(2026, 2, 10), 0m, 17.65m)], ibkrSales.Select(s => (s.Disposal.Acquired, s.RealGain, s.OffsettableLoss)));
        // The other broker's SPY had cash in lieu the lots do not hold: not computed, and the year's sums are not given.
        Assert.Equal((false, (decimal?)null, (decimal?)null), (year2026.IsComplete, year2026.RealGain, year2026.OffsettableLoss));
        Assert.Contains(year2026.NotComputed, s => s.StartsWith("Another broker: the sale of 2026-03-02 from the lot of 2026-02-02 (2 shares) - its instrument has corporate-action cash", StringComparison.Ordinal));
        // A lot of 2005 falls under the transitional rules - said as such, though there is no rate of its day either.
        Assert.Contains("Another broker: the sale of 2026-03-02 from the lot of 2005-06-01 (2 shares) - bought before 2006-01-01: the transitional rules for it are not built.", year2026.NotComputed);
        Assert.Equal(
            [
                "2026 is not over (2026-10-13): these are the sales recorded so far, not the year's.",
                "IBKR: the sale of 2026-02-02 from the lot of 2025-03-03 (6 shares) - a loss, and the same fund was bought within 30 days of the sale: whether it is offsettable in full is open (O9).",
                "IBKR: the sale of 2026-03-02 from the lot of 2026-02-10 (0.5 shares) - a fraction of a share: computed like whole shares; open (O10).",
                "IBKR: the sale of 2026-10-01 from the lot of 2026-02-10 (1 shares) - a loss, and the 30 days after the sale are not over: a purchase of the same fund in them would make it open (O9).",
            ],
            year2026.Remarks);
        Assert.Contains(year2026.Assumptions, a => a.Name == "repurchase_after_a_loss" && a.OpenQuestion == "O9");
        Assert.Contains(year2026.Assumptions, a => a.Name == "real_gain" && a.Source.Rule == "R2");
    }

    [Fact]
    public async Task ADayWithoutARateTakesTheLastOneBeforeItAndALaterCommissionStaysWithItsSale()
    {
        // The Bank of Israel published nothing for 2 September 2025: a buy of that day is converted at the 1st's rate,
        // carried and final, the 3rd's being stored (R3). Trades settle the next day; the earlier, the trade day, counts.
        // IBKR: 10 VT bought 2.9.2025 at $100 (+$1), sold 1.10.2025 at $95 (-$1, and -$0.50 the broker booked on
        // 5.1.2026); 2 SPY bought 1.9.2025 at $500, sold 1.10.2025 at $480, 1 SPY bought 10.10.2025 and cancelled.
        // 1 VT bought 1.12.2025 at $90, $1 cash in lieu on it 2.3.2026. Another broker: 3 VT bought 20.10.2025.
        var database = await pg.NewEmptyDatabaseAsync();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 13, 14, 0));
        await using var app = ITradeHost.Build(RunningHost.Args(database, RunningHost.NewDataDir()), services => services.AddSingleton<IClock>(clock));
        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var ibkr = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var other = new HoldingPlace(HoldingPlaceId.New(), "Another broker", HoldingPlaceKind.Broker, IntegrationKind.Manual);
        var vt = new Instrument(InstrumentId.New(), new Ticker("VT"), "ARCA", Currency.Usd, "Vanguard Total World Stock ETF");
        var spy = new Instrument(InstrumentId.New(), new Ticker("SPY"), "ARCA", Currency.Usd, "SPDR S&P 500 ETF Trust");
        var bank = services.GetRequiredService<IFxRateSource>();
        LocalDate On(int year, int month, int day) => new(year, month, day);
        FxRate Rate(LocalDate day, decimal rate) => new(bank.BaseCurrency, bank.QuoteCurrency, rate, day, bank.Name);
        await using (var db = DbSetup.Create(database))
        {
            db.HoldingPlaces.AddRange(HoldingPlaceRow.From(ibkr), HoldingPlaceRow.From(other));
            db.Instruments.AddRange(InstrumentRow.From(vt), InstrumentRow.From(spy));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await new FxRateStore(db).AddAsync(
                [Rate(On(2025, 9, 1), 3.6m), Rate(On(2025, 9, 3), 3.62m), Rate(On(2025, 10, 1), 3.5m), Rate(On(2025, 10, 2), 3.51m)],
                [], null, Stored, new FxRatesNote("test", FxRatesNote.Updated, "{}"), CancellationToken.None);
        }

        var n = 0;
        Transaction Entry(HoldingPlace place, TransactionKind kind, LocalDate day, decimal cash, Instrument? instrument = null, decimal shares = 0m, decimal? price = null, TransactionId? relatedTo = null) =>
            new(TransactionId.New(), place.Id, kind, day, day.PlusDays(1), Money.Usd(cash), TransactionSource.Flex, clock.GetCurrentInstant(),
                instrument?.Id, shares, price is { } p ? new Price(p, Currency.Usd) : null, $"E-{++n}", relatedTo: relatedTo);
        var vtBuy = Entry(ibkr, TransactionKind.Buy, On(2025, 9, 2), -1_000m, vt, 10m, 100m);
        var vtSale = Entry(ibkr, TransactionKind.Sell, On(2025, 10, 1), 950m, vt, -10m, 95m);
        var cancelled = Entry(ibkr, TransactionKind.Buy, On(2025, 10, 10), -500m, spy, 1m, 500m);
        var ledger = services.GetRequiredService<TransactionLedger>();
        await ledger.RecordAsync(
            [
                Entry(ibkr, TransactionKind.Deposit, On(2025, 8, 29), 3_000m),
                Entry(ibkr, TransactionKind.Buy, On(2025, 9, 1), -1_000m, spy, 2m, 500m),
                vtBuy, Entry(ibkr, TransactionKind.Fee, On(2025, 9, 2), -1m, vt, relatedTo: vtBuy.Id),
                vtSale, Entry(ibkr, TransactionKind.Fee, On(2025, 10, 1), -1m, vt, relatedTo: vtSale.Id),
                Entry(ibkr, TransactionKind.Sell, On(2025, 10, 1), 960m, spy, -2m, 480m),
                cancelled,
                Entry(ibkr, TransactionKind.Buy, On(2025, 12, 1), -90m, vt, 1m, 90m),
                Entry(ibkr, TransactionKind.Fee, On(2026, 1, 5), -0.5m, vt, relatedTo: vtSale.Id),
                Entry(ibkr, TransactionKind.CorporateAction, On(2026, 3, 2), 1m, vt),
                Entry(other, TransactionKind.Deposit, On(2025, 10, 1), 500m),
                Entry(other, TransactionKind.Buy, On(2025, 10, 20), -300m, vt, 3m, 100m),
            ],
            CancellationToken.None);
        await ledger.ReverseAsync(cancelled.Id, "the broker cancelled the trade", CancellationToken.None);
        var gains = services.GetRequiredService<CapitalGains>();

        var year = await gains.OfYearAsync(2025, CancellationToken.None);

        // The cash in lieu of 2026 cannot change a sale of 2025: the year stays computed.
        Assert.True(year.IsComplete);
        var (vtPart, spyPart) = (year.Sales.Single(s => s.Disposal.Instrument == vt.Id), year.Sales.Single(s => s.Disposal.Instrument == spy.Id));
        // The 1st's rate, carried one day, final.
        Assert.Equal((On(2025, 9, 1), 1, false), (vtPart.PurchaseRate.Rate.Date, vtPart.PurchaseRate.DaysCarried, vtPart.PurchaseRate.IsProvisional));
        // Paid $1,001 at 3.6 (₪3,603.60); received $948.50 at 3.5 (₪3,319.75), the January commission in it. The shekel
        // strengthened: only the dollar loss of $52.50 at 3.5, ₪183.75, is offsettable.
        Assert.Equal((3_603.6m, 3_319.75m, 0m, 183.75m), (vtPart.Cost, vtPart.Proceeds, vtPart.RealGain, vtPart.OffsettableLoss));
        // $1,000 at 3.6 for $960 at 3.5: $40 at 3.5, ₪140.
        Assert.Equal(140m, spyPart.OffsettableLoss);
        Assert.Equal(323.75m, year.OffsettableLoss);
        // VT was bought again at the other broker 19 days later; the cancelled SPY purchase is no repurchase.
        Assert.Equal(
            ["IBKR: the sale of 2025-10-01 from the lot of 2025-09-02 (10 shares) - a loss, and the same fund was bought within 30 days of the sale: whether it is offsettable in full is open (O9)."],
            year.Remarks);
        // The January commission is the October sale's; the next year has no sale of it.
        Assert.Empty((await gains.OfYearAsync(2026, CancellationToken.None)).Sales);
    }
}
