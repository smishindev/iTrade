using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Portfolio;

/// <summary>
/// The books of a holding place on a date: positions with their lots, cash by currency, matched sales. Nothing
/// is stored — they are computed from the ledger every time, so they cannot drift away from it.
/// </summary>
public sealed class PortfolioBooksReader(ITransactionStore store)
{
    public async Task<PortfolioBooks> AsOfAsync(
        HoldingPlaceId holdingPlace, LocalDate asOf, CancellationToken cancellationToken) =>
        PortfolioBooks.From(await store.ListAsync(holdingPlace, cancellationToken), asOf);
}
