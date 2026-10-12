using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Portfolio;

/// <summary>An instrument as a source lists it (the catalogue file, later IBKR contract details): no id yet.</summary>
public sealed record InstrumentListing(
    Ticker Ticker, string Exchange, Currency Currency, string Name, Isin? Isin = null, int? IbkrConId = null);

/// <summary>What an import did. A conflict is a listing that was left alone, with the reason.</summary>
public sealed record InstrumentImportResult(int Added, int Updated, int Unchanged, IReadOnlyList<string> Conflicts);

/// <summary>The instruments the platform knows (table `instruments` and the former tickers).</summary>
public interface IInstrumentStore
{
    Task<Instrument?> FindAsync(InstrumentId id, CancellationToken cancellationToken);

    Task<Instrument?> FindByConIdAsync(int ibkrConId, CancellationToken cancellationToken);

    /// <summary>The instrument that trades under this ticker on the exchange today.</summary>
    Task<Instrument?> FindByTickerAsync(Ticker ticker, string exchange, CancellationToken cancellationToken);

    /// <summary>The instrument that traded under this ticker on the exchange on that day — renames taken into account.</summary>
    Task<Instrument?> FindByTickerOnAsync(Ticker ticker, string exchange, LocalDate day, CancellationToken cancellationToken);

    Task<TickerHistory?> TickersOfAsync(InstrumentId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Instrument>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(Instrument instrument, CancellationToken cancellationToken);

    /// <summary>Updates name, ISIN and contract id. Ticker and exchange change only through <see cref="RenameAsync"/>.</summary>
    Task UpdateDetailsAsync(Instrument instrument, CancellationToken cancellationToken);

    /// <summary>Records the old ticker as valid until <paramref name="effective"/> and sets the new one, as one change.</summary>
    Task RenameAsync(InstrumentId id, Ticker newTicker, LocalDate effective, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the instrument list: imports listings without ever duplicating an instrument, and records renames
/// with their dates so that a statement of an earlier year still finds its instrument.
/// </summary>
public sealed class InstrumentCatalogue(IInstrumentStore store)
{
    /// <summary>
    /// Adds the listings that are new, updates name, ISIN and contract id of the known ones, and leaves the
    /// rest alone. An instrument is recognised by its IBKR contract id, else by ticker and exchange. Running
    /// the same import again changes nothing. A listing that contradicts what is stored — another ticker for
    /// the same contract, another contract or currency for the same ticker — is reported, not applied:
    /// a rename needs its date (<see cref="RenameAsync"/>), and the other two need a look by the owner.
    /// </summary>
    public async Task<InstrumentImportResult> ImportAsync(
        IEnumerable<InstrumentListing> listings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listings);
        int added = 0, updated = 0, unchanged = 0;
        var conflicts = new List<string>();
        foreach (var listing in listings)
        {
            var incoming = new Instrument(
                InstrumentId.New(), listing.Ticker, listing.Exchange, listing.Currency, listing.Name,
                listing.Isin, listing.IbkrConId);
            var byContract = incoming.IbkrConId is { } conId
                ? await store.FindByConIdAsync(conId, cancellationToken)
                : null;
            var known = byContract
                ?? await store.FindByTickerAsync(incoming.Ticker, incoming.Exchange, cancellationToken);
            if (known is null)
            {
                await store.AddAsync(incoming, cancellationToken);
                added++;
                continue;
            }

            if (Contradiction(known, incoming) is { } conflict)
            {
                conflicts.Add(conflict);
                continue;
            }

            // What the source does not say (no ISIN, no contract id) is kept, not erased.
            var merged = new Instrument(
                known.Id, known.Ticker, known.Exchange, known.Currency, incoming.Name,
                incoming.Isin ?? known.Isin, incoming.IbkrConId ?? known.IbkrConId);
            if (merged == known)
            {
                unchanged++;
            }
            else
            {
                await store.UpdateDetailsAsync(merged, cancellationToken);
                updated++;
            }
        }

        return new InstrumentImportResult(added, updated, unchanged, conflicts);
    }

    /// <summary>Records that the instrument trades under another ticker from <paramref name="effective"/> on.</summary>
    public async Task RenameAsync(InstrumentId id, Ticker to, LocalDate effective, CancellationToken cancellationToken)
    {
        var instrument = await store.FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"There is no instrument {id}.");
        var history = await store.TickersOfAsync(id, cancellationToken) ?? new TickerHistory(instrument.Ticker);
        _ = history.Renamed(to, effective); // the rules of a rename: a new ticker, later than the previous rename
        if (await store.FindByTickerAsync(to, instrument.Exchange, cancellationToken) is { } taken)
        {
            throw new InvalidOperationException(
                $"{to} on {instrument.Exchange} is already the ticker of '{taken.Name}'.");
        }

        await store.RenameAsync(id, to, effective, cancellationToken);
    }

    private static string? Contradiction(Instrument known, Instrument incoming)
    {
        var label = $"{incoming.Ticker} on {incoming.Exchange}";
        if (known.Ticker != incoming.Ticker || known.Exchange != incoming.Exchange)
        {
            return $"{label}: IBKR contract {incoming.IbkrConId} is stored as {known.Ticker} on {known.Exchange}. " +
                "If the fund was renamed, record the rename with its date.";
        }

        if (known.IbkrConId is { } stored && incoming.IbkrConId is { } listed && stored != listed)
        {
            return $"{label}: stored with IBKR contract {stored}, listed with {listed}.";
        }

        if (known.Currency != incoming.Currency)
        {
            return $"{label}: stored in {known.Currency}, listed in {incoming.Currency}.";
        }

        if (known.Isin is { } storedIsin && incoming.Isin is { } listedIsin && storedIsin != listedIsin)
        {
            return $"{label}: stored with ISIN {storedIsin}, listed with {listedIsin}.";
        }

        return null;
    }
}

/// <summary>The places where money is held (table `holding_places`).</summary>
public interface IHoldingPlaceStore
{
    Task<IReadOnlyList<HoldingPlace>> ListAsync(CancellationToken cancellationToken);

    Task<HoldingPlace?> FindByNameAsync(string name, CancellationToken cancellationToken);

    Task AddAsync(HoldingPlace place, CancellationToken cancellationToken);
}
