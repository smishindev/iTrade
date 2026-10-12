using System.Globalization;
using NodaTime;

namespace ITrade.Domain;

/// <summary>Whether a position has a value on a date, and if not, why.</summary>
public enum PositionValueState
{
    /// <summary>Shares × the closing price that goes with the date.</summary>
    Valued,

    /// <summary>The session the date goes by has no close for the instrument: a gap in the data.</summary>
    PriceMissing,

    /// <summary>There is no price to go by: the date is after today, before the instrument's first price, or the prices are too old.</summary>
    PriceUnavailable,

    /// <summary>
    /// The latest close is of a session before a split that the books have applied: the shares are the new
    /// ones, the price is of the old ones. Their product would be double, or half, the truth.
    /// </summary>
    PriceFromBeforeASplit,
}

/// <summary>
/// A position on a date with the price found for it. The value is in the currency of the price and exists
/// only when the price can be used — a position is never valued at an older price, and never at zero.
/// </summary>
/// <param name="SplitOn">For <see cref="PositionValueState.PriceFromBeforeASplit"/>: the day of that split.</param>
public sealed record PositionValue(Position Position, PriceOnDate Price, PositionValueState State, LocalDate? SplitOn = null)
{
    /// <summary>Shares × close, exact; null unless <see cref="State"/> is <see cref="PositionValueState.Valued"/>.</summary>
    public Money? Value => State == PositionValueState.Valued ? Price.Price!.Close.Times(Position.Quantity) : null;
}

/// <summary>
/// What a portfolio, or one holding place of it, comes to in one currency. <paramref name="Valued"/> is the sum
/// of everything that could be valued and converted — it is the total only when nothing is left out
/// (<see cref="Total"/>). Nothing is rounded: round where the figure is shown.
/// </summary>
/// <param name="Currency">The currency the amounts are in.</param>
/// <param name="Valued">Positions that have a value, and cash — what was in another currency converted with <paramref name="Rate"/>.</param>
/// <param name="NotValued">The positions without a usable price. Their value is not in <paramref name="Valued"/>.</param>
/// <param name="NotConverted">Sums in another currency that could not be converted, because there is no rate. Not in <paramref name="Valued"/>.</param>
/// <param name="Rate">The exchange rate, when an amount was converted with it. It says whether it was carried forward.</param>
/// <param name="IsProvisional">A provisional price or a provisional rate is in the figure: it is expected to change.</param>
public sealed record PortfolioTotal(
    Currency Currency,
    Money Valued,
    IReadOnlyList<PositionValue> NotValued,
    IReadOnlyList<Money> NotConverted,
    AppliedFxRate? Rate,
    bool IsProvisional)
{
    public bool IsComplete => NotValued.Count == 0 && NotConverted.Count == 0;

    /// <summary>The value of all of it — or null when a part could not be valued or converted.</summary>
    public Money? Total => IsComplete ? Valued : null;
}

