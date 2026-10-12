using NodaTime;

namespace ITrade.Domain;

/// <summary>
/// What a portfolio was worth at the end of a day, in one currency, with the owner's money that came in and
/// went out on that day. <paramref name="Inflow"/> and <paramref name="Outflow"/> are both given as positive
/// amounts. Only money that crosses the border of the portfolio counts — a contribution, a withdrawal; a
/// dividend, a fee or a trade is what happens inside.
/// </summary>
public sealed record ValuePoint(LocalDate Date, decimal Value, decimal Inflow = 0m, decimal Outflow = 0m);

/// <summary>The owner's money crossing the border of the portfolio: paid in (positive) or taken out (negative).</summary>
public sealed record OwnerFlow(LocalDate Date, HoldingPlaceId HoldingPlace, Money Amount)
{
    /// <summary>
    /// The contributions and withdrawals of the trade dates from <paramref name="first"/> to
    /// <paramref name="last"/>, in the ledger's order. Only deposits and withdrawals are the owner's money
    /// coming and going; a dividend, interest, a fee, a tax or a conversion happens inside the portfolio and
    /// is part of what it earned. An entry that was reversed, and its reversal, are no flow.
    /// </summary>
    public static IReadOnlyList<OwnerFlow> Of(IEnumerable<Transaction> ledger, LocalDate first, LocalDate last)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var all = ledger as IReadOnlyCollection<Transaction> ?? [.. ledger];
        var cancelled = all.Where(e => e.Reverses is not null).Select(e => e.Reverses!.Value).ToHashSet();
        return all
            .Where(e => e.Kind is TransactionKind.Deposit or TransactionKind.Withdrawal
                && !e.IsReversal && !cancelled.Contains(e.Id)
                && e.TradeDate >= first && e.TradeDate <= last)
            .Select(e => new OwnerFlow(e.TradeDate, e.HoldingPlace, e.Cash))
            .ToList();
    }
}

