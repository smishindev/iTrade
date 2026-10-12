using ITrade.Application.Fx;
using ITrade.Application.Prices;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Portfolio;

/// <summary>
/// The value of the portfolio on a date: the books of that date from the ledger, the closing price that goes
/// with the date for every instrument held, the cash, and the exchange rate of the date. Nothing is stored —
/// it is worked out every time, from the ledger, the prices and the rates as they are now.
///
/// The date is a trade date (New York). The closes of its session are valued with the Bank of Israel's rate
/// of the same calendar date, which was set some hours before that close — the convention of this platform
/// for looking at the portfolio, not a rule for a tax figure.
/// </summary>
public sealed class PortfolioValuation(
    IHoldingPlaceStore places, ITransactionStore ledger, ClosingPrices prices, FxRates rates)
{
    /// <summary>
    /// Every holding place together. What cannot be valued — no price, no rate — is in the result as such
    /// (<see cref="PortfolioValue"/>); only a ledger that cannot be read as books throws.
    /// </summary>
    public async Task<PortfolioValue> AsOfAsync(LocalDate date, CancellationToken cancellationToken)
    {
        var entries = new List<Transaction>();
        foreach (var place in await places.ListAsync(cancellationToken))
        {
            entries.AddRange(await ledger.ListAsync(place.Id, cancellationToken));
        }

        var books = PortfolioBooks.From(entries, date);
        var found = new Dictionary<InstrumentId, PriceOnDate>();
        foreach (var instrument in books.Positions.Select(p => p.Instrument).Distinct())
        {
            found[instrument] = await prices.OnAsync(instrument, date, cancellationToken);
        }

        var rate = await rates.FindAsync(date, cancellationToken);
        return PortfolioValue.Of(books, found, rate.Rate, rate.WhyNone);
    }
}
