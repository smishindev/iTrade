using ITrade.Application.Portfolio;
using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace ITrade.IntegrationTests.Portfolio;

/// <summary>P3.1.02 on a real database: the ledger records a statement once and cancels by reversal.</summary>
[Collection(PostgresTests.Name)]
public sealed class LedgerTests(PostgresFixture pg)
{
    private static readonly LocalDate Traded = new(2026, 10, 12);

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 10, 13, 6, 0));

    private sealed record World(string Database, ITradeDbContext Db, TransactionLedger Ledger, TransactionStore Store, HoldingPlaceId Ibkr, InstrumentId Voo)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<World> NewAsync()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        var db = DbSetup.Create(database);
        await db.Database.MigrateAsync();
        var place = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var voo = new Instrument(InstrumentId.New(), new Ticker("VOO"), "ARCA", Currency.Usd, "Vanguard S&P 500 ETF");
        db.HoldingPlaces.Add(HoldingPlaceRow.From(place));
        db.Instruments.Add(InstrumentRow.From(voo));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new TransactionStore(db);
        return new World(database, db, new TransactionLedger(store, _clock), store, place.Id, voo.Id);
    }

    private Transaction Entry(
        World w, TransactionKind kind, decimal cash, string? externalId, Currency? currency = null, decimal quantity = 0m,
        decimal? price = null, bool instrument = false, TransactionId? relatedTo = null, LocalDate? traded = null) =>
        new(
            TransactionId.New(), w.Ibkr, kind, traded ?? Traded, (traded ?? Traded).PlusDays(1),
            new Money(cash, currency ?? Currency.Usd), externalId is null ? TransactionSource.Manual : TransactionSource.Flex,
            _clock.GetCurrentInstant(), instrument ? w.Voo : null, quantity,
            price is { } p ? new Price(p, currency ?? Currency.Usd) : null, externalId, relatedTo);

    /// <summary>A month of a statement, built afresh on every call as an import would build it.</summary>
    private Transaction[] Statement(World w)
    {
        var deposit = Entry(w, TransactionKind.Deposit, 4000m, "D-1", Currency.Ils, traded: Traded.PlusDays(-3));
        var sold = Entry(w, TransactionKind.FxConversion, -4000m, "FX-1", Currency.Ils, traded: Traded.PlusDays(-2));
        var bought = Entry(w, TransactionKind.FxConversion, 1311.475409m, "FX-1:in", relatedTo: sold.Id, traded: Traded.PlusDays(-2));
        var buy = Entry(w, TransactionKind.Buy, -1203.505m, "T-1", quantity: 2m, price: 601.7525m, instrument: true);
        var fee = Entry(w, TransactionKind.Fee, -0.350753m, "T-1:fee", instrument: true, relatedTo: buy.Id);
        var dividend = Entry(w, TransactionKind.Dividend, 3.42m, "DIV-1", instrument: true, traded: Traded.PlusDays(20));
        var tax = Entry(w, TransactionKind.TaxWithheld, -0.855m, "DIV-1:tax", instrument: true, relatedTo: dividend.Id, traded: Traded.PlusDays(20));
        return [deposit, sold, bought, buy, fee, dividend, tax];
    }

    [Fact]
    public async Task StatementImportedAgainAndAgainIsInTheLedgerOnce()
    {
        await using var w = await NewAsync();

        var first = await w.Ledger.RecordAsync(Statement(w), CancellationToken.None);
        var second = await w.Ledger.RecordAsync(Statement(w), CancellationToken.None);
        var third = await w.Ledger.RecordAsync(Statement(w), CancellationToken.None);

        Assert.Equal((7, 0, 0), (first.Added, first.AlreadyRecorded, first.NotRecorded.Count));
        Assert.Equal((0, 7, 0), (second.Added, second.AlreadyRecorded, second.NotRecorded.Count));
        Assert.Equal((0, 7, 0), (third.Added, third.AlreadyRecorded, third.NotRecorded.Count));
        var ledger = await w.Store.ListAsync(w.Ibkr, CancellationToken.None);
        Assert.Equal(
            ["D-1", "FX-1", "FX-1:in", "T-1", "T-1:fee", "DIV-1", "DIV-1:tax"],
            ledger.Select(t => t.ExternalId));
        // Exact to the last digit, and by currency: dollars left after the purchase, no shekels.
        Assert.Equal(0m, ledger.Where(t => t.Cash.Currency == Currency.Ils).Sum(t => t.Cash.Amount));
        Assert.Equal(110.184656m, ledger.Where(t => t.Cash.Currency == Currency.Usd).Sum(t => t.Cash.Amount));
        Assert.Equal(2m, ledger.Sum(t => t.QuantityChange));
        var fee = ledger.Single(t => t.ExternalId == "T-1:fee");
        Assert.Equal(ledger.Single(t => t.ExternalId == "T-1").Id, fee.RelatedTo);
    }

    [Fact]
    public async Task LineOfALaterStatementHangsOnTheTradeRecordedEarlier()
    {
        await using var w = await NewAsync();
        await w.Ledger.RecordAsync([Entry(w, TransactionKind.Buy, -1203.505m, "T-1", quantity: 2m, price: 601.7525m, instrument: true)], CancellationToken.None);
        var stored = (await w.Store.ListAsync(w.Ibkr, CancellationToken.None)).Single();
        var again = Entry(w, TransactionKind.Buy, -1203.505m, "T-1", quantity: 2m, price: 601.7525m, instrument: true);
        var fee = Entry(w, TransactionKind.Fee, -0.35m, "T-1:fee", instrument: true, relatedTo: again.Id);

        var result = await w.Ledger.RecordAsync([again, fee], CancellationToken.None);

        Assert.Equal((1, 1), (result.Added, result.AlreadyRecorded));
        var related = Assert.Single(await w.Store.ListRelatedToAsync(stored.Id, CancellationToken.None));
        Assert.Equal("T-1:fee", related.ExternalId);
    }

    [Fact]
    public async Task ChangedLineIsReportedAndNotWritten()
    {
        await using var w = await NewAsync();
        await w.Ledger.RecordAsync(Statement(w), CancellationToken.None);
        var changed = Statement(w);
        changed[5] = Entry(w, TransactionKind.Dividend, 3.52m, "DIV-1", instrument: true, traded: Traded.PlusDays(20));

        var result = await w.Ledger.RecordAsync(changed, CancellationToken.None);

        Assert.Equal((0, 6), (result.Added, result.AlreadyRecorded));
        Assert.Contains("Flex DIV-1", Assert.Single(result.NotRecorded), StringComparison.Ordinal);
        var dividend = await w.Store.FindByExternalIdAsync(w.Ibkr, TransactionSource.Flex, "DIV-1", CancellationToken.None);
        Assert.Equal(3.42m, dividend!.Cash.Amount);
    }

    [Fact]
    public async Task ReversalIsWrittenNextToWhatItCancels()
    {
        await using var w = await NewAsync();
        await w.Ledger.RecordAsync(Statement(w), CancellationToken.None);
        var buy = (await w.Store.FindByExternalIdAsync(w.Ibkr, TransactionSource.Flex, "T-1", CancellationToken.None))!;
        _clock.AdvanceHours(5);

        var reversals = await w.Ledger.ReverseAsync(buy.Id, "the broker cancelled the trade", CancellationToken.None);

        Assert.Equal(2, reversals.Count);
        var ledger = await w.Store.ListAsync(w.Ibkr, CancellationToken.None);
        Assert.Equal(9, ledger.Count);
        Assert.Equal(0m, ledger.Sum(t => t.QuantityChange));
        Assert.Equal(1311.475409m + 3.42m - 0.855m, ledger.Where(t => t.Cash.Currency == Currency.Usd).Sum(t => t.Cash.Amount));
        Assert.Equal(reversals[0], await w.Store.FindReversalOfAsync(buy.Id, CancellationToken.None));
        Assert.Equal(buy, await w.Store.FindAsync(buy.Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Ledger.ReverseAsync(buy.Id, "again", CancellationToken.None));

        // Past the ledger's own check — two sessions at once — the database still allows one reversal only.
        var racing = buy.Reversal(TransactionId.New(), _clock.GetCurrentInstant(), "a second session");
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => w.Store.AddAsync([racing], CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (refused.InnerException as PostgresException)?.SqlState);
        Assert.Equal(9, (await w.Store.ListAsync(w.Ibkr, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task BatchWithOneImpossibleEntryIsNotRecordedAtAll()
    {
        await using var w = await NewAsync();
        var good = Entry(w, TransactionKind.Deposit, 1000m, "D-1");
        var unknownInstrument = new Transaction(
            TransactionId.New(), w.Ibkr, TransactionKind.Dividend, Traded, Traded, Money.Usd(1m), TransactionSource.Flex,
            _clock.GetCurrentInstant(), InstrumentId.New(), externalId: "DIV-9");

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            w.Ledger.RecordAsync([good, unknownInstrument], CancellationToken.None));

        Assert.Empty(await w.Store.ListAsync(w.Ibkr, CancellationToken.None));
        // The same session goes on working, and the refused entries do not come back with the next save.
        var result = await w.Ledger.RecordAsync([Entry(w, TransactionKind.Deposit, 1000m, "D-1")], CancellationToken.None);
        Assert.Equal(1, result.Added);
        Assert.Single(await w.Store.ListAsync(w.Ibkr, CancellationToken.None));
    }

    [Fact]
    public async Task LineTheBrokerCorrectedIsRecordedAgainAfterItsOldRecordingIsReversed()
    {
        // The way a report of a changed line tells the owner to go, to its end, against the real unique index.
        await using var w = await NewAsync();
        Transaction[] Corrected()
        {
            var statement = Statement(w);
            statement[3] = Entry(w, TransactionKind.Buy, -1203.605m, "T-1", quantity: 2m, price: 601.8025m, instrument: true);
            statement[4] = Entry(w, TransactionKind.Fee, -0.350753m, "T-1:fee", instrument: true, relatedTo: statement[3].Id);
            return statement;
        }

        await w.Ledger.RecordAsync(Statement(w), CancellationToken.None);
        var reported = await w.Ledger.RecordAsync(Corrected(), CancellationToken.None);
        Assert.Equal((0, 6, 1), (reported.Added, reported.AlreadyRecorded, reported.NotRecorded.Count));
        var old = (await w.Store.FindByExternalIdAsync(w.Ibkr, TransactionSource.Flex, "T-1", CancellationToken.None))!;
        Assert.Contains($"reverse the entry {old.Id}", reported.NotRecorded[0], StringComparison.Ordinal);

        await w.Ledger.ReverseAsync(old.Id, "the broker corrected the price", CancellationToken.None);
        var recorded = await w.Ledger.RecordAsync(Corrected(), CancellationToken.None);
        var again = await w.Ledger.RecordAsync(Corrected(), CancellationToken.None);

        Assert.Equal((2, 5, 0), (recorded.Added, recorded.AlreadyRecorded, recorded.NotRecorded.Count));
        Assert.Equal((0, 7, 0), (again.Added, again.AlreadyRecorded, again.NotRecorded.Count));
        var buy = (await w.Store.FindByExternalIdAsync(w.Ibkr, TransactionSource.Flex, "T-1", CancellationToken.None))!;
        var fee = (await w.Store.FindByExternalIdAsync(w.Ibkr, TransactionSource.Flex, "T-1:fee", CancellationToken.None))!;
        Assert.Equal((-1203.605m, 1, 1, buy.Id), (buy.Cash.Amount, buy.ExternalRevision, fee.ExternalRevision, fee.RelatedTo!.Value));
        var ledger = await w.Store.ListAsync(w.Ibkr, CancellationToken.None);
        Assert.Equal(11, ledger.Count); // 7 lines, 2 reversals, 2 recorded again
        Assert.Equal(2m, ledger.Sum(t => t.QuantityChange));
        Assert.Equal(110.184656m - 0.10m, ledger.Where(t => t.Cash.Currency == Currency.Usd).Sum(t => t.Cash.Amount));

        // A reversed line that the source still states unchanged is reported, not passed off as recorded.
        await w.Ledger.ReverseAsync(buy.Id, "the broker cancelled the trade", CancellationToken.None);
        var cancelled = await w.Ledger.RecordAsync(Corrected(), CancellationToken.None);
        Assert.Equal((0, 5, 2), (cancelled.Added, cancelled.AlreadyRecorded, cancelled.NotRecorded.Count));
        Assert.Contains("was reversed (the broker cancelled the trade)", cancelled.NotRecorded[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversionIsCancelledWholeAndNotByOneLeg()
    {
        await using var w = await NewAsync();
        await w.Ledger.RecordAsync(Statement(w), CancellationToken.None);
        var sold = (await w.Store.FindByExternalIdAsync(w.Ibkr, TransactionSource.Flex, "FX-1", CancellationToken.None))!;
        var bought = (await w.Store.FindByExternalIdAsync(w.Ibkr, TransactionSource.Flex, "FX-1:in", CancellationToken.None))!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Ledger.ReverseAsync(bought.Id, "wrong rate", CancellationToken.None));
        Assert.Equal(7, (await w.Store.ListAsync(w.Ibkr, CancellationToken.None)).Count);

        await w.Ledger.ReverseAsync(sold.Id, "wrong rate", CancellationToken.None);

        var ledger = await w.Store.ListAsync(w.Ibkr, CancellationToken.None);
        Assert.Equal(4000m, ledger.Where(t => t.Cash.Currency == Currency.Ils).Sum(t => t.Cash.Amount)); // the deposit is back in shekels
        Assert.Equal(110.184656m - 1311.475409m, ledger.Where(t => t.Cash.Currency == Currency.Usd).Sum(t => t.Cash.Amount));
    }

    [Fact]
    public async Task LedgerIsWrittenOnItsOwnAndInsideATransactionABatchIsStillAllOrNothing()
    {
        await using var w = await NewAsync();

        // Other unsaved changes of the same unit of work would be saved with the first row, or lost with a refused one.
        w.Db.Settings.Add(new Setting { Key = "test", Value = "1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Ledger.RecordAsync([Entry(w, TransactionKind.Deposit, 1m, "D-0")], CancellationToken.None));
        w.Db.ChangeTracker.Clear();

        await using (var outer = await w.Db.Database.BeginTransactionAsync())
        {
            await w.Ledger.RecordAsync([Entry(w, TransactionKind.Deposit, 1000m, "D-1")], CancellationToken.None);
            var unknownInstrument = new Transaction(
                TransactionId.New(), w.Ibkr, TransactionKind.Dividend, Traded, Traded, Money.Usd(1m), TransactionSource.Flex,
                _clock.GetCurrentInstant(), InstrumentId.New(), externalId: "DIV-9");
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                w.Ledger.RecordAsync([Entry(w, TransactionKind.Deposit, 2000m, "D-2"), unknownInstrument], CancellationToken.None));

            // The caller carries on and commits: the refused batch must not have left its first half behind.
            await outer.CommitAsync();
        }

        var deposit = Assert.Single(await w.Store.ListAsync(w.Ibkr, CancellationToken.None));
        Assert.Equal("D-1", deposit.ExternalId);
        Assert.Empty(w.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LedgerIsListedInTheOrderThingsHappened()
    {
        await using var w = await NewAsync();
        var later = Entry(w, TransactionKind.Deposit, 2m, null, traded: Traded.PlusDays(5));
        var earlier = Entry(w, TransactionKind.Deposit, 1m, null, traded: Traded.PlusDays(-5));
        await w.Ledger.RecordAsync([later], CancellationToken.None);
        _clock.AdvanceMinutes(1);
        await w.Ledger.RecordAsync([earlier, Entry(w, TransactionKind.Deposit, 3m, null, traded: Traded.PlusDays(5))], CancellationToken.None);

        var ledger = await w.Store.ListAsync(w.Ibkr, CancellationToken.None);

        Assert.Equal([1m, 2m, 3m], ledger.Select(t => t.Cash.Amount));
        Assert.Empty(await w.Store.ListAsync(HoldingPlaceId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task EntriesOfOneDayStayInTheOrderOfTheStatement()
    {
        // Same trade date, same moment of recording: only the order they were given in tells them apart, and
        // first-in-first-out matching of lots depends on it.
        await using var w = await NewAsync();
        var amounts = Enumerable.Range(1, 40).Select(i => (decimal)i).ToList();

        await w.Ledger.RecordAsync(amounts.Take(25).Select(a => Entry(w, TransactionKind.Deposit, a, $"D-{a}")), CancellationToken.None);
        await w.Ledger.RecordAsync(amounts.Skip(25).Select(a => Entry(w, TransactionKind.Deposit, a, null)), CancellationToken.None);

        var ledger = await w.Store.ListAsync(w.Ibkr, CancellationToken.None);
        Assert.Equal(amounts, ledger.Select(t => t.Cash.Amount));
    }
}
