using System.Globalization;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Portfolio;

/// <summary>
/// What recording a batch did. <paramref name="NotRecorded"/> lists every entry that was left out although
/// the ledger does not hold it as given, each with the reason — the owner has to look at these.
/// </summary>
public sealed record LedgerResult(int Added, int AlreadyRecorded, IReadOnlyList<string> NotRecorded);

/// <summary>The accounting ledger (table `transactions`). Entries are only ever added.</summary>
public interface ITransactionStore
{
    Task<Transaction?> FindAsync(TransactionId id, CancellationToken cancellationToken);

    /// <summary>The latest recording of the source's line (see <see cref="Transaction.ExternalRevision"/>).</summary>
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
    /// trade date. An entry comes after the one it is related to or reverses. Only <see cref="TransactionLedger"/>
    /// calls this: it is what checks an entry against the ledger first.
    /// </summary>
    Task AddAsync(IReadOnlyList<Transaction> entries, CancellationToken cancellationToken);
}

/// <summary>
/// Writes to the ledger. An import may be repeated as often as wanted: an entry is recognised by its source and
/// external id and is recorded once. Nothing in the ledger is ever changed — a statement line that now says
/// something else than what was recorded is reported, and a mistake is cancelled by a reversal.
/// </summary>
public sealed class TransactionLedger(ITransactionStore store, IClock clock)
{
    /// <summary>
    /// Records the entries that are not in the ledger yet, as one change. The entries are given in the order of
    /// the source, an entry after the one it hangs on; within a trade date the ledger keeps that order.
    ///
    /// A line of a source (an entry with an external id) is recorded once, however often it is imported. If the
    /// ledger holds it with other facts, it is reported and not recorded. If its recording was reversed and
    /// the source now states other facts — the source corrected the line — it is recorded as the next revision,
    /// and what hangs on it comes back with it; if the source still states the reversed facts, it is reported. Entries typed in by hand have no external
    /// id and are always recorded.
    ///
    /// An entry that hangs on one already in the ledger (a fee whose trade was imported earlier) is linked to
    /// the stored one. An entry that hangs on one that was not recorded, or on one that was reversed, is not
    /// recorded either. A batch that breaks the ledger's own rules (a link to nowhere, a reversal that is not
    /// the opposite of its target) is a fault of the caller: it throws and records nothing.
    /// </summary>
    public async Task<LedgerResult> RecordAsync(IEnumerable<Transaction> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var toAdd = new List<Transaction>();
        var adding = new Dictionary<TransactionId, Transaction>();
        var lines = new Dictionary<(HoldingPlaceId, TransactionSource, string), Transaction>();
        var stored = new Dictionary<TransactionId, TransactionId>(); // id built by the import → id in the ledger
        var leftOut = new Dictionary<TransactionId, string>(); // id built by the import → what the entry was
        var reversing = new HashSet<TransactionId>();
        var already = 0;
        var notRecorded = new List<string>();

        TransactionId? Moved(TransactionId? link) =>
            link is { } id && stored.TryGetValue(id, out var known) ? known : link;

        void LeaveOut(Transaction entry, string why)
        {
            leftOut[entry.Id] = Label(entry);
            notRecorded.Add($"{Label(entry)}: {why}");
        }

        foreach (var given in entries)
        {
            var entry = given.Relinked(Moved(given.RelatedTo), Moved(given.Reverses));
            (HoldingPlaceId, TransactionSource, string)? line = null;
            if (entry.ExternalId is { } externalId)
            {
                var key = (entry.HoldingPlace, entry.Source, externalId);
                if (lines.TryGetValue(key, out var twin))
                {
                    // The same line twice in one batch.
                    if (twin.SameFactsAs(entry))
                    {
                        stored[entry.Id] = twin.Id;
                        already++;
                    }
                    else
                    {
                        LeaveOut(entry, $"the source states this line twice, differently ({Differences(twin, entry)}). The second one was not recorded.");
                    }

                    continue;
                }

                var known = await store.FindByExternalIdAsync(entry.HoldingPlace, entry.Source, externalId, cancellationToken);
                var reversal = known is null ? null : await store.FindReversalOfAsync(known.Id, cancellationToken);
                if (known is not null && reversal is null)
                {
                    if (known.SameFactsAs(entry))
                    {
                        // Later entries of this batch that hang on this one are linked to what the ledger holds.
                        stored[entry.Id] = known.Id;
                        already++;
                    }
                    else
                    {
                        LeaveOut(
                            entry,
                            $"the ledger holds this line differently, as entry {known.Id} ({Differences(known, entry)}). " +
                            $"Nothing was changed; if the source was corrected, reverse the entry {known.Id} and import again.");
                    }

                    continue;
                }

                // A reversed line that the source still states the same way stays out — unless it hangs on an
                // entry this batch records: it was cancelled together with that entry and comes back with it.
                var comesBackWithItsParent = given.RelatedTo is { } recordedNow && adding.ContainsKey(recordedNow);
                if (known is not null && known.SameFactsAs(entry) && !comesBackWithItsParent)
                {
                    LeaveOut(
                        entry,
                        $"its recording {known.Id} was reversed ({reversal!.Note}) and the source still states the same. " +
                        "It was not recorded again.");
                    continue;
                }

                // New, or corrected by the source after its earlier recording was reversed.
                entry = entry.Revised(known is null ? 0 : known.ExternalRevision + 1);
                line = key;
            }

            // From here on the entry is new to the ledger. What it hangs on has to be there, and sound.
            if (new[] { given.RelatedTo, given.Reverses }.FirstOrDefault(l => l is { } id && leftOut.ContainsKey(id)) is { } missing)
            {
                LeaveOut(entry, $"not recorded, because {leftOut[missing]}, which it belongs to, was not recorded.");
                continue;
            }

            if (entry.RelatedTo is { } parentId)
            {
                var parent = adding.GetValueOrDefault(parentId)
                    ?? await store.FindAsync(parentId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"{Label(entry)} hangs on {parentId}, which is neither in the ledger nor earlier in this batch.");
                if (parent.HoldingPlace != entry.HoldingPlace || parent.RelatedTo is not null)
                {
                    throw new InvalidOperationException(
                        $"{Label(entry)} cannot hang on {Label(parent)}: an entry hangs on a main entry of its own holding place.");
                }

                if (!adding.ContainsKey(parentId) && await store.FindReversalOfAsync(parentId, cancellationToken) is not null)
                {
                    LeaveOut(entry, $"not recorded, because {Label(parent)}, which it belongs to, was reversed (entry {parentId}).");
                    continue;
                }
            }

            if (entry.Reverses is { } targetId)
            {
                var target = adding.GetValueOrDefault(targetId)
                    ?? await store.FindAsync(targetId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"{Label(entry)} reverses {targetId}, which is neither in the ledger nor earlier in this batch.");
                if (target.IsReversal || !entry.IsOppositeOf(target))
                {
                    throw new InvalidOperationException(
                        $"{Label(entry)} is not the exact opposite of {Label(target)} ({targetId}): a reversal cancels one entry whole.");
                }

                if (!reversing.Add(targetId) || await store.FindReversalOfAsync(targetId, cancellationToken) is not null)
                {
                    throw new InvalidOperationException($"{Label(target)} ({targetId}) is already reversed.");
                }
            }

            toAdd.Add(entry);
            adding[entry.Id] = entry;
            if (line is { } recorded)
            {
                lines[recorded] = entry;
            }
        }

        if (toAdd.Count > 0)
        {
            await store.AddAsync(toAdd, cancellationToken);
        }

        return new LedgerResult(toAdd.Count, already, notRecorded);
    }

