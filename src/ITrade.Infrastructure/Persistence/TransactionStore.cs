using ITrade.Application.Portfolio;
using ITrade.Domain;
using Microsoft.EntityFrameworkCore;

namespace ITrade.Infrastructure.Persistence;

/// <summary>`transactions` behind <see cref="ITransactionStore"/>: reads and inserts, nothing else.</summary>
public sealed class TransactionStore(ITradeDbContext db) : ITransactionStore
{
    public async Task<Transaction?> FindAsync(TransactionId id, CancellationToken cancellationToken) =>
        (await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken))?.ToDomain();

    public async Task<Transaction?> FindByExternalIdAsync(
        HoldingPlaceId holdingPlace, TransactionSource source, string externalId, CancellationToken cancellationToken)
    {
        var row = await db.Transactions.AsNoTracking()
            .Where(t => t.HoldingPlaceId == holdingPlace && t.Source == source && t.ExternalId == externalId)
            .OrderByDescending(t => t.ExternalRevision)
            .FirstOrDefaultAsync(cancellationToken);
        return row?.ToDomain();
    }

    public async Task<Transaction?> FindReversalOfAsync(TransactionId id, CancellationToken cancellationToken) =>
        (await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.Reverses == id, cancellationToken))?.ToDomain();

    public async Task<IReadOnlyList<Transaction>> ListRelatedToAsync(TransactionId id, CancellationToken cancellationToken)
    {
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.RelatedTo == id)
            .OrderBy(t => t.Sequence)
            .ToListAsync(cancellationToken);
        return rows.ConvertAll(r => r.ToDomain());
    }

    public async Task<IReadOnlyList<Transaction>> ListAsync(HoldingPlaceId holdingPlace, CancellationToken cancellationToken)
    {
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.HoldingPlaceId == holdingPlace)
            .OrderBy(t => t.TradeDate).ThenBy(t => t.Sequence)
            .ToListAsync(cancellationToken);
        return rows.ConvertAll(r => r.ToDomain());
    }

    public async Task AddAsync(IReadOnlyList<Transaction> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (db.ChangeTracker.HasChanges())
        {
            // Saving a ledger row saves everything the context tracks: the other changes would be written with
            // the first row and, if a later row is refused, rolled back without anyone being told.
            throw new InvalidOperationException(
                "The ledger is written on its own: save or discard the other changes of this unit of work first.");
        }

        var rows = entries.Select(TransactionRow.From).ToList();
        // All the entries or none. Each row is sent on its own, in the order given, because that order becomes
        // the ledger's order (EF Core is free to reorder the rows of one save). Inside a transaction of the
        // caller the batch is a savepoint, so a refused batch leaves nothing of itself for the caller to commit.
        var outer = db.Database.CurrentTransaction;
        var own = outer is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (outer is not null)
        {
            await outer.CreateSavepointAsync(Savepoint, cancellationToken);
        }

        try
        {
            foreach (var row in rows)
            {
                db.Transactions.Add(row);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }
            else
            {
                await outer!.ReleaseSavepointAsync(Savepoint, cancellationToken);
            }
        }
        catch when (outer is not null)
        {
            await outer.RollbackToSavepointAsync(Savepoint, CancellationToken.None);
            throw;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync(); // rolls back what was not committed
            }

            // Nothing of the ledger stays tracked: a written row is never to be updated, and a refused one must
            // not ride along with the next thing this context saves.
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    private const string Savepoint = "ledger_batch";
}
