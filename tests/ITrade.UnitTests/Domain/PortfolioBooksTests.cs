using CsCheck;
using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Domain;

/// <summary>
/// P3.1.03: lots, positions and cash derived from the ledger — hand-worked examples (a partial sale, a split,
/// a sale after a split) and properties that hold for any ledger.
/// </summary>
public sealed class PortfolioBooksTests
{
    private static readonly HoldingPlaceId Ibkr = HoldingPlaceId.New();
    private static readonly InstrumentId Voo = InstrumentId.New();
    private static readonly InstrumentId Vxus = InstrumentId.New();
    private static readonly LocalDate Day = new(2026, 11, 2);
    private static readonly LocalDate Later = new(2027, 6, 30);
    private static readonly Instant Recorded = Instant.FromUtc(2027, 7, 1, 6, 0);

    private static Transaction Entry(
        TransactionKind kind, decimal cash, decimal quantity = 0m, InstrumentId? instrument = null, int day = 0,
        Currency? currency = null, TransactionId? relatedTo = null, HoldingPlaceId? place = null)
    {
        var traded = Day.PlusDays(day);
        var money = new Money(cash, currency ?? Currency.Usd);
        var price = kind is TransactionKind.Buy or TransactionKind.Sell
            ? new Price(Math.Round(Math.Abs(cash / quantity), Transaction.Scale), money.Currency)
            : (Price?)null;
        return new Transaction(
            TransactionId.New(), place ?? Ibkr, kind, traded, traded, money, TransactionSource.Manual, Recorded,
            instrument, quantity, price, relatedTo: relatedTo);
    }

    private static Transaction Buy(decimal shares, decimal price, int day = 0, InstrumentId? instrument = null) =>
        Entry(TransactionKind.Buy, -shares * price, shares, instrument ?? Voo, day);

    private static Transaction Sell(decimal shares, decimal price, int day = 0, InstrumentId? instrument = null) =>
        Entry(TransactionKind.Sell, shares * price, -shares, instrument ?? Voo, day);

    private static Transaction FeeOf(Transaction trade, decimal amount, int day = 0) =>
        Entry(TransactionKind.Fee, -amount, instrument: trade.Instrument, day: day, relatedTo: trade.Id);

    private static Transaction Split(decimal sharesChange, int day, InstrumentId? instrument = null) =>
        Entry(TransactionKind.CorporateAction, 0m, sharesChange, instrument ?? Voo, day);

    private static PortfolioBooks Books(params Transaction[] ledger) => PortfolioBooks.From(ledger, Later);

    [Fact]
    public void PurchaseOpensALotWithItsCostAndCommission()
    {
        var buy = Buy(2m, 601.7525m);

        var books = Books(buy, FeeOf(buy, 0.350753m));

        var position = Assert.Single(books.Positions);
        var lot = Assert.Single(position.Lots);
        Assert.Equal((buy.Id, Ibkr, Voo, Day), (lot.OpenedBy, lot.HoldingPlace, lot.Instrument, lot.Acquired));
        Assert.Equal((new Quantity(2m), Money.Usd(1203.505m), Money.Usd(0.350753m)), (lot.Quantity, lot.Cost, lot.Fees));
        Assert.Equal(601.7525m, lot.CostPerShare);
        Assert.Equal(Money.Usd(-1203.855753m), books.CashOf(Ibkr, Currency.Usd));
        Assert.Empty(books.Disposals);
        Assert.Equal(LotMatching.Fifo, books.Method);
    }

