using System.Globalization;
using NodaTime;

namespace ITrade.Domain;

/// <summary>
/// The split days the market reports for a fund (<see cref="FundEvent"/>) held against the splits the ledger of one
/// holding place applied. A split the prices show and the ledger lacks makes the old number of shares be valued at
/// the new price - half the value of a 2:1 split, told as a whole total - and nothing in the valuation can see it;
/// a split the ledger holds and the prices do not show values the new shares at old prices. Either is said with
/// both figures; nothing is repaired here (the statement import records a split, a reversal undoes one).
/// The days must match exactly: the vendor's first day of the split shares and the trade date of the ledger's split.
/// Whether IBKR books a split on that day (and not the evening before) is not known yet - the first real split
/// shows it, and until then a difference of one day reads as both a missing and an unknown split.
/// </summary>
public static class SplitCheck
{
    /// <summary>
    /// How far a stored ratio may be from the true one: half of its last place (<see cref="FundEvent.Scale"/>).
    /// The shares a split gives are held against held × ratio within held × this, and this once more for the
    /// ledger's own six places.
    /// </summary>
    public const decimal RatioPrecision = 0.0000005m;

    /// <param name="ledger">The entries of the ledger, in its order (all places may be given; one is checked).</param>
    /// <param name="place">The holding place whose books are checked.</param>
    /// <param name="events">The funds' stored events; dividends are ignored.</param>
    /// <param name="pricesThrough">
    /// The last session with a close of each instrument: a split after it cannot be in the prices yet, so a split of
    /// the ledger after it is not missing from them.
    /// </param>
    /// <param name="through">The last day looked at; splits after it are not compared.</param>
    /// <param name="nameOf">How an instrument is named to the owner.</param>
    public static IReadOnlyList<string> Of(
        IReadOnlyList<Transaction> ledger,
        HoldingPlaceId place,
        IReadOnlyList<FundEvent> events,
        IReadOnlyDictionary<InstrumentId, LocalDate> pricesThrough,
        LocalDate through,
        Func<InstrumentId, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(pricesThrough);
        ArgumentNullException.ThrowIfNull(nameOf);
        var entries = ledger.Where(e => e.HoldingPlace == place).ToList();
        var differences = new List<string>();
        var inPrices = events.Where(e => e.Kind == FundEventKind.Split && e.Day <= through).ToList();
        var applied = PortfolioBooks.From(entries, through).Splits
            .Where(s => s.Instrument is not null)
            .GroupBy(s => (Instrument: s.Instrument!.Value, Day: s.TradeDate))
            .ToDictionary(g => g.Key, g => g.Sum(s => s.QuantityChange));

        foreach (var split in inPrices)
        {
            // Held on the evening before: a fund bought on or after its split day was bought as the new shares.
            var held = PortfolioBooks.From(entries, split.Day.PlusDays(-1)).QuantityOf(place, split.Instrument).Value;
            if (held == 0m)
            {
                continue;
            }

            var expected = (held * split.Value) - held;
            var changed = applied.GetValueOrDefault((split.Instrument, split.Day));
            // A ratio is stored to six places (1 for 3 is 0.333333): the shares it gives are exact to half of that,
            // and the ledger's new number of shares is itself kept to six places (0.5 → 0.166667).
            var tolerance = (held * RatioPrecision) + RatioPrecision;
            if (changed == 0m)
            {
                differences.Add(Invariant(
                    $"{nameOf(split.Instrument)}: the prices show a split of {Plain(split.Value)} for 1 on {split.Day:R}, and the ledger holds {Plain(held)} shares the evening before and no split that day: from then on the shares are valued at the new price as if they were the old number. The broker's statement brings the split - import it; if the statement shows none, the data may be wrong (a spin-off can be reported as a split)."));
            }
            else if (Math.Abs(changed - expected) > tolerance)
            {
                differences.Add(Invariant(
                    $"{nameOf(split.Instrument)}: the ledger's split of {split.Day:R} changed {Plain(held)} shares by {Plain(changed)}, the prices' {Plain(split.Value)} for 1 would change them by {Plain(expected)}: a fraction paid in cash, or another ratio - look at the broker's statement."));
            }
        }

        foreach (var ((instrument, day), changed) in applied.OrderBy(a => a.Key.Day))
        {
            var known = pricesThrough.TryGetValue(instrument, out var last) && last >= day;
            if (known && changed != 0m && !inPrices.Any(e => e.Instrument == instrument && e.Day == day))
            {
                differences.Add(Invariant(
                    $"{nameOf(instrument)}: the ledger holds a split on {day:R} ({Plain(changed)} shares) that the prices, stored up to {last:R}, do not show: closes before that day are of the old shares. Look at the broker's statement and at the data before trusting a value of that fund."));
            }
        }

        return differences;
    }

    private static string Plain(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
