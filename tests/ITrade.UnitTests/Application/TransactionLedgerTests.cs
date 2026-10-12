using CsCheck;
using ITrade.Application.Portfolio;
using ITrade.Domain;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.UnitTests.Application;

/// <summary>P3.1.02: a repeated import records nothing twice, and a mistake is cancelled, not edited.</summary>
public sealed class TransactionLedgerTests
{
    private static readonly HoldingPlaceId Ibkr = HoldingPlaceId.New();
    private static readonly InstrumentId Voo = InstrumentId.New();
    private static readonly LocalDate Traded = new(2026, 10, 12);

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 10, 13, 6, 0));
    private readonly MemoryStore _store = new();
    private readonly TransactionLedger _ledger;

    public TransactionLedgerTests() => _ledger = new TransactionLedger(_store, _clock);

    private Transaction Buy(string externalId = "T-1", decimal cash = -1203.50m, LocalDate? settled = null) => new(
        TransactionId.New(), Ibkr, TransactionKind.Buy, Traded, settled ?? Traded.PlusDays(1), Money.Usd(cash), TransactionSource.Flex,
        _clock.GetCurrentInstant(), Voo, 2m, new Price(601.75m, Currency.Usd), externalId);

    private Transaction FeeOf(Transaction trade, string? externalId = "T-1:fee", decimal amount = 0.35m) => new(
        TransactionId.New(), trade.HoldingPlace, TransactionKind.Fee, Traded, Traded.PlusDays(1), Money.Usd(-amount),
        externalId is null ? TransactionSource.Manual : TransactionSource.Flex, _clock.GetCurrentInstant(), Voo,
        externalId: externalId, relatedTo: trade.Id);

    private Transaction Deposit(decimal amount, string? externalId = null) => new(
        TransactionId.New(), Ibkr, TransactionKind.Deposit, Traded, Traded, Money.Usd(amount),
        externalId is null ? TransactionSource.Manual : TransactionSource.Flex, _clock.GetCurrentInstant(), externalId: externalId);

    private Transaction ConversionLeg(decimal amount, Currency currency, string externalId, Transaction? firstLeg = null) => new(
        TransactionId.New(), Ibkr, TransactionKind.FxConversion, Traded, Traded, new Money(amount, currency), TransactionSource.Flex,
        _clock.GetCurrentInstant(), externalId: externalId, relatedTo: firstLeg?.Id);

    /// <summary>The statement read once more: the same lines, built afresh with new ids.</summary>
    private Transaction[] Statement()
    {
        var buy = Buy();
        return [buy, FeeOf(buy), Deposit(1000m, "D-7")];
    }

    private Task<LedgerResult> RecordAsync(params Transaction[] entries) =>
        _ledger.RecordAsync(entries, CancellationToken.None);

    private Task<IReadOnlyList<Transaction>> ReverseAsync(Transaction entry, string reason = "a mistake") =>
        _ledger.ReverseAsync(entry.Id, reason, CancellationToken.None);

    private Transaction Stored(string externalId) => _store.Rows.Last(t => t.ExternalId == externalId);

    private decimal Usd() => _store.Rows.Where(t => t.Cash.Currency == Currency.Usd).Sum(t => t.Cash.Amount);

    [Fact]
    public async Task ImportingTheSameStatementAgainRecordsNothing()
    {
        var first = await RecordAsync(Statement());
        var ids = _store.Rows.Select(t => t.Id).ToList();

        var second = await RecordAsync(Statement());
        var third = await RecordAsync(Statement());

        Assert.Equal((3, 0), (first.Added, first.AlreadyRecorded));
        Assert.Equal((0, 3), (second.Added, second.AlreadyRecorded));
        Assert.Equal((0, 3, 0), (third.Added, third.AlreadyRecorded, third.NotRecorded.Count));
        Assert.Equal(ids, _store.Rows.Select(t => t.Id));
        Assert.Equal(1, _store.Batches);
    }

    [Fact]
    public async Task LineThatArrivesLaterIsLinkedToTheEntryAlreadyInTheLedger()
    {
        await RecordAsync(Buy());
        var storedBuy = _store.Rows.Single();
        var buyAgain = Buy();

        var result = await RecordAsync(buyAgain, FeeOf(buyAgain));

        Assert.Equal((1, 1), (result.Added, result.AlreadyRecorded));
        var fee = _store.Rows.Single(t => t.Kind == TransactionKind.Fee);
        Assert.Equal(storedBuy.Id, fee.RelatedTo);
        Assert.NotEqual(buyAgain.Id, fee.RelatedTo);
    }

    [Fact]
    public async Task SameLineTwiceInOneStatementIsRecordedOnce()
    {
        var result = await RecordAsync(Deposit(1000m, "D-7"), Deposit(1000m, "D-7"));

        Assert.Equal((1, 1), (result.Added, result.AlreadyRecorded));
        Assert.Single(_store.Rows);

        // Twice with different amounts: the first is recorded, the second is reported.
        var differing = await RecordAsync(Deposit(500m, "D-8"), Deposit(600m, "D-8"));
        Assert.Equal((1, 0), (differing.Added, differing.AlreadyRecorded));
        Assert.Contains("states this line twice", Assert.Single(differing.NotRecorded), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LineThatNowSaysSomethingElseIsReportedAndTheLedgerStaysAsItIs()
    {
        await RecordAsync(Statement());
        var before = _store.Rows.ToList();
        var corrected = Buy(cash: -1203.60m);

        var result = await RecordAsync(corrected, FeeOf(corrected));

        Assert.Equal((0, 1), (result.Added, result.AlreadyRecorded)); // the fee is unchanged and already there
        var report = Assert.Single(result.NotRecorded);
        Assert.Contains("Flex T-1", report, StringComparison.Ordinal);
        Assert.Contains("cash -1203.50 USD in the ledger and -1203.60 USD in the source", report, StringComparison.Ordinal);
        Assert.Contains($"reverse the entry {Stored("T-1").Id}", report, StringComparison.Ordinal);
        Assert.Equal(before, _store.Rows);
    }

    [Fact]
    public async Task ReportNamesEveryFactThatDiffersNotOnlyTheAmount()
    {
        await RecordAsync(Buy());

        var result = await RecordAsync(Buy(settled: Traded.PlusDays(2)));

        var report = Assert.Single(result.NotRecorded);
        Assert.Contains("settlement date 2026-10-13 in the ledger and 2026-10-14 in the source", report, StringComparison.Ordinal);
        Assert.DoesNotContain("cash", report, StringComparison.Ordinal); // the amount did not change
    }

    [Fact]
    public async Task LineThatHangsOnAChangedLineIsNotRecordedOnTheOldOne()
    {
        // The dividend was recorded before its tax line arrived; then the broker corrected the dividend.
        var dividend = new Transaction(
            TransactionId.New(), Ibkr, TransactionKind.Dividend, Traded, Traded, Money.Usd(3.42m), TransactionSource.Flex,
            _clock.GetCurrentInstant(), Voo, externalId: "DIV-1");
        await RecordAsync(dividend);
        var corrected = new Transaction(
            TransactionId.New(), Ibkr, TransactionKind.Dividend, Traded, Traded, Money.Usd(3.52m), TransactionSource.Flex,
            _clock.GetCurrentInstant(), Voo, externalId: "DIV-1");
        var tax = new Transaction(
            TransactionId.New(), Ibkr, TransactionKind.TaxWithheld, Traded, Traded, Money.Usd(-0.88m), TransactionSource.Flex,
            _clock.GetCurrentInstant(), Voo, externalId: "DIV-1:tax", relatedTo: corrected.Id);

        var result = await RecordAsync(corrected, tax);

        // Otherwise the ledger would show a dividend of 3.42 with 0.88 withheld from it.
        Assert.Equal((0, 0, 2), (result.Added, result.AlreadyRecorded, result.NotRecorded.Count));
        Assert.Contains("Flex DIV-1:tax: not recorded, because Flex DIV-1, which it belongs to, was not recorded", result.NotRecorded[1], StringComparison.Ordinal);
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task EntriesTypedInByHandAreAlwaysRecorded()
    {
        await RecordAsync(Deposit(500m));

        var result = await RecordAsync(Deposit(500m));

        Assert.Equal((1, 0), (result.Added, result.AlreadyRecorded));
        Assert.Equal(2, _store.Rows.Count);
    }

    [Fact]
    public async Task SameExternalIdAtAnotherPlaceOrFromAnotherSourceIsAnotherEntry()
    {
        await RecordAsync(Deposit(1000m, "D-7"));
        var elsewhere = new Transaction(
            TransactionId.New(), HoldingPlaceId.New(), TransactionKind.Deposit, Traded, Traded, Money.Usd(1000m),
            TransactionSource.Flex, _clock.GetCurrentInstant(), externalId: "D-7");
        var fromApi = new Transaction(
            TransactionId.New(), Ibkr, TransactionKind.Deposit, Traded, Traded, Money.Usd(1000m),
            TransactionSource.ApiExecution, _clock.GetCurrentInstant(), externalId: "D-7");

        var result = await RecordAsync(elsewhere, fromApi);

        Assert.Equal((2, 0), (result.Added, result.AlreadyRecorded));
    }

    [Fact]
    public async Task ReversingATradeCancelsItAndItsCommissionAndLeavesBothInTheLedger()
    {
        await RecordAsync(Statement());
        var buy = _store.Rows.Single(t => t.Kind == TransactionKind.Buy);
        var fee = _store.Rows.Single(t => t.Kind == TransactionKind.Fee);
        _clock.AdvanceHours(2);

        var reversals = await _ledger.ReverseAsync(buy.Id, "entered on the wrong account", CancellationToken.None);

        Assert.Equal(5, _store.Rows.Count);
        Assert.Equal([buy.Id, fee.Id], reversals.Select(r => r.Reverses));
        Assert.Equal(reversals[0].Id, reversals[1].RelatedTo);
        Assert.All(reversals, r =>
        {
            Assert.Equal("entered on the wrong account", r.Note);
            Assert.Equal(_clock.GetCurrentInstant(), r.RecordedAt);
        });
        // What is left is the deposit alone: the trade and its fee add up to nothing.
        Assert.Equal(1000m, _store.Rows.Sum(t => t.Cash.Amount));
        Assert.Equal(0m, _store.Rows.Sum(t => t.QuantityChange));
        Assert.Contains(buy, _store.Rows);
    }

    [Fact]
    public async Task EntryIsReversedOnceAndAReversalNever()
    {
        await RecordAsync(Deposit(500m));
        var deposit = _store.Rows.Single();
        var reversal = Assert.Single(await _ledger.ReverseAsync(deposit.Id, "wrong amount", CancellationToken.None));

        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _ledger.ReverseAsync(deposit.Id, "again", CancellationToken.None));
        Assert.Contains("already reversed", twice.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _ledger.ReverseAsync(reversal.Id, "undo", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _ledger.ReverseAsync(TransactionId.New(), "nothing there", CancellationToken.None));
        Assert.Equal(2, _store.Rows.Count);
    }

    [Fact]
    public async Task CorrectionIsAReversalFollowedByTheRightEntry()
    {
        await RecordAsync(Deposit(500m));
        var wrong = _store.Rows.Single();

        await _ledger.ReverseAsync(wrong.Id, "it was 550", CancellationToken.None);
        await RecordAsync(Deposit(550m));

        Assert.Equal(3, _store.Rows.Count);
        Assert.Equal(550m, _store.Rows.Sum(t => t.Cash.Amount));
        Assert.Contains(wrong, _store.Rows);
    }

    [Fact]
    public async Task LineTheSourceCorrectedIsRecordedAgainOnceItsOldRecordingIsReversed()
    {
        // The whole way the report prescribes: the broker corrects T-1, the import reports it, the owner
        // reverses the old recording, and the next import records the corrected line with its commission.
        await RecordAsync(Statement());
        Transaction[] CorrectedStatement()
        {
            var buy = Buy(cash: -1203.60m);
            return [buy, FeeOf(buy), Deposit(1000m, "D-7")];
        }

        var reported = await RecordAsync(CorrectedStatement());
        Assert.Equal((0, 2, 1), (reported.Added, reported.AlreadyRecorded, reported.NotRecorded.Count));
        await ReverseAsync(Stored("T-1"), "the broker corrected the amount");
        Assert.Equal(1000m, Usd()); // the trade and its commission are cancelled

        var recorded = await RecordAsync(CorrectedStatement());

        Assert.Equal((2, 1, 0), (recorded.Added, recorded.AlreadyRecorded, recorded.NotRecorded.Count));
        var buyNow = Stored("T-1");
        var feeNow = Stored("T-1:fee");
        Assert.Equal((-1203.60m, 1, buyNow.Id), (buyNow.Cash.Amount, buyNow.ExternalRevision, feeNow.RelatedTo!.Value));
        Assert.Equal(1, feeNow.ExternalRevision);
        Assert.Equal(1000m - 1203.60m - 0.35m, Usd());
        Assert.Equal(2m, _store.Rows.Sum(t => t.QuantityChange));

        // And from now on the corrected statement is simply in the books.
        var again = await RecordAsync(CorrectedStatement());
        Assert.Equal((0, 3, 0), (again.Added, again.AlreadyRecorded, again.NotRecorded.Count));
    }

    [Fact]
    public async Task ReversedLineTheSourceStillStatesIsReportedAndNotCountedAsRecorded()
    {
        await RecordAsync(Statement());
        await ReverseAsync(Stored("T-1"), "the broker cancelled the trade");
        var rows = _store.Rows.Count;

        var result = await RecordAsync(Statement());

        // "Already recorded" would tell the owner the trade is in the books while they net it to nothing.
        Assert.Equal((0, 1, 2), (result.Added, result.AlreadyRecorded, result.NotRecorded.Count));
        Assert.Contains("was reversed (the broker cancelled the trade) and the source still states the same", result.NotRecorded[0], StringComparison.Ordinal);
        Assert.Contains("Flex T-1:fee", result.NotRecorded[1], StringComparison.Ordinal);
        Assert.Equal(rows, _store.Rows.Count);
    }

    [Fact]
    public async Task LineThatHangsOnAReversedEntryIsNotRecorded()
    {
        await RecordAsync(Buy());
        var buy = Stored("T-1");
        await ReverseAsync(buy);
        var late = new Transaction(
            TransactionId.New(), Ibkr, TransactionKind.Fee, Traded, Traded, Money.Usd(-0.35m), TransactionSource.Flex,
            _clock.GetCurrentInstant(), Voo, externalId: "T-1:fee", relatedTo: buy.Id);

        var result = await RecordAsync(late);

        Assert.Equal(0, result.Added);
        Assert.Contains("was reversed", Assert.Single(result.NotRecorded), StringComparison.Ordinal);
        Assert.Equal(0m, Usd());
    }

    [Fact]
    public async Task ConversionIsCancelledWholeFromItsFirstLegAndNeverByHalf()
    {
        var sold = ConversionLeg(-4000m, Currency.Ils, "FX-1");
        var bought = ConversionLeg(1311.475409m, Currency.Usd, "FX-1:in", sold);
        await RecordAsync(sold, bought);

        // Reversing the dollar leg alone would leave 4000 shekels spent on nothing.
        var half = await Assert.ThrowsAsync<InvalidOperationException>(() => ReverseAsync(Stored("FX-1:in")));
        Assert.Contains($"Reverse the conversion {Stored("FX-1").Id}", half.Message, StringComparison.Ordinal);
        Assert.Equal(2, _store.Rows.Count);

        var reversals = await ReverseAsync(Stored("FX-1"));

        Assert.Equal(2, reversals.Count);
        Assert.Equal(0m, Usd());
        Assert.Equal(0m, _store.Rows.Where(t => t.Cash.Currency == Currency.Ils).Sum(t => t.Cash.Amount));
    }

    [Fact]
    public async Task CommissionCanBeCancelledOnItsOwn()
    {
        await RecordAsync(Statement());

        var reversal = Assert.Single(await ReverseAsync(Stored("T-1:fee"), "charged twice"));

        Assert.Equal(0.35m, reversal.Cash.Amount);
        Assert.Equal(1000m - 1203.50m, Usd());
    }

    [Fact]
    public async Task ReversalThatComesFromTheSourceIsCheckedAgainstWhatItCancels()
    {
        await RecordAsync(Deposit(1000m, "D-7"));
        var deposit = Stored("D-7");
        Transaction Cancelling(decimal cash, string externalId, TransactionId? target = null) => new(
            TransactionId.New(), Ibkr, TransactionKind.Deposit, Traded, Traded, Money.Usd(cash), TransactionSource.Flex,
            _clock.GetCurrentInstant(), externalId: externalId, reverses: target ?? deposit.Id);

        // "Reverses the deposit" with one dollar: the sum would say 999 and the books (reversed pairs cancel) 0.
        var partial = await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(Cancelling(-1m, "D-7:cancel")));
        Assert.Contains("not the exact opposite", partial.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(Cancelling(-1000m, "X", TransactionId.New())));
        Assert.Single(_store.Rows);

        // The broker's own cancellation, mapped by an import: recorded once, however often it is imported.
        var first = await RecordAsync(Cancelling(-1000m, "D-7:cancel"));
        var second = await RecordAsync(Cancelling(-1000m, "D-7:cancel"));
        Assert.Equal((1, 0, 0, 1), (first.Added, first.AlreadyRecorded, second.Added, second.AlreadyRecorded));
        Assert.Equal(0m, Usd());

        // Not a second time under another id, and a reversal is not reversed.
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(Cancelling(-1000m, "D-7:cancel-2")));
        Assert.Contains("already reversed", twice.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RecordAsync(Cancelling(-1000m, "D-7:undo", Stored("D-7:cancel").Id)));
        Assert.Equal(2, _store.Rows.Count);
    }

    [Fact]
    public async Task BatchWithALinkTheLedgerCannotKeepRecordsNothing()
    {
        var buy = Buy();
        var fee = FeeOf(buy);
        var feeOfTheFee = new Transaction(
            TransactionId.New(), Ibkr, TransactionKind.Fee, Traded, Traded, Money.Usd(-0.01m), TransactionSource.Flex,
            _clock.GetCurrentInstant(), externalId: "T-1:fee:fee", relatedTo: fee.Id);
        var elsewhere = new Transaction(
            TransactionId.New(), HoldingPlaceId.New(), TransactionKind.Fee, Traded, Traded, Money.Usd(-0.01m),
            TransactionSource.Flex, _clock.GetCurrentInstant(), externalId: "T-9:fee", relatedTo: buy.Id);

        // A reversal reaches one level down: something hanging lower would survive the reversal of the trade.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(buy, fee, feeOfTheFee));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(buy, elsewhere));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(fee)); // its trade is nowhere

        Assert.Empty(_store.Rows);
    }

    [Fact]
    public void ForAnyStatementImportingTwiceEqualsImportingOnceAndAReversalRestoresEveryBalance()
    {
        var line = Gen.Select(Gen.Int[0, 3], Gen.Long[1, 5_000_000], Gen.Long[1_000_000, 900_000_000], Gen.Long[1, 3_000_000]);
        line.List[1, 12].Sample(lines =>
        {
            var clock = new FakeClock(Instant.FromUtc(2026, 10, 13, 6, 0));
            var store = new MemoryStore();
            var ledger = new TransactionLedger(store, clock);
            Transaction[] Build()
            {
                var statement = new List<Transaction>();
                for (var i = 0; i < lines.Count; i++)
                {
                    var (kind, a, b, c) = lines[i];
                    var (shares, price, fee) = (a / 10_000m, b / 1_000_000m, c / 1_000_000m);
                    if (kind == 0)
                    {
                        statement.Add(new Transaction(
                            TransactionId.New(), Ibkr, TransactionKind.Deposit, Traded, Traded, Money.Usd(price), TransactionSource.Flex,
                            clock.GetCurrentInstant(), externalId: $"D-{i}"));
                        continue;
                    }

                    // A purchase or a sale with its commission (the ledger does not ask whether the shares are held).
                    var sign = kind == 3 ? -1m : 1m;
                    var trade = new Transaction(
                        TransactionId.New(), Ibkr, sign > 0m ? TransactionKind.Buy : TransactionKind.Sell, Traded, Traded.PlusDays(1),
                        Money.Usd(-sign * decimal.Round(shares * price, Transaction.Scale)), TransactionSource.Flex, clock.GetCurrentInstant(),
                        Voo, sign * shares, new Price(price, Currency.Usd), $"T-{i}");
                    statement.Add(trade);
                    statement.Add(new Transaction(
                        TransactionId.New(), Ibkr, TransactionKind.Fee, Traded, Traded.PlusDays(1), Money.Usd(-fee), TransactionSource.Flex,
                        clock.GetCurrentInstant(), Voo, externalId: $"T-{i}:fee", relatedTo: trade.Id));
                }

                return [.. statement];
            }

            (decimal Cash, decimal Shares) Balance() => (store.Rows.Sum(t => t.Cash.Amount), store.Rows.Sum(t => t.QuantityChange));

            var first = ledger.RecordAsync(Build(), CancellationToken.None).GetAwaiter().GetResult();
            var once = (Balance(), store.Rows.Count);
            var second = ledger.RecordAsync(Build(), CancellationToken.None).GetAwaiter().GetResult();
            var twiceEqualsOnce = once == (Balance(), store.Rows.Count) && second.Added == 0 && second.NotRecorded.Count == 0
                && second.AlreadyRecorded == first.Added;

            // Reversing every main entry (what hangs on it goes with it) leaves nothing at all.
            foreach (var main in store.Rows.Where(t => t.RelatedTo is null).ToList())
            {
                ledger.ReverseAsync(main.Id, "test", CancellationToken.None).GetAwaiter().GetResult();
            }

            return twiceEqualsOnce && Balance() == (0m, 0m);
        });
    }

    /// <summary>The store's contract, in memory.</summary>
    private sealed class MemoryStore : ITransactionStore
    {
        public List<Transaction> Rows { get; } = [];

        public int Batches { get; private set; }

        public Task<Transaction?> FindAsync(TransactionId id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(t => t.Id == id));

        public Task<Transaction?> FindByExternalIdAsync(
            HoldingPlaceId holdingPlace, TransactionSource source, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows
                .Where(t => t.HoldingPlace == holdingPlace && t.Source == source && t.ExternalId == externalId)
                .OrderByDescending(t => t.ExternalRevision)
                .FirstOrDefault());

        public Task<Transaction?> FindReversalOfAsync(TransactionId id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(t => t.Reverses == id));

        public Task<IReadOnlyList<Transaction>> ListRelatedToAsync(TransactionId id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Transaction>>(Rows.Where(t => t.RelatedTo == id).ToList());

        public Task<IReadOnlyList<Transaction>> ListAsync(HoldingPlaceId holdingPlace, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Transaction>>(Rows.Where(t => t.HoldingPlace == holdingPlace).ToList());

        public Task AddAsync(IReadOnlyList<Transaction> entries, CancellationToken cancellationToken)
        {
            // As the database does: one recording of a line of a source per revision.
            foreach (var entry in entries.Where(e => e.ExternalId is not null))
            {
                if (Rows.Concat(entries).Count(t =>
                        t.HoldingPlace == entry.HoldingPlace && t.Source == entry.Source && t.ExternalId == entry.ExternalId
                        && t.ExternalRevision == entry.ExternalRevision) > 1)
                {
                    throw new InvalidOperationException($"Duplicate line {entry.Source} {entry.ExternalId}.");
                }
            }

            Rows.AddRange(entries);
            Batches++;
            return Task.CompletedTask;
        }
    }
}