    [Fact]
    public void PartialSaleTakesTheEarliestSharesFirst()
    {
        // 10 @ 100 and 5 @ 120 are bought; 12 are sold @ 150: the whole first lot and 2 shares of the second.
        var first = Buy(10m, 100m);
        var second = Buy(5m, 120m, day: 30);
        var sale = Sell(12m, 150m, day: 60);

        var books = Books(first, FeeOf(first, 1m), second, FeeOf(second, 1m, day: 30), sale, FeeOf(sale, 1.2m, day: 60));

        Assert.Equal(2, books.Disposals.Count);
        var (whole, part) = (books.Disposals[0], books.Disposals[1]);
        Assert.Equal((sale.Id, first.Id, Day, Day.PlusDays(60)), (whole.Sale, whole.Lot, whole.Acquired, whole.Sold));
        Assert.Equal(
            (new Quantity(10m), Money.Usd(1000m), Money.Usd(1m), Money.Usd(1500m), Money.Usd(1m)),
            (whole.Quantity, whole.Cost, whole.PurchaseFees, whole.Proceeds, whole.SaleFees));
        Assert.Equal(Money.Usd(498m), whole.NominalGain);
        Assert.Equal((second.Id, Day.PlusDays(30)), (part.Lot, part.Acquired));
        Assert.Equal(
            (new Quantity(2m), Money.Usd(240m), Money.Usd(0.4m), Money.Usd(300m), Money.Usd(0.2m)),
            (part.Quantity, part.Cost, part.PurchaseFees, part.Proceeds, part.SaleFees));
        Assert.Equal(Money.Usd(59.4m), part.NominalGain);

        var left = Assert.Single(Assert.Single(books.Positions).Lots);
        Assert.Equal((second.Id, new Quantity(3m), Money.Usd(360m), Money.Usd(0.6m)), (left.OpenedBy, left.Quantity, left.Cost, left.Fees));
        Assert.Equal(Money.Usd(196.8m), books.CashOf(Ibkr, Currency.Usd)); // -1000 - 1 - 600 - 1 + 1800 - 1.2
    }

    [Fact]
    public void SplitChangesTheSharesAndThePriceOfALotAndNotItsCost()
    {
        var buy = Buy(10m, 100m);

        var books = Books(buy, Split(+10m, day: 20)); // 2 for 1

        var lot = Assert.Single(Assert.Single(books.Positions).Lots);
        Assert.Equal((new Quantity(20m), Money.Usd(1000m), 50m), (lot.Quantity, lot.Cost, lot.CostPerShare));
        Assert.Equal((buy.Id, Day), (lot.OpenedBy, lot.Acquired)); // still the same purchase, bought when it was bought
        Assert.Equal(Money.Usd(-1000m), books.CashOf(Ibkr, Currency.Usd));
    }

    [Fact]
    public void SaleAfterASplitTakesTheCostOfTheNewShares()
    {
        var buy = Buy(10m, 100m);
        var sale = Sell(5m, 60m, day: 40);

        var books = Books(buy, Split(+10m, day: 20), sale);

        var sold = Assert.Single(books.Disposals);
        Assert.Equal((new Quantity(5m), Money.Usd(250m), Money.Usd(300m)), (sold.Quantity, sold.Cost, sold.Proceeds));
        Assert.Equal(Money.Usd(50m), sold.NominalGain);
        var lot = Assert.Single(Assert.Single(books.Positions).Lots);
        Assert.Equal((new Quantity(15m), Money.Usd(750m), 50m), (lot.Quantity, lot.Cost, lot.CostPerShare));
    }

    [Fact]
    public void ReverseSplitSharesTheFewerSharesAmongTheLotsInProportion()
    {
        var books = Books(Buy(6m, 10m), Buy(2m, 12m, day: 1), Split(-6m, day: 20)); // 1 for 4

        var lots = Assert.Single(books.Positions).Lots;
        Assert.Equal([new Quantity(1.5m), new Quantity(0.5m)], lots.Select(l => l.Quantity));
        Assert.Equal([Money.Usd(60m), Money.Usd(24m)], lots.Select(l => l.Cost));
        Assert.Equal([40m, 48m], lots.Select(l => l.CostPerShare));
    }

