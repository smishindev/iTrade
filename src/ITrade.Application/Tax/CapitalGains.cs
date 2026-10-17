using System.Globalization;
using ITrade.Application.Fx;
using ITrade.Application.Portfolio;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Tax;

/// <summary>
/// The sales of a tax year in shekels: every part of a sale taken from a lot, with its real gain or its offsettable
/// loss (<see cref="RealGain"/>). <paramref name="NotComputed"/> names every part that could not be computed, and
/// why; while it is not empty, the sums leave something out and are not given. <paramref name="YearOver"/> is false
/// while the year runs: the sums are then of the sales recorded so far, not the year's. <paramref name="Remarks"/>
/// are the sentences to show next to the figures: the year not over, computed parts that rest on an open question
/// (a loss with the fund bought again around it or the days for that not over, O9; a fraction of a share, O10).
/// <paramref name="Assumptions"/> are the parameters the figures rest on, and <paramref name="ParametersFrom"/> the
/// file they were read from. An estimate, not checked by an accountant.
/// </summary>
public sealed record CapitalGainsOfAYear(
    int Year,
    bool YearOver,
    IReadOnlyList<SaleInShekels> Sales,
    IReadOnlyList<string> NotComputed,
    IReadOnlyList<string> Remarks,
    IReadOnlyList<(string Name, TaxSource Source, string? OpenQuestion)> Assumptions,
    string ParametersFrom)
{
    /// <summary>Every part of every recorded sale of the year was computed.</summary>
    public bool AllComputed => NotComputed.Count == 0;

    /// <summary>The year's figures: the year is over and nothing was left out.</summary>
    public bool IsComplete => AllComputed && YearOver;

    /// <summary>The sum of the real gains of the recorded sales; null while something was not computed.</summary>
    public decimal? RealGain => AllComputed ? Sales.Sum(s => s.RealGain) : null;

    /// <summary>The sum of the offsettable losses of the recorded sales; null while something was not computed.</summary>
    public decimal? OffsettableLoss => AllComputed ? Sales.Sum(s => s.OffsettableLoss) : null;
}

/// <summary>
/// Works out the sales of a tax year from the books of each holding place (lots matched first-in-first-out within
/// the place, docs/tax-sources.md R4) and the Bank of Israel's rates of the trade days (R3). Nothing is stored: a
/// later reversal or a rate the bank publishes late changes a past year's figures, so a report that hands a figure
/// on keeps the rates it was computed with. A part of a sale is not computed - and said - when its instrument has
/// corporate-action cash the lots do not hold, the lot was bought before 2006, a rate is missing, provisional or of
/// another source, or it is not a dollar security. What the ledger holds is all it knows: a statement not imported
/// yet is a sale not counted (the reconciliation, not this, says how far the ledger reaches).
/// </summary>
public sealed class CapitalGains(IHoldingPlaceStore places, ITransactionStore transactions, FxRates rates, TaxParameters tax, IClock clock)
{
    /// <summary>
    /// How far around a sale at a loss a purchase of the same fund is named (O9). The US wash-sale window, used only
    /// to point at the case: whether Israel has a rule for it is open.
    /// </summary>
    public const int RepurchaseDays = 30;

    private static readonly string[] Rests =
    [
        "real_gain", "lot_matching", "lot_matching_abroad", "exchange_rate", "exchange_rate_day_of_a_trade",
        "repurchase_after_a_loss", "fractional_shares",
    ];