    /// <summary>
    /// Cancels an entry and everything that hangs on it (the commission of a trade, the tax withheld from a
    /// dividend, the other leg of a conversion), as one change. Returns the reversals, the one of the entry
    /// itself first. A commission or a withheld tax can be cancelled on its own; one leg of a currency
    /// conversion cannot — the conversion is cancelled whole, from its first leg.
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

        if (entry is { Kind: TransactionKind.FxConversion, RelatedTo: { } firstLeg })
        {
            throw new InvalidOperationException(
                $"Transaction {id} is one leg of a currency conversion. Reverse the conversion {firstLeg}: both legs are cancelled together.");
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

    /// <summary>What the owner knows the entry by: the source's id of the line, or what it is and when.</summary>
    private static string Label(Transaction entry) =>
        entry.ExternalId is { } externalId
            ? $"{entry.Source} {externalId}"
            : $"{entry.Kind} of {entry.TradeDate:R} ({entry.Cash})";

    /// <summary>Every fact in which the two differ, the ledger's value first.</summary>
    private static string Differences(Transaction held, Transaction stated)
    {
        var differences = new List<string>();
        void Compare<T>(string name, T was, T now, Func<T, string> text)
        {
            if (!EqualityComparer<T>.Default.Equals(was, now))
            {
                differences.Add($"{name} {text(was)} in the ledger and {text(now)} in the source");
            }
        }

        Compare("kind", held.Kind, stated.Kind, k => k.ToString());
        Compare("trade date", held.TradeDate, stated.TradeDate, d => $"{d:R}");
        Compare("settlement date", held.SettlementDate, stated.SettlementDate, d => $"{d:R}");
        Compare("cash", held.Cash, stated.Cash, m => m.ToString());
        Compare("shares", held.QuantityChange, stated.QuantityChange, q => q.ToString(CultureInfo.InvariantCulture));
        Compare("price", held.Price, stated.Price, p => p?.ToString() ?? "none");
        Compare("instrument", held.Instrument, stated.Instrument, i => i?.ToString() ?? "none");
        Compare("a reversal", held.IsReversal, stated.IsReversal, r => r ? "yes" : "no");
        return string.Join("; ", differences);
    }
}
