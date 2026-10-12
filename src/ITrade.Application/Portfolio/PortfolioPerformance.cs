using System.Globalization;
using ITrade.Application.Prices;
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
/// The fund the portfolio is compared with, and what is left of its dividends after withholding. Which fund
/// stands for "the index" is the owner's choice (PLAN: a broad world fund); the share kept is an assumption
/// about tax and carries its source where it is set.
/// </summary>
public sealed record IndexFund(string Ticker, decimal DividendKept);

/// <summary>
/// What the same money, paid in and taken out on the same days, would have earned in the index fund
/// (<see cref="IndexPortfolio"/>) — per currency, with the same two returns as the portfolio itself, so that
/// each can be put next to its counterpart. No commission is taken from the index, and its dividends are
/// bought back at once: it is flattered by that much.
/// </summary>
/// <param name="Ticker">The fund that stands for the index.</param>
/// <param name="WhyNone">Why there is no comparison at all; empty when there is one.</param>
public sealed record IndexPerformance(string Ticker, IReadOnlyList<PerformanceIn> ByCurrency, IReadOnlyList<string> WhyNone)
{
    public PerformanceIn? In(Currency currency) => ByCurrency.SingleOrDefault(p => p.Currency == currency);
}

/// <summary>
/// What the whole portfolio earned from <paramref name="First"/> to <paramref name="Last"/>, both included,
/// by the ledger: once in dollars and once in shekels. The two differ by what the dollar did against the
/// shekel in between.
/// </summary>
/// <param name="ValuedOn">The days the portfolio was valued on for this: the day before the period, every day with a flow and the day before a contribution, and the last day.</param>
/// <param name="Remarks">What the valuations of those days had to say (a provisional price, a position not valued), each with its day.</param>
/// <param name="Index">The same flows in the index fund.</param>
public sealed record PerformanceReport(
    LocalDate First,
    LocalDate Last,
    IReadOnlyList<PerformanceIn> ByCurrency,
    IReadOnlyList<LocalDate> ValuedOn,
    IReadOnlyList<string> Remarks,
    IndexPerformance Index)
{
    public PerformanceIn In(Currency currency) => ByCurrency.Single(p => p.Currency == currency);
}