    [Fact]
    public void SplitThatDoesNotDivideEvenlyStillAddsUpToTheNewNumberOfShares()
    {
        // 3 lots of 1 share become 7 shares: 7/3 has no end, the last lot takes what remains.
        var books = Books(Buy(1m, 10m), Buy(1m, 11m, day: 1), Buy(1m, 12m, day: 2), Split(+4m, day: 20));

        var position = Assert.Single(books.Positions);
        Assert.Equal(new Quantity(7m), position.Quantity);
        Assert.Equal(2.333333333333m, position.Lots[0].Quantity.Value);
        Assert.Equal(2.333333333334m, position.Lots[2].Quantity.Value);
        Assert.Equal(Money.Usd(33m), position.Cost);

        // Selling everything afterwards leaves nothing behind, not a sliver of a share.
        var sold = PortfolioBooks.From(
            [Buy(1m, 10m), Buy(1m, 11m, day: 1), Buy(1m, 12m, day: 2), Split(+4m, day: 20), Sell(7m, 6m, day: 30)], Later);
        Assert.Empty(sold.Positions);
        Assert.Equal(Money.Usd(33m), sold.Disposals.Select(d => d.Cost).Aggregate((a, b) => a + b));
        Assert.Equal(Money.Usd(42m), sold.Disposals.Select(d => d.Proceeds).Aggregate((a, b) => a + b));
    }

    [Fact]
    public void PartsThatDoNotDivideEvenlyStillAddUpToTheWhole()
    {
        // A third of the commission has no end: the part is kept to 12 places and the lot keeps the exact remainder.
        var buy = Buy(3m, 33.333333m);
        var sale = Sell(1m, 40m, day: 5);

        var books = Books(buy, FeeOf(buy, 1m), sale);

        var sold = Assert.Single(books.Disposals);
        var left = Assert.Single(Assert.Single(books.Positions).Lots);
        Assert.Equal(33.333333m, sold.Cost.Amount);
        Assert.Equal(0.333333333333m, sold.PurchaseFees.Amount);
        Assert.Equal(Money.Usd(99.999999m), sold.Cost + left.Cost);
        Assert.Equal(Money.Usd(1m), sold.PurchaseFees + left.Fees);
    }

    [Fact]
    public void WithinOneDayTheOrderOfTheLedgerDecidesWhichPurchaseWasFirst()
    {
        var cheap = Buy(1m, 100m);
        var dear = Buy(1m, 110m);
        var sale = Sell(1m, 120m);

        Assert.Equal(cheap.Id, Assert.Single(Books(cheap, dear, sale).Disposals).Lot);
        Assert.Equal(dear.Id, Assert.Single(Books(dear, cheap, sale).Disposals).Lot);
    }

    [Fact]
    public void CommissionRecordedAfterTheSaleStillBelongsToItsPurchase()
    {
        // A fee line may reach the ledger later than other entries of its day.
        var buy = Buy(2m, 100m);
        var sale = Sell(1m, 110m);

        var books = Books(buy, sale, FeeOf(buy, 1m), FeeOf(sale, 0.5m));

        var sold = Assert.Single(books.Disposals);
        Assert.Equal((Money.Usd(0.5m), Money.Usd(0.5m)), (sold.PurchaseFees, sold.SaleFees));
        Assert.Equal(Money.Usd(0.5m), Assert.Single(books.Positions).Fees);
    }

    [Fact]
    public void ReversedPurchaseLeavesNoLotAndNoCash()
    {
        var wrong = Buy(10m, 100m);
        var fee = FeeOf(wrong, 1m);
        var right = Buy(1m, 100m, day: 1);
        var undoBuy = wrong.Reversal(TransactionId.New(), Recorded, "entered ten instead of one");
        var undoFee = fee.Reversal(TransactionId.New(), Recorded, "entered ten instead of one");

        // The reversals carry the trade date of what they cancel, so they stand next to it in the ledger.
        var books = Books(wrong, fee, undoBuy, undoFee, right);

        var lot = Assert.Single(Assert.Single(books.Positions).Lots);
        Assert.Equal((right.Id, new Quantity(1m)), (lot.OpenedBy, lot.Quantity));
        Assert.Equal(Money.Usd(-100m), books.CashOf(Ibkr, Currency.Usd));
    }