/// <summary>
/// The value of the owner's portfolio on a date, by the ledger: every position with the closing price that
/// goes with the date, the cash in each currency, and the exchange rate of the date. For looking at — never a
/// tax figure. Each position keeps its <see cref="PriceOnDate"/> and the whole keeps its
/// <see cref="AppliedFxRate"/>, so that an amount can always say how good its price and its rate were.
/// </summary>
/// <param name="AsOf">The trade date (New York) the books and the prices are of; the rate is that date's too.</param>
/// <param name="Positions">Every position of the books, valued or not.</param>
/// <param name="Cash">Cash by holding place and currency, as in the books.</param>
/// <param name="Rate">The exchange rate on the date; null when there is none to use.</param>
/// <param name="WhyNoRate">In words, why there is no rate.</param>
public sealed record PortfolioValue(
    LocalDate AsOf,
    IReadOnlyList<PositionValue> Positions,
    IReadOnlyList<CashBalance> Cash,
    AppliedFxRate? Rate,
    string? WhyNoRate)
{
    /// <summary>The holding places that hold something, in the order of the books.</summary>
    public IReadOnlyList<HoldingPlaceId> Places =>
        Positions.Select(p => p.Position.HoldingPlace).Concat(Cash.Select(c => c.HoldingPlace)).Distinct().ToList();

    /// <summary>
    /// Values the books with the prices found for their date. <paramref name="prices"/> holds the answer of the
    /// price service for every instrument held, asked for the date of the books; <paramref name="rate"/> is
    /// the rate on that date, or null with the reason.
    ///
    /// A position whose price is missing or unavailable is not valued. Neither is one whose price was carried
    /// from a session before a split the books have applied since. A price in another currency than the
    /// position was bought in is refused: one of the two is wrong.
    /// </summary>
    public static PortfolioValue Of(
        PortfolioBooks books, IReadOnlyDictionary<InstrumentId, PriceOnDate> prices, AppliedFxRate? rate, string? whyNoRate)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(prices);
        if (rate is not null && rate.On != books.AsOf)
        {
            throw new ArgumentException($"The rate is for {rate.On:R} and the books are of {books.AsOf:R}.", nameof(rate));
        }

        if ((rate is null) == string.IsNullOrWhiteSpace(whyNoRate))
        {
            throw new ArgumentException("Either a rate, or the reason there is none.", nameof(whyNoRate));
        }

        var positions = new List<PositionValue>(books.Positions.Count);
        foreach (var position in books.Positions)
        {
            if (!prices.TryGetValue(position.Instrument, out var price) || price.Instrument != position.Instrument || price.Date != books.AsOf)
            {
                throw new ArgumentException(
                    $"No price was asked for instrument {position.Instrument} on {books.AsOf:R}.", nameof(prices));
            }

            var split = SplitAfterThePrice(position, price, books);
            positions.Add(new PositionValue(position, price, split is null ? StateOf(price) : PositionValueState.PriceFromBeforeASplit, split));
        }

        return new PortfolioValue(books.AsOf, positions, books.Cash, rate, rate is null ? whyNoRate : null);
    }

    /// <summary>The whole portfolio in the currency.</summary>
    public PortfolioTotal In(Currency currency) => TotalOf(currency, Positions, Cash);

    /// <summary>One holding place in the currency.</summary>
    public PortfolioTotal In(Currency currency, HoldingPlaceId holdingPlace) => TotalOf(
        currency,
        Positions.Where(p => p.Position.HoldingPlace == holdingPlace).ToList(),
        Cash.Where(c => c.HoldingPlace == holdingPlace).ToList());

    /// <summary>
    /// What has to be said in words next to the figures: a position that is not valued and why, a provisional
    /// price, a provisional rate, no rate. Empty when every figure is as good as it gets.
    /// </summary>
    /// <param name="nameOf">The name to call an instrument by — its ticker.</param>
    public IReadOnlyList<string> Remarks(Func<InstrumentId, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        var remarks = new List<string>();
        foreach (var position in Positions)
        {
            var name = nameOf(position.Position.Instrument);
            var price = position.Price;
            switch (position.State)
            {
                case PositionValueState.PriceMissing:
                    remarks.Add(Invariant($"{name}: not valued - there is no closing price for the session {AsOf:R} goes by."));
                    break;
                case PositionValueState.PriceUnavailable:
                    remarks.Add(Invariant($"{name}: not valued - no closing price can be used on {AsOf:R}."));
                    break;
                case PositionValueState.PriceFromBeforeASplit:
                    remarks.Add(Invariant(
                        $"{name}: not valued - the latest close is of {price.Price!.Session:R}, before the split of {position.SplitOn:R}."));
                    break;
                case PositionValueState.Valued when price.IsProvisional:
                    remarks.Add(Invariant(
                        $"{name}: provisional - valued at the close of {price.Price!.Session:R}, {price.DaysCarried} day(s) before; the exchange may have been open since."));
                    break;
                default:
                    break;
            }
        }

        if (Rate is null)
        {
            remarks.Add($"No exchange rate, so nothing is converted: {WhyNoRate}");
        }
        else if (Rate.IsProvisional)
        {
            remarks.Add(Invariant(
                $"The exchange rate is provisional: {Rate.Rate.Rate:0.######} of {Rate.Rate.Date:R} ({Rate.Rate.Source}), {Rate.DaysCarried} day(s) before; a later one may still be published."));
        }

        // An instrument held at two places is two positions and one remark.
        return remarks.Distinct().ToList();
    }

    private static PositionValueState StateOf(PriceOnDate price) => price switch
    {
        { HasPrice: true } => PositionValueState.Valued,
        { Availability: PriceAvailability.MissingForTheSession } => PositionValueState.PriceMissing,
        _ => PositionValueState.PriceUnavailable,
    };

    /// <summary>
    /// The day of the earliest split the books applied after the session the price is of, if there is one. The
    /// books of the date hold the shares after every split up to it; a close of a session before one of those
    /// splits is the price of the shares before it. (The close of the split's own day is already the new one.)
    /// </summary>
    private static LocalDate? SplitAfterThePrice(Position position, PriceOnDate price, PortfolioBooks books)
    {
        if (price.Price is not { } close)
        {
            return null;
        }

        if (close.Close.Currency != position.Cost.Currency)
        {
            throw new InvalidOperationException(
                $"The price of instrument {position.Instrument} is in {close.Close.Currency} and it was bought in {position.Cost.Currency}.");
        }

        // The earliest one, at whichever holding place it was recorded: a split is the fund's, not the place's.
        return books.Splits
            .Where(s => s.Instrument == position.Instrument && s.TradeDate > close.Session)
            .Min(s => (LocalDate?)s.TradeDate);
    }

    private PortfolioTotal TotalOf(Currency currency, IReadOnlyList<PositionValue> positions, IReadOnlyList<CashBalance> cash)
    {
        // Amounts are added in their own currency first and converted once: nothing is lost on the way.
        var sums = new Dictionary<Currency, decimal>();
        foreach (var amount in positions.Where(p => p.Value is not null).Select(p => p.Value!.Value).Concat(cash.Select(c => c.Amount)))
        {
            sums[amount.Currency] = sums.GetValueOrDefault(amount.Currency) + amount.Amount;
        }

        var valued = sums.GetValueOrDefault(currency);
        var notConverted = new List<Money>();
        var converted = false;
        foreach (var (other, sum) in sums.Where(s => s.Key != currency && s.Value != 0m))
        {
            if (Rate is not null && Converts(Rate.Rate, other, currency))
            {
                valued += Rate.Rate.Convert(new Money(sum, other)).Amount;
                converted = true;
            }
            else
            {
                notConverted.Add(new Money(sum, other));
            }
        }

        return new PortfolioTotal(
            currency,
            new Money(valued, currency),
            positions.Where(p => p.State != PositionValueState.Valued).ToList(),
            notConverted,
            converted ? Rate : null,
            positions.Any(p => p.State == PositionValueState.Valued && p.Price.IsProvisional) || (converted && Rate!.IsProvisional));
    }

    private static bool Converts(FxRate rate, Currency one, Currency other) =>
        (rate.From == one && rate.To == other) || (rate.From == other && rate.To == one);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
