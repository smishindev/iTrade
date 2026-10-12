using ITrade.Application.Portfolio;
using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.IntegrationTests.Portfolio;

/// <summary>
/// P3.1.03 on a real database: the books are computed from the ledger as the database returns it — the order
/// within a day, repeated imports and reversals included.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class BooksTests(PostgresFixture pg)
{
    private static readonly LocalDate Traded = new(2026, 11, 2);

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 11, 3, 6, 0));

    private sealed record World(ITradeDbContext Db, TransactionLedger Ledger, PortfolioBooksReader Books, HoldingPlaceId Ibkr, InstrumentId Voo)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<World> NewAsync()
    {
        var db = DbSetup.Create(await pg.NewEmptyDatabaseAsync());
        await db.Database.MigrateAsync();
        var place = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var voo = new Instrument(InstrumentId.New(), new Ticker("VOO"), "ARCA", Currency.Usd, "Vanguard S&P 500 ETF");
        db.HoldingPlaces.Add(HoldingPlaceRow.From(place));
        db.Instruments.Add(InstrumentRow.From(voo));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new TransactionStore(db);
        return new World(db, new TransactionLedger(store, _clock), new PortfolioBooksReader(store), place.Id, voo.Id);
    }

    private Transaction Trade(World w, string externalId, decimal shares, decimal price, int day = 0) =>
        new(
            TransactionId.New(), w.Ibkr, shares > 0m ? TransactionKind.Buy : TransactionKind.Sell, Traded.PlusDays(day),
            Traded.PlusDays(day + 1), Money.Usd(-shares * price), TransactionSource.Flex, _clock.GetCurrentInstant(), w.Voo,
            shares, new Price(price, Currency.Usd), externalId);

    private Transaction FeeOf(Transaction trade, decimal amount) =>
        new(
            TransactionId.New(), trade.HoldingPlace, TransactionKind.Fee, trade.TradeDate, trade.SettlementDate,
            Money.Usd(-amount), TransactionSource.Flex, _clock.GetCurrentInstant(), trade.Instrument,
            externalId: trade.ExternalId + ":fee", relatedTo: trade.Id);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaleTakesThePurchaseTheStatementListedFirstThatDay(bool dearFirst)
    {
        // Two purchases on one day, written in one batch at one moment: only the order of the statement tells
        // them apart, and it has to survive the database.
        await using var w = await NewAsync();
        var cheap = Trade(w, "T-cheap", 1m, 100m);
        var dear = Trade(w, "T-dear", 1m, 110m);
        Transaction[] purchases = dearFirst ? [dear, FeeOf(dear, 0.35m), cheap] : [cheap, dear, FeeOf(dear, 0.35m)];
        await w.Ledger.RecordAsync(purchases, CancellationToken.None);
        var sale = Trade(w, "T-sale", -1m, 120m, day: 3);
        await w.Ledger.RecordAsync([sale, FeeOf(sale, 0.35m)], CancellationToken.None);

        var books = await w.Books.AsOfAsync(w.Ibkr, Traded.PlusDays(3), CancellationToken.None);

        var sold = Assert.Single(books.Disposals);
        var (first, second) = dearFirst ? (dear, cheap) : (cheap, dear);
        Assert.Equal((first.Id, Money.Usd(-first.Cash.Amount), Money.Usd(120m), Money.Usd(0.35m)), (sold.Lot, sold.Cost, sold.Proceeds, sold.SaleFees));
        var left = Assert.Single(Assert.Single(books.Positions).Lots);
        Assert.Equal((second.Id, new Quantity(1m), Money.Usd(-second.Cash.Amount)), (left.OpenedBy, left.Quantity, left.Cost));
        Assert.Equal(Money.Usd(dearFirst ? 0m : 0.35m), left.Fees); // the commission stays with the purchase it was charged for
        Assert.Equal(Money.Usd(-100m - 110m + 120m - 0.70m), books.CashOf(w.Ibkr, Currency.Usd));

        // The day before the sale the books still hold both lots, in the statement's order.
        var before = await w.Books.AsOfAsync(w.Ibkr, Traded.PlusDays(2), CancellationToken.None);
        Assert.Equal([first.Id, second.Id], Assert.Single(before.Positions).Lots.Select(l => l.OpenedBy));
        Assert.Empty(before.Disposals);
    }

    [Fact]
    public async Task ImportingAgainAndReversingAMistakeLeaveTheBooksRight()
    {
        await using var w = await NewAsync();
        Transaction[] Statement()
        {
            var deposit = new Transaction(
                TransactionId.New(), w.Ibkr, TransactionKind.Deposit, Traded.PlusDays(-1), Traded.PlusDays(-1), Money.Usd(2000m),
                TransactionSource.Flex, _clock.GetCurrentInstant(), externalId: "D-1");
            var buy = Trade(w, "T-1", 2m, 601.7525m);
            return [deposit, buy, FeeOf(buy, 0.350753m)];
        }

        await w.Ledger.RecordAsync(Statement(), CancellationToken.None);
        await w.Ledger.RecordAsync(Statement(), CancellationToken.None); // the same statement once more
        var once = await w.Books.AsOfAsync(w.Ibkr, Traded, CancellationToken.None);

        Assert.Equal(new Quantity(2m), once.QuantityOf(w.Ibkr, w.Voo));
        Assert.Equal(Money.Usd(796.144247m), once.CashOf(w.Ibkr, Currency.Usd)); // 2000 - 1203.505 - 0.350753
        Assert.Equal(Money.Usd(0.350753m), Assert.Single(once.Positions).Fees);

        // The broker cancels the trade: it and its commission are reversed, and the books are as if it never was.
        var buyId = Assert.Single(once.Positions).Lots[0].OpenedBy;
        _clock.AdvanceHours(4);
        await w.Ledger.ReverseAsync(buyId, "the broker cancelled the trade", CancellationToken.None);
        var after = await w.Books.AsOfAsync(w.Ibkr, Traded, CancellationToken.None);

        Assert.Empty(after.Positions);
        Assert.Equal(Money.Usd(2000m), after.CashOf(w.Ibkr, Currency.Usd));
        Assert.Empty((await w.Books.AsOfAsync(HoldingPlaceId.New(), Traded, CancellationToken.None)).Cash);
    }
}