    [Fact]
    public void BooksOfAnEarlierDateDoNotKnowWhatHappenedLater()
    {
        var buy = Buy(10m, 100m);
        Transaction[] ledger = [buy, Split(+10m, day: 20), Sell(5m, 60m, day: 40)];

        var before = PortfolioBooks.From(ledger, Day.PlusDays(-1));
        var bought = PortfolioBooks.From(ledger, Day);
        var split = PortfolioBooks.From(ledger, Day.PlusDays(20));
        var sold = PortfolioBooks.From(ledger, Day.PlusDays(40));

        Assert.Empty(before.Positions);
        Assert.Empty(before.Cash);
        Assert.Equal(Quantity.Zero, before.QuantityOf(Ibkr, Voo));
        Assert.Equal(Money.Zero(Currency.Usd), before.CashOf(Ibkr, Currency.Usd));
        Assert.Equal(new Quantity(10m), bought.QuantityOf(Ibkr, Voo));
        Assert.Equal(new Quantity(20m), split.QuantityOf(Ibkr, Voo));
        Assert.Empty(split.Disposals);
        Assert.Equal(new Quantity(15m), sold.QuantityOf(Ibkr, Voo));
        Assert.Equal(Day.PlusDays(40), sold.AsOf);
    }

    [Fact]
    public void CashIsKeptByCurrencyAndEveryKindOfEntryCounts()
    {
        var sold = Entry(TransactionKind.FxConversion, -4000m, currency: Currency.Ils, day: 1);
        var buy = Buy(2m, 601.7525m, day: 2);
        var dividend = Entry(TransactionKind.Dividend, 3.42m, instrument: Voo, day: 60);

        var books = Books(
            Entry(TransactionKind.Deposit, 4000m, currency: Currency.Ils),
            sold,
            Entry(TransactionKind.FxConversion, 1311.475409m, day: 1, relatedTo: sold.Id),
            Entry(TransactionKind.Fee, -2m, day: 1, relatedTo: sold.Id), // the fee of a conversion is cash, not a lot's
            buy,
            FeeOf(buy, 0.350753m, day: 2),
            dividend,
            Entry(TransactionKind.TaxWithheld, -0.855m, instrument: Voo, day: 60, relatedTo: dividend.Id),
            Entry(TransactionKind.Interest, 0.07m, day: 61),
            Entry(TransactionKind.Withdrawal, -50m, day: 62));

        Assert.Equal(
            [Money.Ils(0m), Money.Usd(58.254656m)], // a currency that was used stays listed, at zero
            books.Cash.Select(c => c.Amount));
        Assert.Equal(Money.Usd(0.350753m), Assert.Single(books.Positions).Fees);
    }

    [Fact]
    public void PlacesAndInstrumentsAreKeptApart()
    {
        var other = HoldingPlaceId.New();
        var elsewhere = Entry(TransactionKind.Buy, -500m, 5m, Voo, place: other);

        var books = Books(Buy(10m, 100m), Buy(4m, 50m, instrument: Vxus), elsewhere, Sell(3m, 110m, day: 9));

        Assert.Equal(new Quantity(7m), books.QuantityOf(Ibkr, Voo));
        Assert.Equal(new Quantity(4m), books.QuantityOf(Ibkr, Vxus));
        Assert.Equal(new Quantity(5m), books.QuantityOf(other, Voo)); // the sale at IBKR did not touch it
        Assert.Equal(Money.Usd(-870m), books.CashOf(Ibkr, Currency.Usd));
        Assert.Equal(Money.Usd(-500m), books.CashOf(other, Currency.Usd));
        Assert.Equal(3, books.Positions.Count);
    }

