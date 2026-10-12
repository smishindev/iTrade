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

    private Transaction Buy(string externalId = "T-1", decimal cash = -1203.50m) => new(
        TransactionId.New(), Ibkr, TransactionKind.Buy, Traded, Traded.PlusDays(1), Money.Usd(cash), TransactionSource.Flex,
        _clock.GetCurrentInstant(), Voo, 2m, new Price(601.75m, Currency.Usd), externalId);

    private Transaction FeeOf(Transaction trade, string? externalId = "T-1:fee") => new(
        TransactionId.New(), Ibkr, TransactionKind.Fee, Traded, Traded.PlusDays(1), Money.Usd(-0.35m), trade.Source,
        _clock.GetCurrentInstant(), Voo, externalId: externalId, relatedTo: trade.Id);

    private Transaction Deposit(decimal amount, string? externalId = null) => new(
        TransactionId.New(), Ibkr, TransactionKind.Deposit, Traded, Traded, Money.Usd(amount),
        externalId is null ? TransactionSource.Manual : TransactionSource.Flex, _clock.GetCurrentInstant(), externalId: externalId);

    /// <summary>The statement read once more: the same lines, built afresh with new ids.</summary>
    private Transaction[] Statement()
    {
        var buy = Buy();
        return [buy, FeeOf(buy), Deposit(1000m, "D-7")];
    }

    private Task<LedgerResult> RecordAsync(params Transaction[] entries) =>
        _ledger.RecordAsync(entries, CancellationToken.None);

    [Fact]
    public async Task ImportingTheSameStatementAgainRecordsNothing()
    {
        var first = await RecordAsync(Statement());
        var ids = _store.Rows.Select(t => t.Id).ToList();

        var second = await RecordAsync(Statement());
        var third = await RecordAsync(Statement());

        Assert.Equal((3, 0), (first.Added, first.AlreadyRecorded));
        Assert.Equal((0, 3), (second.Added, second.AlreadyRecorded));
        Assert.Equal((0, 3, 0), (third.Added, third.AlreadyRecorded, third.Conflicts.Count));
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
    }

    [Fact]
    public async Task LineThatNowSaysSomethingElseIsReportedAndTheLedgerStaysAsItIs()
    {
        await RecordAsync(Statement());
        var before = _store.Rows.ToList();
        var corrected = Buy(cash: -1203.60m);

        var result = await RecordAsync(corrected, FeeOf(corrected));

        Assert.Equal((0, 1), (result.Added, result.AlreadyRecorded)); // the fee is unchanged and already there
        var conflict = Assert.Single(result.Conflicts);
        Assert.Contains("Flex T-1", conflict, StringComparison.Ordinal);
        Assert.Contains("-1203.50", conflict, StringComparison.Ordinal);
        Assert.Contains("-1203.60", conflict, StringComparison.Ordinal);
        Assert.Contains("reverse the entry", conflict, StringComparison.Ordinal);
        Assert.Equal(before, _store.Rows);
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

    /// <summary>The store's contract, in memory.</summary>
    private sealed class MemoryStore : ITransactionStore
    {
        public List<Transaction> Rows { get; } = [];

        public int Batches { get; private set; }

        public Task<Transaction?> FindAsync(TransactionId id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(t => t.Id == id));

        public Task<Transaction?> FindByExternalIdAsync(
            HoldingPlaceId holdingPlace, TransactionSource source, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(
                t => t.HoldingPlace == holdingPlace && t.Source == source && t.ExternalId == externalId));

        public Task<Transaction?> FindReversalOfAsync(TransactionId id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(t => t.Reverses == id));

        public Task<IReadOnlyList<Transaction>> ListRelatedToAsync(TransactionId id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Transaction>>(Rows.Where(t => t.RelatedTo == id).ToList());

        public Task<IReadOnlyList<Transaction>> ListAsync(HoldingPlaceId holdingPlace, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Transaction>>(Rows.Where(t => t.HoldingPlace == holdingPlace).ToList());

        public Task AddAsync(IReadOnlyCollection<Transaction> entries, CancellationToken cancellationToken)
        {
            Rows.AddRange(entries);
            Batches++;
            return Task.CompletedTask;
        }
    }
}