/// <summary>
/// What a portfolio earned over a period, told two ways, in one currency. Returns are fractions: 0.05 is 5%.
///
/// <b>Time-weighted</b> is what one unit of money earned that was there the whole time: it does not depend on
/// when, or how much, the owner paid in or took out — the figure to compare with an index. <b>Money-weighted</b>
/// is what the owner's own money earned, each amount for the time it was in: paying in before a rise makes it
/// higher than the time-weighted return, paying in before a fall lower.
///
/// One convention of timing for both, a named assumption: a contribution counts from the start of its day —
/// the end of the day before — and a withdrawal at the end of its day. So money is never counted as working
/// before it was there, and never as gone while it still was.
/// </summary>
/// <param name="Opening">The day whose end the period starts from.</param>
/// <param name="Closing">The last day of the period.</param>
/// <param name="InvestedSince">The end of this day is when money was first in the portfolio; null if there never was any.</param>
/// <param name="OpeningValue">The value at the end of <paramref name="Opening"/>.</param>
/// <param name="ClosingValue">The value at the end of <paramref name="Closing"/>.</param>
/// <param name="Contributed">Everything paid in during the period.</param>
/// <param name="Withdrawn">Everything taken out during the period.</param>
/// <param name="DaysAtWork">The days on which there was money in the portfolio. A stretch in which it stood empty is not counted.</param>
/// <param name="GainedOnNothing">
/// Money that turned up while nothing was invested — a dividend paid after everything was taken out, or a
/// contribution the ledger does not hold. It is in <see cref="Gain"/>; no rate of return can be told for it,
/// so it is in neither return. Not zero: say so next to the figures.
/// </param>
/// <param name="TimeWeighted">Over the period, not per year.</param>
/// <param name="TimeWeightedAnnual">Per year — only for a year or more at work: a few weeks stretched to a year tell nothing.</param>
/// <param name="MoneyWeighted">
/// Over the period; null when there is none: nothing was invested, no rate explains the flows, or the
/// portfolio stood empty in between — a rate carried across days on which the money was not there would
/// credit it with earnings it did not make.
/// </param>
/// <param name="MoneyWeightedAnnual">Per year while invested (what a spreadsheet calls XIRR) — only for a year or more at work.</param>
public sealed record PeriodReturns(
    LocalDate Opening,
    LocalDate Closing,
    LocalDate? InvestedSince,
    int DaysAtWork,
    decimal OpeningValue,
    decimal ClosingValue,
    decimal Contributed,
    decimal Withdrawn,
    decimal GainedOnNothing,
    decimal TimeWeighted,
    decimal? TimeWeightedAnnual,
    decimal? MoneyWeighted,
    decimal? MoneyWeightedAnnual)
{
    /// <summary>
    /// Decimal places of a rate that needed a power or a root (the money-weighted ones, and anything per year).
    /// A rate is a ratio, not money: such a one is computed in floating point and rounded to this. The
    /// time-weighted return over the period is a chain of exact divisions and is not rounded.
    /// </summary>
    public const int RateScale = 10;

    private const int DaysInAYear = 365;

    /// <summary>
    /// What the portfolio gained in money: a contribution is not a gain, and a withdrawal is not a loss.
    /// </summary>
    public decimal Gain => ClosingValue + Withdrawn - OpeningValue - Contributed;

    /// <summary>
    /// Computes the returns from the values at the ends of days, earliest first. The first point opens the
    /// period: its value is what was there before, and it carries no flow. A day with a contribution needs the
    /// value of the day before it as the point before — that is what the new money is added to; no other day
    /// in between is needed, as long as every day with a flow is a point.
    ///
    /// Refused: fewer than two points, dates out of order, a negative value or flow, and a contribution
    /// without the day before it.
    /// </summary>
    public static PeriodReturns Of(IReadOnlyList<ValuePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            throw new ArgumentException("A period needs its opening value and at least one later day.", nameof(points));
        }

        var opening = points[0];
        if (opening.Inflow != 0m || opening.Outflow != 0m)
        {
            throw new ArgumentException(
                $"The opening value of {opening.Date:R} is what was there before the period: it carries no flow.", nameof(points));
        }

        var growth = 1m;
        var (daysAtWork, gainedOnNothing, stoodEmpty) = (0, 0m, false);
        LocalDate? investedSince = opening.Value > 0m ? opening.Date : null;
        var flows = new List<(LocalDate On, decimal Amount)>();
        if (opening.Value > 0m)
        {
            flows.Add((opening.Date, opening.Value));
        }

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.Value < 0m || point.Inflow < 0m || point.Outflow < 0m)
            {
                throw new ArgumentException($"The value and the flows of {point.Date:R} cannot be negative.", nameof(points));
            }

            if (i == 0)
            {
                continue;
            }

            var before = points[i - 1];
            if (point.Date <= before.Date)
            {
                throw new ArgumentException($"{point.Date:R} comes after {before.Date:R}: the days are not in order.", nameof(points));
            }

            if (point.Inflow > 0m && before.Date != point.Date.PlusDays(-1))
            {
                throw new ArgumentException(
                    $"Money came in on {point.Date:R}: the value of the day before it is needed, and the point before is {before.Date:R}.",
                    nameof(points));
            }

            // The money at work during the step, and what it had become by the end of it.
            var atWork = before.Value + point.Inflow;
            var became = point.Value + point.Outflow;
            if (atWork > 0m)
            {
                growth *= became / atWork;
                investedSince ??= before.Date;
                daysAtWork += Period.DaysBetween(before.Date, point.Date);
            }
            else
            {
                // Nothing was invested during the step. Whatever is there at its end was earned by no money:
                // it counts as a gain and is left out of the chain.
                gainedOnNothing += became;
                stoodEmpty |= investedSince is not null;
            }

            if (point.Inflow > 0m)
            {
                flows.Add((before.Date, point.Inflow));
            }

            if (point.Outflow > 0m)
            {
                flows.Add((point.Date, -point.Outflow));
            }
        }

        var closing = points[^1];
        if (closing.Value > 0m)
        {
            flows.Add((closing.Date, -closing.Value));
        }

        var aYearOrMore = daysAtWork >= DaysInAYear;
        var timeWeighted = growth - 1m;
        var dailyRate = investedSince is null ? null : DailyRate(flows);
        return new PeriodReturns(
            opening.Date,
            closing.Date,
            investedSince,
            daysAtWork,
            opening.Value,
            closing.Value,
            points.Sum(p => p.Inflow),
            points.Sum(p => p.Outflow),
            gainedOnNothing,
            timeWeighted,
            aYearOrMore ? Rate(Math.Pow((double)growth, (double)DaysInAYear / daysAtWork) - 1d) : null,
            dailyRate is { } rate && !stoodEmpty ? Rate(Math.Exp(rate * daysAtWork) - 1d) : null,
            dailyRate is { } annual && aYearOrMore ? Rate(Math.Exp(annual * DaysInAYear) - 1d) : null);
    }

    /// <summary>
    /// The one rate at which all the money put in grows into all the money taken out and the value left —
    /// as a continuous rate per day; null when there is none. Money in is positive, money out and the value
    /// left are negative; each is carried from its day to the first day at the rate, and the sum has to be zero.
    /// </summary>
    private static double? DailyRate(List<(LocalDate On, decimal Amount)> flows)
    {
        if (!flows.Exists(f => f.Amount > 0m))
        {
            return null;
        }

        if (!flows.Exists(f => f.Amount < 0m))
        {
            return double.NegativeInfinity; // everything paid in is gone: -100%
        }

        var first = flows.Min(f => f.On);
        var dated = flows.ConvertAll(f => (Days: (double)Period.DaysBetween(first, f.On), Amount: (double)f.Amount));
        double Sum(double rate) => dated.Sum(f => f.Amount * Math.Exp(-rate * f.Days));

        // As wide as a number allows: over the whole period nothing grows, or shrinks, by more than e^600.
        var widest = 600d / Math.Max(1d, dated.Max(f => f.Days));
        var (low, high) = (-widest, widest);
        var (atLow, atHigh) = (Sum(low), Sum(high));
        if (!double.IsFinite(atLow) || !double.IsFinite(atHigh) || Math.Sign(atLow) == Math.Sign(atHigh))
        {
            return null;
        }

        for (var i = 0; i < 200 && high - low > 1e-16; i++)
        {
            var middle = (low + high) / 2d;
            var atMiddle = Sum(middle);
            if (Math.Sign(atMiddle) == Math.Sign(atLow))
            {
                (low, atLow) = (middle, atMiddle);
            }
            else
            {
                high = middle;
            }
        }

        return (low + high) / 2d;
    }

    /// <summary>A floating-point rate as a decimal of <see cref="RateScale"/> places; null when it is no number to show.</summary>
    private static decimal? Rate(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 1e12 ? decimal.Round((decimal)value, RateScale) : null;
}