    [Fact]
    public void WhatTheLedgerCannotMeanIsRefused()
    {
        var buy = Buy(10m, 100m);

        var oversold = Assert.Throws<InvalidOperationException>(() => Books(buy, Sell(11m, 100m, day: 1)));
        Assert.Contains("sells 11 shares and 10 are held", oversold.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Books(Sell(1m, 100m))); // nothing was ever bought
        Assert.Throws<InvalidOperationException>(() => Books(Sell(1m, 100m), Buy(1m, 100m))); // sold before it was bought
        Assert.Throws<InvalidOperationException>(() => Books(Split(+10m, day: 1))); // a split of something not held
        Assert.Throws<InvalidOperationException>(() => Books(buy, Split(-10m, day: 1))); // all the shares vanish
        var mixed = Assert.Throws<InvalidOperationException>(() =>
            Books(buy, Entry(TransactionKind.CorporateAction, 5m, -0.5m, Voo, day: 1)));
        Assert.Contains("not supported yet", mixed.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            Books(buy, Entry(TransactionKind.Fee, -1m, currency: Currency.Ils, relatedTo: buy.Id)));
        Assert.Throws<InvalidOperationException>(() =>
            Books(buy, Entry(TransactionKind.Sell, 4000m, -1m, Voo, day: 1, currency: Currency.Ils)));

        // What happens after the date asked for cannot break the books of that date.
        Assert.Equal(new Quantity(10m), PortfolioBooks.From([buy, Sell(11m, 100m, day: 1)], Day).QuantityOf(Ibkr, Voo));
    }

