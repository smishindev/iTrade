using System.Globalization;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Portfolio;

/// <summary>
/// The returns of a period in one currency — or, when they cannot be computed honestly, the reasons.
/// </summary>
/// <param name="Returns">Null when a value or a flow of the period could not be told in this currency.</param>
/// <param name="WhyNone">The reasons, each naming its day.</param>
/// <param name="IsProvisional">A provisional price or rate is in one of the values: the figures are expected to change.</param>
/// <param name="Notes">What has to be said next to the returns that were computed.</param>
public sealed record PerformanceIn(
    Currency Currency, PeriodReturns? Returns, IReadOnlyList<string> WhyNone, bool IsProvisional, IReadOnlyList<string> Notes);

/// <summary>
/// What the whole portfolio earned from <paramref name="First"/> to <paramref name="Last"/>, both included,
/// by the ledger: once in dollars and once in shekels. The two differ by what the dollar did against the
/// shekel in between.
/// </summary>
/// <param name="ValuedOn">The days the portfolio was valued on for this: the day before the period, every day with a flow and the day before a contribution, and the last day.</param>
/// <param name="Remarks">What the valuations of those days had to say (a provisional price, a position not valued), each with its day.</param>
public sealed record PerformanceReport(
    LocalDate First,
    LocalDate Last,
    IReadOnlyList<PerformanceIn> ByCurrency,
    IReadOnlyList<LocalDate> ValuedOn,
    IReadOnlyList<string> Remarks)
{
    public PerformanceIn In(Currency currency) => ByCurrency.Single(p => p.Currency == currency);
}

/// <summary>
/// Time-weighted and money-weighted returns of the portfolio over a period, in dollars and in shekels
/// (<see cref="PeriodReturns"/>). Nothing is stored: the values come from <see cref="PortfolioValuation"/> on
/// the few days that matter — the returns between the owner's contributions and withdrawals need the value
/// around each of them, not of every day — and the flows from the ledger. For looking at, never for tax.
///
/// A flow in another currency is converted with the exchange rate of its day, the same one that day's value
/// is converted with. If one of the values is not a complete total — a price is missing, there is no rate —
/// there are no returns in that currency, and the report says why: a return over a hole would be a guess.
/// </summary>
public sealed class PortfolioPerformance(
    IHoldingPlaceStore places, ITransactionStore ledger, IInstrumentStore instruments, PortfolioValuation valuation)
{
    private static readonly Currency[] Currencies = [Currency.Usd, Currency.Ils];

    public async Task<PerformanceReport> OverAsync(LocalDate first, LocalDate last, CancellationToken cancellationToken)
    {
        if (last < first)
        {
            throw new ArgumentException($"The period ends on {last:R}, before it starts on {first:R}.", nameof(last));
        }

        var entries = new List<Transaction>();
        foreach (var place in await places.ListAsync(cancellationToken))
        {
            entries.AddRange(await ledger.ListAsync(place.Id, cancellationToken));
        }

        var flows = OwnerFlow.Of(entries, first, last);
        var opening = first.PlusDays(-1);
        var days = new SortedSet<LocalDate> { opening, last };
        foreach (var flow in flows)
        {
            days.Add(flow.Date);
            if (flow.Amount.Amount > 0m)
            {
                days.Add(flow.Date.PlusDays(-1)); // what the new money is added to
            }
        }

        var names = (await instruments.ListAsync(cancellationToken)).ToDictionary(i => i.Id, i => i.Ticker.Value);
        var values = new Dictionary<LocalDate, PortfolioValue>();
        var remarks = new List<string>();
        foreach (var day in days)
        {
            var value = await valuation.AsOfAsync(day, cancellationToken);
            values[day] = value;
            remarks.AddRange(value.Remarks(id => names.GetValueOrDefault(id, id.ToString())).Select(r => Invariant($"{day:R}: {r}")));
        }

        var byCurrency = Currencies.Select(currency => In(currency, days, values, flows)).ToList();
        return new PerformanceReport(first, last, byCurrency, [.. days], remarks);
    }

    private static PerformanceIn In(
        Currency currency, SortedSet<LocalDate> days, Dictionary<LocalDate, PortfolioValue> values, IReadOnlyList<OwnerFlow> flows)
    {
        var whyNone = new List<string>();
        var points = new List<ValuePoint>();
        var provisional = false;
        foreach (var day in days)
        {
            var value = values[day];
            var total = value.In(currency);
            if (total.Total is not { } worth)
            {
                whyNone.Add(Invariant($"{day:R}: the value in {currency} is not complete - {WhatIsLeftOut(total)}."));
                continue;
            }

            provisional |= total.IsProvisional;
            var (paidIn, takenOut) = (0m, 0m);
            foreach (var flow in flows.Where(f => f.Date == day))
            {
                var amount = flow.Amount;
                if (amount.Currency != currency)
                {
                    if (value.Rate is null)
                    {
                        whyNone.Add(Invariant($"{day:R}: {amount} came or went and there is no rate to tell it in {currency}."));
                        continue;
                    }

                    amount = value.Rate.Rate.Convert(amount);
                    provisional |= value.Rate.IsProvisional;
                }

                paidIn += Math.Max(amount.Amount, 0m);
                takenOut += Math.Max(-amount.Amount, 0m);
            }

            points.Add(new ValuePoint(day, worth.Amount, paidIn, takenOut));
        }

        if (whyNone.Count > 0)
        {
            return new PerformanceIn(currency, null, whyNone, provisional, []);
        }

        PeriodReturns returns;
        try
        {
            returns = PeriodReturns.Of(points);
        }
        catch (ArgumentException e)
        {
            // A value below nothing (cash overdrawn): no return can be computed from that.
            return new PerformanceIn(currency, null, [e.Message], provisional, []);
        }

        var notes = new List<string>();
        if (returns.GainedOnNothing != 0m)
        {
            notes.Add(Invariant(
                $"{new Money(returns.GainedOnNothing, currency)} turned up while nothing was invested - a payment after everything was taken out, or a contribution the ledger does not hold. It is in the gain and in neither return."));
        }

        if (returns.InvestedSince is not null && returns.MoneyWeighted is null && returns.MoneyWeightedAnnual is null
            && Period.DaysBetween(returns.InvestedSince.Value, returns.Closing) > returns.DaysAtWork)
        {
            notes.Add("The portfolio stood empty for a while: no money-weighted return is told for the period as a whole.");
        }

        return new PerformanceIn(currency, returns, [], provisional, notes);
    }

    private static string WhatIsLeftOut(PortfolioTotal total)
    {
        var parts = new List<string>();
        if (total.NotValued.Count > 0)
        {
            parts.Add(Invariant($"{total.NotValued.Count} position(s) have no usable price"));
        }

        if (total.NotConverted.Count > 0)
        {
            parts.Add($"{string.Join(" and ", total.NotConverted)} could not be converted");
        }

        return string.Join(", ", parts);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
