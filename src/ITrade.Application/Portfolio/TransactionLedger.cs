using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Portfolio;

/// <summary>What recording a batch did. A conflict is an entry that was left out, with the reason.</summary>
public sealed record LedgerResult(int Added, int AlreadyRecorded, IReadOnlyList<string> Conflicts);

/// <summary>The accounting ledger (table `transactions`). Entries are only ever added.</summary>
public interface ITransactionStore
{
    Task<Transaction?> FindAsync(TransactionId id, CancellationToken cancellationToken);

    Task<Transaction?> FindByExternalIdAsync(
        HoldingPlaceId holdingPlace, TransactionSource source, string externalId, CancellationToken cancellationToken);

    /// <summary>The entry that cancels the given one, if it was reversed.</summary>
    Task<Transaction?> FindReversalOfAsync(TransactionId id, CancellationToken cancellationToken);

    /// <summary>The entries that hang on the given one: the fee of a trade, the tax of a dividend, the other leg of a conversion.</summary>
    Task<IReadOnlyList<Transaction>> ListRelatedToAsync(TransactionId id, CancellationToken cancellationToken);

    /// <summary>Everything at a holding place, in the order it happened: by trade date, then as it was recorded.</summary>
    Task<IReadOnlyList<Transaction>> ListAsync(HoldingPlaceId holdingPlace, CancellationToken cancellationToken);

    /// <summary>
    /// Adds all the entries or none of them, in the order given — that order is the ledger's order within a
    /// trade date. An entry comes after the one it is related to or reverses.
    /// </summary>
    Task AddAsync(IReadOnlyCollection<Transaction> entries, CancellationToken cancellationToken);
}

/// <summary>
/// Writes to the ledger. An import may be repeated as often as wanted: an entry is recognised by its source and
/// external id and is recorded once. Nothing in the ledger is ever changed — a statement line that now says
/// something else than what was recorded is reported, and a mistake is cancelled by a reversal.
/// </summary>
public sealed class TransactionLedger(ITransactionStore store, IClock clock)
{
    /// <summary>
    /// Records the entries that are not in the ledger yet, as one change. Entries typed in by hand carry no
    /// external id and are always recorded. An entry that hangs on one already in the ledger (a fee whose trade
    /// was imported earlier) is linked to the stored one. The entries are given in the order of the source, an
    /// entry after the one it hangs on; within a trade date the ledger keeps that order.
    /// </summary>
    public async Task<LedgerResult> RecordAsync(IEnumerable<Transaction> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var toAdd = new List<Transaction>();
        var batch = new Dictionary<(HoldingPlaceId, TransactionSource, string), Transaction>();
        var stored = new Dictionary<TransactionId, TransactionId>(); // id built by the import → id in the ledger
        var already = 0;
        var conflicts = new List<string>();

        TransactionId? Moved(TransactionId? link) =>
            link is { } id && stored.TryGetValue(id, out var known) ? known : link;

        foreach (var given in entries)
        {
            var entry = given.Relinked(Moved(given.RelatedTo), Moved(given.Reverses));
            if (entry.ExternalId is not { } externalId)
            {
                toAdd.Add(entry);
                continue;
            }

            var key = (entry.HoldingPlace, entry.Source, externalId);
            var known = batch.GetValueOrDefault(key)
                ?? await store.FindByExternalIdAsync(entry.HoldingPlace, entry.Source, externalId, cancellationToken);
            if (known is null)
            {
                batch[key] = entry;
                toAdd.Add(entry);
                continue;
            }

            // Later entries of this batch that hang on this one are linked to what the ledger holds.
            stored[entry.Id] = known.Id;
            if (known.SameFactsAs(entry))
            {
                already++;
            }
            else
            {
                conflicts.Add(
                    $"{entry.Source} {externalId}: the ledger holds {Describe(known)}, the source now says " +
                    $"{Describe(entry)}. Nothing was changed; if the source was corrected, reverse the entry and record the new one.");
            }
        }

        if (toAdd.Count > 0)
        {
            await store.AddAsync(toAdd, cancellationToken);
        }

        return new LedgerResult(toAdd.Count, already, conflicts);
    }

    /// <summary>
    /// Cancels an entry and everything that hangs on it (the commission of a trade, the tax withheld from a
    /// dividend), as one change. Returns the reversals, the one of the entry itself first.
    /// </summary>
    public async Task<IReadOnlyList<Transaction>> ReverseAsync(
        TransactionId id, string reason, CancellationToken cancellationToken)
    {
        var entry = await store.FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"There is no transaction {id}.");
        if (entry.IsReversal)
        {
            throw new InvalidOperationException("A reversal is not reversed: record the right entry instead.");
        }

        if (await store.FindReversalOfAsync(id, cancellationToken) is not null)
        {
            throw new InvalidOperationException($"Transaction {id} is already reversed.");
        }

        var now = clock.GetCurrentInstant();
        var reversals = new List<Transaction> { entry.Reversal(TransactionId.New(), now, reason) };
        foreach (var related in await store.ListRelatedToAsync(id, cancellationToken))
        {
            if (!related.IsReversal && await store.FindReversalOfAsync(related.Id, cancellationToken) is null)
            {
                reversals.Add(related.Reversal(TransactionId.New(), now, reason).Relinked(reversals[0].Id, related.Id));
            }
        }

        await store.AddAsync(reversals, cancellationToken);
        return reversals;
    }

    private static string Describe(Transaction entry) =>
        $"{entry.Kind} on {entry.TradeDate:R}, cash {entry.Cash}" +
        (entry.QuantityChange == 0m ? "" : $", shares {entry.QuantityChange:0.######}");
}