    [Fact]
    public void LedgerOutOfOrderIsRefused()
    {
        var error = Assert.Throws<ArgumentException>(() => Books(Buy(1m, 100m, day: 5), Buy(1m, 100m, day: 4)));
        Assert.Contains("not in the ledger's order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CashOfACorporateActionIsCountedAndNamedAsNotWorkedIntoTheLots()
    {
        // 10 shares for 1000; a 1-for-4 reverse split leaves 2 shares and pays 200 in lieu of the half share.
        // Really half a share was sold for 200: the cost of the lot and the realised result are not complete.
        var inLieu = Entry(TransactionKind.CorporateAction, 200m, instrument: Voo, day: 3);

        var books = Books(Buy(10m, 100m), Split(-8m, day: 3), inLieu);

        Assert.Equal(new Quantity(2m), books.QuantityOf(Ibkr, Voo));
        Assert.Equal(Money.Usd(-800m), books.CashOf(Ibkr, Currency.Usd)); // the cash is right
        Assert.Equal(Money.Usd(1000m), Assert.Single(books.Positions).Cost); // the cost is not: 200 of it went with the half share
        Assert.Empty(books.Disposals);
        Assert.Equal([inLieu], books.NotInTheLots); // and the books say so
        Assert.Empty(Books(Buy(10m, 100m), Split(-8m, day: 3)).NotInTheLots);
    }

    [Fact]
    public void SplitListedAfterATradeOfItsDayIsRefused()
    {
        // 10 shares held, cost 1000. On the day of a 2-for-1 split one more share is bought at the new price.
        var held = Buy(10m, 100m);
        var bought = Buy(1m, 50m, day: 20);
        var split = Split(+10m, day: 20);

        // In the right order every lot is right: 20 shares that cost 1000, and 1 share that cost 50.
        var lots = Assert.Single(Books(held, split, bought).Positions).Lots;
        Assert.Equal([(new Quantity(20m), Money.Usd(1000m)), (new Quantity(1m), Money.Usd(50m))], lots.Select(l => (l.Quantity, l.Cost)));

        // The other way round the ratio would come out as 21/11 and the lots as 19.09 and 1.91 shares, while
        // the totals (21 shares, 1050) looked right - so it is refused instead.
        var error = Assert.Throws<InvalidOperationException>(() => Books(held, bought, split));
        Assert.Contains("before the trades of its day", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Books(held, Sell(1m, 110m, day: 20), split));
        // A trade of another instrument that day, or a purchase that was reversed, does not stand in the way.
        var undone = Buy(1m, 50m, day: 20);
        Assert.Equal(new Quantity(20m), Books(held, Buy(1m, 30m, day: 20, instrument: Vxus), split).QuantityOf(Ibkr, Voo));
        Assert.Equal(
            new Quantity(20m),
            Books(held, undone, undone.Reversal(TransactionId.New(), Recorded, "entered by mistake"), split).QuantityOf(Ibkr, Voo));
    }

    private sealed record Step(int Kind, long Shares, long Price, long Fee);

    private static readonly Gen<Step> Steps = Gen.Select(
        Gen.Int[0, 9], Gen.Long[1, 5_000_000], Gen.Long[1_000_000, 900_000_000], Gen.Long[0, 3_000_000],
        (kind, shares, price, fee) => new Step(kind, shares, price, fee));

    /// <summary>A ledger that can happen: purchases, sales of at most what is held, splits and reverse splits.</summary>
    private static List<Transaction> LedgerOf(IEnumerable<Step> steps)
    {
        var ledger = new List<Transaction>();
        var held = 0m;
        var day = 0;
        foreach (var step in steps)
        {
            day++;
            var shares = step.Shares / 10_000m; // up to 500 shares, four decimal places
            var price = step.Price / 1_000_000m; // 1.000000 … 900.000000
            if (step.Kind <= 4 || held == 0m)
            {
                var buy = Entry(TransactionKind.Buy, -decimal.Round(shares * price, Transaction.Scale), shares, Voo, day);
                ledger.Add(buy);
                held += shares;
                if (step.Fee > 0)
                {
                    ledger.Add(FeeOf(buy, step.Fee / 1_000_000m, day));
                }
            }
            else if (step.Kind <= 7)
            {
                var sold = decimal.Round(Math.Min(held, shares), 4);
                var sale = Entry(TransactionKind.Sell, decimal.Round(sold * price, Transaction.Scale), -sold, Voo, day);
                ledger.Add(sale);
                held -= sold;
                if (step.Fee > 0)
                {
                    ledger.Add(FeeOf(sale, step.Fee / 1_000_000m, day));
                }
            }
            else
            {
                var change = step.Kind == 8 ? held * 2m : -decimal.Round(held * 0.75m, 4); // 3 for 1, or about 1 for 4
                if (held + change > 0m && change != 0m)
                {
                    ledger.Add(Split(change, day));
                    held += change;
                }
            }

            day++; // a split never shares its day with a trade
        }

        return ledger;
    }

    [Fact]
    public void ForAnyLedgerTheLotsAndTheMatchedSalesAddUpExactly()
    {
        Steps.List[1, 40].Sample(steps =>
        {
            var ledger = LedgerOf(steps);
            var books = PortfolioBooks.From(ledger, Later);
            var lots = books.Positions.SelectMany(p => p.Lots).ToList();

            // Shares: what is held is the sum of every change of shares in the ledger.
            var sharesOk = lots.Sum(l => l.Quantity.Value) == ledger.Sum(e => e.QuantityChange);
            // Cash: the plain sum of the ledger.
            var cashOk = books.CashOf(Ibkr, Currency.Usd).Amount == ledger.Sum(e => e.Cash.Amount);
            // Cost: every dollar paid for shares is either still in a lot or in the cost of a matched sale.
            var paid = -ledger.Where(e => e.Kind == TransactionKind.Buy).Sum(e => e.Cash.Amount);
            var costOk = lots.Sum(l => l.Cost.Amount) + books.Disposals.Sum(d => d.Cost.Amount) == paid;
            // Proceeds: the parts of the sales add up to the sales.
            var received = ledger.Where(e => e.Kind == TransactionKind.Sell).Sum(e => e.Cash.Amount);
            var proceedsOk = books.Disposals.Sum(d => d.Proceeds.Amount) == received;
            // Commissions: each one is in a lot, in a matched sale as a purchase fee, or in a sale as its own fee.
            var fees = -ledger.Where(e => e.Kind == TransactionKind.Fee).Sum(e => e.Cash.Amount);
            var feesOk = lots.Sum(l => l.Fees.Amount) + books.Disposals.Sum(d => d.PurchaseFees.Amount + d.SaleFees.Amount) == fees;
            // No lot is empty, negative or out of order.
            var lotsOk = lots.All(l => l.Quantity.Value > 0m && l.Cost.Amount >= 0m && l.Fees.Amount >= 0m)
                && lots.Select(l => l.Acquired).SequenceEqual(lots.Select(l => l.Acquired).Order());
            return sharesOk && cashOk && costOk && proceedsOk && feesOk && lotsOk;
        });
    }

    [Fact]
    public void ForAnyLedgerASaleInTwoPartsTakesTheSameSharesFromTheSameLotsAsOneSale()
    {
        Gen.Select(Steps.List[1, 20], Gen.Long[1, 9_999], Gen.Long[1, 9_999]).Sample((steps, share, cut) =>
        {
            var ledger = LedgerOf(steps);
            var day = (2 * ledger.Count) + 100;
            var held = PortfolioBooks.From(ledger, Later).QuantityOf(Ibkr, Voo).Value;
            // A part of what is held is sold - so lots stay behind - once whole and once in two pieces.
            var sold = decimal.Round(held * share / 10_000m, Transaction.Scale, MidpointRounding.ToZero);
            var firstPiece = decimal.Round(sold * cut / 10_000m, Transaction.Scale, MidpointRounding.ToZero);
            if (firstPiece == 0m || firstPiece == sold)
            {
                return true;
            }

            var once = PortfolioBooks.From([.. ledger, Sell(sold, 100m, day)], Later);
            var twice = PortfolioBooks.From([.. ledger, Sell(firstPiece, 100m, day), Sell(sold - firstPiece, 100m, day)], Later);
            var (lotsOnce, lotsTwice) = (LotsLeft(once), LotsLeft(twice));
            var (takenOnce, takenTwice) = (TakenFromEachLot(once, day), TakenFromEachLot(twice, day));
            // The same lots are left with exactly the same shares, and their cost differs by rounding at most
            // (a part is kept to 12 places; the remainder stays in the lot).
            return lotsOnce.Count > 0
                && lotsOnce.Select(l => (l.OpenedBy, l.Quantity)).SequenceEqual(lotsTwice.Select(l => (l.OpenedBy, l.Quantity)))
                && lotsOnce.Zip(lotsTwice).All(pair => Math.Abs(pair.First.Cost.Amount - pair.Second.Cost.Amount) < Tolerance)
                // And the same shares at the same cost were taken from each lot.
                && takenOnce.Keys.Order().SequenceEqual(takenTwice.Keys.Order())
                && takenOnce.All(t => t.Value.Shares == takenTwice[t.Key].Shares
                    && Math.Abs(t.Value.Cost - takenTwice[t.Key].Cost) < Tolerance);
        });

        static List<Lot> LotsLeft(PortfolioBooks books) => books.Positions.SelectMany(p => p.Lots).ToList();

        static Dictionary<Guid, (decimal Shares, decimal Cost)> TakenFromEachLot(PortfolioBooks books, int day) =>
            books.Disposals
                .Where(d => d.Sold == Day.PlusDays(day))
                .GroupBy(d => d.Lot.Value)
                .ToDictionary(g => g.Key, g => (g.Sum(d => d.Quantity.Value), g.Sum(d => d.Cost.Amount)));
    }

    private const decimal Tolerance = 0.000_000_001m;
}