    public async Task<CapitalGainsOfAYear> OfYearAsync(int year, CancellationToken cancellationToken)
    {
        // The code follows these rules; parameters that say otherwise are a change it does not know.
        if (tax.RealGain.Value != RealGainRule.ExchangeRateAsIndex || tax.LotMatching.Value != LotMatching.Fifo
            || tax.ExchangeRate.Value != TaxExchangeRate.BankOfIsraelRepresentative
            || tax.ExchangeRateDayOfATrade.Value != TradeRateDay.EarlierOfTradeAndPayment)
        {
            throw new InvalidOperationException("config/tax.toml names a real-gain, lot or exchange-rate rule the capital-gains code does not follow.");
        }

        // An answer to one of these questions changes what the code has to do; until it does, it refuses.
        if (!tax.LotMatchingAbroad.IsOpen || !tax.RepurchaseAfterALoss.IsOpen || !tax.FractionalShares.IsOpen)
        {
            throw new InvalidOperationException("config/tax.toml answers O2, O9 or O10, which the capital-gains code still treats as open.");
        }

        // A sale of 31 December in New York is a sale of that year; the year is over once New York is past it.
        var today = TradeDate.Of(clock.GetCurrentInstant()).Value;
        var ledgers = new List<(HoldingPlace Place, IReadOnlyList<Transaction> Ledger)>();
        foreach (var place in await places.ListAsync(cancellationToken))
        {
            ledgers.Add((place, await transactions.ListAsync(place.Id, cancellationToken)));
        }

        // A repurchase is one of the fund at any holding place (O9 names the fund, not the place).
        var buys = ledgers.SelectMany(l => LiveBuys(l.Ledger)).ToList();
        var sales = new List<SaleInShekels>();
        var notComputed = new List<string>();
        var remarks = new List<string>();
        foreach (var (place, ledger) in ledgers)
        {
            // The books of today, not of 31 December: a commission or an adjustment dated later still belongs to
            // its sale. Corporate-action cash refuses up to the end of the year, as the books of 31 December would:
            // one of a later year does not make a filed year uncomputable.
            var asOf = ledger.Count == 0 ? today : LocalDate.Max(today, ledger.Max(e => e.TradeDate));
            var books = PortfolioBooks.From(ledger, asOf);
            var unsettled = books.NotInTheLots.Where(e => e.TradeDate.Year <= year).Select(e => e.Instrument).ToHashSet();
            foreach (var part in books.Disposals.Where(d => d.HoldingPlace == place.Id && d.Sold.Year == year))
            {
                var what = Invariant($"{place.Name}: the sale of {part.Sold:R} from the lot of {part.Acquired:R} ({part.Quantity.Value:0.############} shares)");
                if (unsettled.Contains(part.Instrument))
                {
                    notComputed.Add($"{what} - its instrument has corporate-action cash the lots do not hold; its cost is not known.");
                    continue;
                }

                // Before the rates: such a lot is refused for this, whatever rates there are.
                if (part.Acquired < RealGain.FirstPurchaseComputed)
                {
                    notComputed.Add(Invariant($"{what} - bought before {RealGain.FirstPurchaseComputed:R}: the transitional rules for it are not built."));
                    continue;
                }

                var bought = await rates.FindAsync(part.Acquired, cancellationToken);
                var sold = await rates.FindAsync(part.Sold, cancellationToken);
                if (bought.Rate is null || sold.Rate is null)
                {
                    notComputed.Add($"{what} - {string.Join(" ", new[] { bought.WhyNone, sold.WhyNone }.OfType<string>())}");
                    continue;
                }

                SaleInShekels computed;
                try
                {
                    computed = RealGain.Of(part, bought.Rate, sold.Rate);
                }
                catch (InvalidOperationException e)
                {
                    notComputed.Add($"{what} - {e.Message}");
                    continue;
                }

                sales.Add(computed);
                if (computed.OffsettableLoss > 0m)
                {
                    if (BoughtAround(buys, part))
                    {
                        remarks.Add(Invariant($"{what} - a loss, and the same fund was bought within {RepurchaseDays} days of the sale: whether it is offsettable in full is open (O9)."));
                    }
                    else if (Period.DaysBetween(part.Sold, today) <= RepurchaseDays)
                    {
                        remarks.Add(Invariant($"{what} - a loss, and the {RepurchaseDays} days after the sale are not over: a purchase of the same fund in them would make it open (O9)."));
                    }
                }

                if (part.Quantity.Value != decimal.Truncate(part.Quantity.Value))
                {
                    remarks.Add($"{what} - a fraction of a share: computed like whole shares; open (O10).");
                }
            }
        }

        var yearOver = today.Year > year;
        if (!yearOver)
        {
            remarks.Insert(0, Invariant($"{year} is not over ({today:R}): these are the sales recorded so far, not the year's."));
        }

        var assumptions = tax.Listed.Where(p => Rests.Contains(p.Name, StringComparer.Ordinal)).ToList();
        return new CapitalGainsOfAYear(year, yearOver, sales, notComputed, remarks, assumptions, tax.Origin);
    }

    /// <summary>The purchases that stand: a reversed one and its reversal are no purchase.</summary>
    private static IEnumerable<(TransactionId Id, InstrumentId? Instrument, LocalDate Day)> LiveBuys(IReadOnlyList<Transaction> ledger)
    {
        var reversed = ledger.Where(e => e.Reverses is not null).Select(e => e.Reverses!.Value).ToHashSet();
        return ledger.Where(e => e.Kind == TransactionKind.Buy && e.Reverses is null && !reversed.Contains(e.Id))
            .Select(e => (e.Id, e.Instrument, e.TradeDate));
    }

    private static bool BoughtAround(IEnumerable<(TransactionId Id, InstrumentId? Instrument, LocalDate Day)> buys, Disposal part) =>
        buys.Any(b => b.Instrument == part.Instrument && b.Id != part.Lot && Math.Abs(Period.DaysBetween(part.Sold, b.Day)) <= RepurchaseDays);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
