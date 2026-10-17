using ITrade.Application.Prices;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Portfolio;

/// <summary>
/// The split days of the stored prices (`fund_events`) against the ledger of a holding place, for every fund the
/// ledger ever held there (<see cref="SplitCheck"/>). Reads only.
/// </summary>
public sealed class SplitReconciliation(
    ITransactionStore transactions, IClosingPriceStore prices, IInstrumentStore instruments)
{
    public async Task<IReadOnlyList<string>> AgainstTheLedgerAsync(
        HoldingPlaceId place, LocalDate through, CancellationToken cancellationToken)
    {
        var ledger = await transactions.ListAsync(place, cancellationToken);
        var events = new List<FundEvent>();
        var names = new Dictionary<InstrumentId, string>();
        foreach (var instrument in ledger.Select(e => e.Instrument).OfType<InstrumentId>().Distinct())
        {
            events.AddRange(await prices.ListEventsAsync(instrument, cancellationToken));
            names[instrument] = (await instruments.FindAsync(instrument, cancellationToken))?.Ticker.Value ?? instrument.ToString();
        }

        var pricesThrough = await prices.LatestPerInstrumentAsync(cancellationToken);
        return SplitCheck.Of(ledger, place, events, pricesThrough, through, id => names.GetValueOrDefault(id) ?? id.ToString());
    }
}
