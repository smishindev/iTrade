using ITrade.Application.Portfolio;
using ITrade.Domain;
using NodaTime;

namespace ITrade.UnitTests.Application;

/// <summary>P3.1.01: importing instruments never duplicates, and never applies what contradicts the records.</summary>
public sealed class InstrumentCatalogueTests
{
    private readonly MemoryStore _store = new();
    private readonly InstrumentCatalogue _catalogue;

    public InstrumentCatalogueTests() => _catalogue = new InstrumentCatalogue(_store);

    private static InstrumentListing Voo(string name = "Vanguard S&P 500 ETF", string? isin = "US9229083632", int? conId = 136155102) =>
        new(new Ticker("VOO"), "ARCA", Currency.Usd, name, isin is null ? null : new Isin(isin), conId);

    private static InstrumentListing Spy() =>
        new(new Ticker("SPY"), "ARCA", Currency.Usd, "SPDR S&P 500 ETF Trust", new Isin("US78462F1030"), 756733);

    private Task<InstrumentImportResult> ImportAsync(params InstrumentListing[] listings) =>
        _catalogue.ImportAsync(listings, CancellationToken.None);

    [Fact]
    public async Task ImportingTheSameListAgainChangesNothing()
    {
        var first = await ImportAsync(Voo(), Spy());
        var ids = _store.Rows.Select(i => i.Id).ToList();

        var second = await ImportAsync(Voo(), Spy());

        Assert.Equal(new InstrumentImportResult(2, 0, 0, []), first with { Conflicts = [] });
        Assert.Equal((0, 0, 2), (second.Added, second.Updated, second.Unchanged));
        Assert.Empty(second.Conflicts);
        Assert.Equal(ids, _store.Rows.Select(i => i.Id));
        Assert.Equal(0, _store.Writes - 2);
    }

