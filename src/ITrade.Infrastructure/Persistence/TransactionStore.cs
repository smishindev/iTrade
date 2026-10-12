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
        var row = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(
            t => t.HoldingPlaceId == holdingPlace && t.Source == source && t.ExternalId == externalId, cancellationToken);
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

    public async Task AddAsync(IReadOnlyCollection<Transaction> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = entries.Select(TransactionRow.From).ToList();
        // One database transaction: all the entries or none. Each row is sent on its own, in the order given,
        // because that order becomes the ledger's order (EF Core is free to reorder the rows of one save).
        var own = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
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
}
