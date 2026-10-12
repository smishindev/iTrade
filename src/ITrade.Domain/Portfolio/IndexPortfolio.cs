using NodaTime;

namespace ITrade.Domain;

/// <summary>
/// The portfolio the owner would have had by putting the same money, on the same days, into one index fund —
/// what the real portfolio is compared with. It follows the real one's contributions and withdrawals:
///
/// <list type="bullet">
/// <item>what was there when the period opened buys the fund at the close of that day;</item>
/// <item>a contribution buys the fund at the close of its own day — the first price anyone could have had
/// for it — and a withdrawal sells at the close of its day;</item>
/// <item>a dividend buys more of the fund at the close of its ex-date, after what is withheld from it;</item>
/// <item>a split changes the number of shares on its day, as it changes the price.</item>
/// </list>
///
/// No commission and no spread are taken: the comparison flatters the index by what buying it would have
/// cost. For looking at, never for tax.
/// </summary>
public static class IndexPortfolio
{
    /// <summary>
    /// The values of the index portfolio on the days of <paramref name="points"/>, with the same flows — ready
    /// for <see cref="PeriodReturns.Of"/>. Everything is in the currency the fund is priced in.
    /// </summary>
    /// <param name="points">The real portfolio's values, earliest first; the first opens the period.</param>
    /// <param name="events">The fund's dividends and splits; those after the opening day up to the last day count.</param>
    /// <param name="closes">The fund's close, as traded, for every day of <paramref name="points"/> and every dividend day.</param>
    /// <param name="dividendKept">The share of a dividend left after withholding: 0.75 when a quarter is withheld.</param>
    public static IReadOnlyList<ValuePoint> Following(
        IReadOnlyList<ValuePoint> points,
        IReadOnlyList<FundEvent> events,
        IReadOnlyDictionary<LocalDate, decimal> closes,
        decimal dividendKept)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(closes);
        if (points.Count < 2)
        {
            throw new ArgumentException("A period needs its opening value and at least one later day.", nameof(points));
        }

        if (dividendKept is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(dividendKept), dividendKept, "The share of a dividend that is kept is above 0 and at most 1.");
        }

        var (opening, closing) = (points[0], points[^1]);
        decimal CloseOn(LocalDate day) => closes.TryGetValue(day, out var close) && close > 0m
            ? close
            : throw new InvalidOperationException($"There is no close of the index fund for {day:R}.");

        var shares = opening.Value == 0m ? 0m : opening.Value / CloseOn(opening.Date);
        var followed = new List<ValuePoint>(points.Count) { opening };
        var during = events.Where(e => e.Day > opening.Date && e.Day <= closing.Date).ToLookup(e => e.Day);
        var byDay = points.Skip(1).ToDictionary(p => p.Date);
        foreach (var day in byDay.Keys.Concat(during.Select(e => e.Key)).Distinct().Order())
        {
            // The morning: a split gives new shares for old ones, and the day's dividend goes to the shares held.
            foreach (var split in during[day].Where(e => e.Kind == FundEventKind.Split))
            {
                shares *= split.Value;
            }

            foreach (var dividend in during[day].Where(e => e.Kind == FundEventKind.Dividend && shares > 0m))
            {
                shares += shares * dividend.Value * dividendKept / CloseOn(day);
            }

            if (!byDay.TryGetValue(day, out var point))
            {
                continue;
            }

            // The evening: the owner's money buys or sells at the close.
            if (point.Inflow != 0m || point.Outflow != 0m)
            {
                shares += (point.Inflow - point.Outflow) / CloseOn(day);
            }

            if (shares < 0m)
            {
                // A division leaves a hair when everything is taken out to the cent.
                shares = shares > -0.000000000000001m
                    ? 0m
                    : throw new InvalidOperationException(
                        $"On {day:R} more is taken out than the index portfolio holds: it cannot follow the real one any further.");
            }

            followed.Add(point with { Value = shares == 0m ? 0m : shares * CloseOn(day) });
        }

        return followed;
    }
}