    [Fact]
    public async Task SameInstrumentListedTwiceInOneFileIsAddedOnce()
    {
        var result = await ImportAsync(Voo(), Voo());

        Assert.Equal((1, 0, 1), (result.Added, result.Updated, result.Unchanged));
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task NewNameIsTakenOverAndTheIdentityStays()
    {
        await ImportAsync(Voo());
        var id = _store.Rows.Single().Id;

        var result = await ImportAsync(Voo(name: "Vanguard S&P 500 UCITS-free ETF"));

        Assert.Equal((0, 1, 0), (result.Added, result.Updated, result.Unchanged));
        var stored = _store.Rows.Single();
        Assert.Equal((id, "Vanguard S&P 500 UCITS-free ETF"), (stored.Id, stored.Name));
    }

    [Fact]
    public async Task WhatTheSourceDoesNotSayIsKeptNotErased()
    {
        await ImportAsync(Voo());

        var result = await ImportAsync(Voo(isin: null, conId: null));

        Assert.Equal((0, 0, 1), (result.Added, result.Updated, result.Unchanged));
        var stored = _store.Rows.Single();
        Assert.Equal(("US9229083632", 136155102), (stored.Isin?.Value, stored.IbkrConId));
    }

    [Fact]
    public async Task InstrumentEnteredWithoutAContractGetsItFromALaterImport()
    {
        await ImportAsync(Voo(isin: null, conId: null));

        var result = await ImportAsync(Voo());

        Assert.Equal((0, 1, 0), (result.Added, result.Updated, result.Unchanged));
        var stored = _store.Rows.Single();
        Assert.Equal(("US9229083632", 136155102), (stored.Isin?.Value, stored.IbkrConId));
    }

    [Fact]
    public async Task ListingThatContradictsTheRecordsIsReportedAndLeftAlone()
    {
        await ImportAsync(Voo(), Spy());
        var before = _store.Rows.ToList();

        var result = await ImportAsync(
            Voo() with { Ticker = new Ticker("VOOX") },                      // the same contract under another ticker
            Voo(conId: 999),                                                 // the same ticker with another contract
            Spy() with { Currency = Currency.Ils },                          // another currency
            Spy() with { IbkrConId = null, Isin = new Isin("US0378331005") }); // another ISIN

        Assert.Equal((0, 0, 0), (result.Added, result.Updated, result.Unchanged));
        Assert.Equal(4, result.Conflicts.Count);
        Assert.Contains("record the rename with its date", result.Conflicts[0], StringComparison.Ordinal);
        Assert.Contains("IBKR contract 136155102, listed with 999", result.Conflicts[1], StringComparison.Ordinal);
        Assert.Contains("stored in USD, listed in ILS", result.Conflicts[2], StringComparison.Ordinal);
        Assert.Contains("ISIN US78462F1030, listed with US0378331005", result.Conflicts[3], StringComparison.Ordinal);
        Assert.Equal(before, _store.Rows);
    }

    [Fact]
    public async Task RenameIsRecordedWithItsDateAndOldStatementsStillFindTheInstrument()
    {
        await ImportAsync(new InstrumentListing(new Ticker("FB"), "NASDAQ", Currency.Usd, "Meta Platforms", null, 107113386));
        var id = _store.Rows.Single().Id;
        var day = new LocalDate(2022, 6, 9);

        await _catalogue.RenameAsync(id, new Ticker("META"), day, CancellationToken.None);

        Assert.Equal(new Ticker("META"), _store.Rows.Single().Ticker);
        Assert.Equal(id, (await _store.FindByTickerOnAsync(new Ticker("FB"), "NASDAQ", day.PlusDays(-1), CancellationToken.None))?.Id);
        Assert.Null(await _store.FindByTickerOnAsync(new Ticker("FB"), "NASDAQ", day, CancellationToken.None));
        Assert.Equal(id, (await _store.FindByTickerOnAsync(new Ticker("META"), "NASDAQ", day, CancellationToken.None))?.Id);
        // The import now knows the contract under its new ticker: nothing to do, nothing to report.
        var again = await ImportAsync(new InstrumentListing(new Ticker("META"), "NASDAQ", Currency.Usd, "Meta Platforms", null, 107113386));
        Assert.Equal((0, 0, 1, 0), (again.Added, again.Updated, again.Unchanged, again.Conflicts.Count));
    }

    [Fact]
    public async Task RenameIsRefusedWhenItBreaksTheRules()
    {
        await ImportAsync(Voo(), Spy());
        var voo = _store.Rows.Single(i => i.Ticker.Value == "VOO").Id;
        var day = new LocalDate(2026, 1, 5);

        var taken = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _catalogue.RenameAsync(voo, new Ticker("SPY"), day, CancellationToken.None));
        Assert.Contains("already the ticker of", taken.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _catalogue.RenameAsync(voo, new Ticker("VOO"), day, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _catalogue.RenameAsync(InstrumentId.New(), new Ticker("NEW"), day, CancellationToken.None));

        await _catalogue.RenameAsync(voo, new Ticker("VOOA"), day, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _catalogue.RenameAsync(voo, new Ticker("VOOB"), day, CancellationToken.None)); // not after the previous one
    }

    /// <summary>The store's contract, in memory.</summary>
    private sealed class MemoryStore : IInstrumentStore
    {
        private readonly Dictionary<InstrumentId, List<FormerTicker>> _former = [];

        public List<Instrument> Rows { get; } = [];

        public int Writes { get; private set; }

        public Task<Instrument?> FindAsync(InstrumentId id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(i => i.Id == id));

        public Task<Instrument?> FindByConIdAsync(int ibkrConId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(i => i.IbkrConId == ibkrConId));

        public Task<Instrument?> FindByTickerAsync(Ticker ticker, string exchange, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(i => i.Ticker == ticker && i.Exchange == exchange));

        public Task<Instrument?> FindByTickerOnAsync(Ticker ticker, string exchange, LocalDate day, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(i => i.Exchange == exchange && History(i).On(day) == ticker));

        public Task<TickerHistory?> TickersOfAsync(InstrumentId id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(i => i.Id == id) is { } found ? History(found) : null);

        public Task<IReadOnlyList<Instrument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Instrument>>(Rows);

        public Task AddAsync(Instrument instrument, CancellationToken cancellationToken)
        {
            Rows.Add(instrument);
            Writes++;
            return Task.CompletedTask;
        }

        public Task UpdateDetailsAsync(Instrument instrument, CancellationToken cancellationToken)
        {
            Rows[Rows.FindIndex(i => i.Id == instrument.Id)] = instrument;
            Writes++;
            return Task.CompletedTask;
        }

        public Task RenameAsync(InstrumentId id, Ticker newTicker, LocalDate effective, CancellationToken cancellationToken)
        {
            var index = Rows.FindIndex(i => i.Id == id);
            var old = Rows[index];
            _ = History(old).Renamed(newTicker, effective);
            if (!_former.TryGetValue(id, out var former))
            {
                _former[id] = former = [];
            }

            former.Add(new FormerTicker(old.Ticker, effective));
            Rows[index] = new Instrument(old.Id, newTicker, old.Exchange, old.Currency, old.Name, old.Isin, old.IbkrConId);
            Writes++;
            return Task.CompletedTask;
        }

        private TickerHistory History(Instrument instrument) =>
            new(instrument.Ticker, _former.GetValueOrDefault(instrument.Id));
    }
}