/// <summary>
/// Time-weighted and money-weighted returns of the portfolio over a period, in dollars and in shekels
/// (<see cref="PeriodReturns"/>), next to what the same flows would have earned in the index fund. Nothing is
/// stored: the values come from <see cref="PortfolioValuation"/> on the few days that matter — the returns
/// between the owner's contributions and withdrawals need the value around each of them, not of every day —
/// and the flows from the ledger. For looking at, never for tax.
///
/// A flow in another currency is converted with the exchange rate of its day, the same one that day's value
/// is converted with. If one of the values is not a complete total — a price is missing, there is no rate —
/// there are no returns in that currency, and the report says why: a return over a hole would be a guess.
/// </summary>
public sealed class PortfolioPerformance(
    IHoldingPlaceStore places,
    ITransactionStore ledger,
    IInstrumentStore instruments,
    PortfolioValuation valuation,
    ClosingPrices prices,
    IndexFund index)
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

        var catalogue = await instruments.ListAsync(cancellationToken);
        var names = catalogue.ToDictionary(i => i.Id, i => i.Ticker.Value);
        var values = new Dictionary<LocalDate, PortfolioValue>();
        var remarks = new List<string>();
        foreach (var day in days)
        {
            var value = await valuation.AsOfAsync(day, cancellationToken);
            values[day] = value;
            remarks.AddRange(value.Remarks(id => names.GetValueOrDefault(id, id.ToString())).Select(r => Invariant($"{day:R}: {r}")));
        }

        var told = Currencies.ToDictionary(currency => currency, currency => In(currency, days, values, flows));
        return new PerformanceReport(
            first, last, [.. Currencies.Select(c => told[c].Performance)], [.. days], remarks,
            await IndexAsync(catalogue, told, values, cancellationToken));
    }

    /// <summary>The portfolio in one currency: its returns, and the points they were computed from (null when incomplete).</summary>
    private sealed record Told(PerformanceIn Performance, IReadOnlyList<ValuePoint>? Points);

    private static Told In(
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

        return whyNone.Count > 0
            ? new Told(new PerformanceIn(currency, null, whyNone, provisional, []), null)
            : new Told(Computed(currency, points, provisional), points);
    }

    private static PerformanceIn Computed(Currency currency, IReadOnlyList<ValuePoint> points, bool provisional)
    {
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

        if (returns.InvestedSince is { } since && returns.MoneyWeighted is null
            && Period.DaysBetween(since, returns.Closing) > returns.DaysAtWork)
        {
            notes.Add("The portfolio stood empty for a while: no money-weighted return is told for the period as a whole.");
        }

        return new PerformanceIn(currency, returns, [], provisional, notes);
    }

    /// <summary>
    /// The index portfolio that follows the real one's flows. It is worked out in the currency the fund is
    /// priced in and then told in the other one with the rate of each day — the same rates the real
    /// portfolio's values were told with.
    /// </summary>
    private async Task<IndexPerformance> IndexAsync(
        IReadOnlyList<Instrument> catalogue,
        Dictionary<Currency, Told> told,
        Dictionary<LocalDate, PortfolioValue> values,
        CancellationToken cancellationToken)
    {
        IndexPerformance None(params string[] why) => new(index.Ticker, [], why);
        var fund = catalogue.FirstOrDefault(i => string.Equals(i.Ticker.Value, index.Ticker, StringComparison.OrdinalIgnoreCase));
        if (fund is null)
        {
            return None($"{index.Ticker}, the fund that stands for the index, is not in the instrument catalogue.");
        }

        if (!told.TryGetValue(fund.Currency, out var own) || own.Points is not { } points)
        {
            return None($"The portfolio's own values in {fund.Currency} are not complete, so there is nothing for the index to follow.");
        }

        var (opening, closing) = (points[0].Date, points[^1].Date);
        var events = await prices.EventsAsync(fund.Id, opening, closing, cancellationToken);
        var closes = new Dictionary<LocalDate, decimal>();
        var why = new List<string>();
        var provisional = false;
        var needed = points.Select(p => p.Date).Concat(events.Where(e => e.Kind == FundEventKind.Dividend).Select(e => e.Day));
        foreach (var day in needed.Distinct().Order())
        {
            var price = await prices.OnAsync(fund.Id, day, cancellationToken);
            if (price.Price is not { } close)
            {
                why.Add(Invariant($"{day:R}: there is no closing price of {fund.Ticker} to use."));
            }
            else
            {
                // A carried close is never from before a split of the fund: an event is stored only for a
                // session, and a session without the fund's close gives no price at all.
                closes[day] = close.Close.Value;
                provisional |= price.IsProvisional;
            }
        }

        if (why.Count > 0)
        {
            return None([.. why]);
        }

        IReadOnlyList<ValuePoint> followed;
        try
        {
            followed = IndexPortfolio.Following(points, events, closes, index.DividendKept);
        }
        catch (InvalidOperationException e)
        {
            return None(e.Message);
        }

        var byCurrency = new List<PerformanceIn>();
        foreach (var currency in Currencies)
        {
            // The index follows the portfolio's flows: where those were told at a provisional rate, so is it.
            var followsProvisional = told[currency].Performance.IsProvisional;
            if (currency == fund.Currency)
            {
                byCurrency.Add(Computed(currency, followed, provisional || followsProvisional));
            }
            else if (told[currency].Points is not { } inThisCurrency)
            {
                byCurrency.Add(new PerformanceIn(
                    currency, null, [$"The portfolio's own values in {currency} are not complete, so there is nothing for the index to follow."],
                    provisional, []));
            }
            else
            {
                // The index's value of each day at that day's rate; the flows are the real portfolio's own.
                var converted = new List<ValuePoint>(followed.Count);
                var noRate = new List<string>();
                var atAProvisionalRate = false;
                for (var i = 0; i < followed.Count; i++)
                {
                    var value = followed[i].Value;
                    if (value != 0m)
                    {
                        // The portfolio may have needed no rate that day - it stood empty - while the index
                        // portfolio still holds something.
                        if (values[followed[i].Date].Rate is not { } rate)
                        {
                            noRate.Add(Invariant($"{followed[i].Date:R}: there is no exchange rate to tell the index's value in {currency}."));
                            continue;
                        }

                        value = rate.Rate.Convert(new Money(value, fund.Currency)).Amount;
                        atAProvisionalRate |= rate.IsProvisional;
                    }

                    converted.Add(inThisCurrency[i] with { Value = value });
                }

                byCurrency.Add(noRate.Count > 0
                    ? new PerformanceIn(currency, null, noRate, provisional, [])
                    : Computed(currency, converted, provisional || atAProvisionalRate || followsProvisional));
            }
        }

        return new IndexPerformance(fund.Ticker.Value, byCurrency, []);
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
