using System.Globalization;
using NodaTime;

namespace ITrade.Domain;

/// <summary>Which shares a sale takes. Results name the method they were computed with.</summary>
public enum LotMatching
{
    /// <summary>
    /// First in, first out: a sale takes the shares bought earliest. An assumption — not confirmed by an
    /// accountant (the owner decided on 2026-10-12 to go by our own judgement).
    /// </summary>
    Fifo,
}

/// <summary>
/// The shares of one purchase that are still held. A sale takes shares from the earliest lots; a split changes
/// the number of shares and leaves the cost as it was. Amounts are positive: what was paid.
/// </summary>
/// <param name="OpenedBy">The purchase that opened the lot.</param>
/// <param name="Acquired">The trade date of the purchase.</param>
/// <param name="Quantity">Shares left in the lot, after splits and sales.</param>
/// <param name="Cost">What those shares cost, without the commission.</param>
/// <param name="Fees">The part of the purchase commission that belongs to those shares.</param>
public sealed record Lot(
    TransactionId OpenedBy,
    HoldingPlaceId HoldingPlace,
    InstrumentId Instrument,
    LocalDate Acquired,
    Quantity Quantity,
    Money Cost,
    Money Fees)
{
    /// <summary>The cost of one share, for showing. A quotient: it may not be exact, so sums use <see cref="Cost"/>.</summary>
    public decimal CostPerShare => Cost.Amount / Quantity.Value;
}

/// <summary>
/// The part of a sale that was taken from one lot: what tax and the realised result are computed from.
/// Amounts are positive. The parts of one sale add up exactly to the sale; the parts taken from one lot and
/// what is left in it add up exactly to the purchase.
/// </summary>
public sealed record Disposal(
    TransactionId Sale,
    TransactionId Lot,
    HoldingPlaceId HoldingPlace,
    InstrumentId Instrument,
    LocalDate Acquired,
    LocalDate Sold,
    Quantity Quantity,
    Money Cost,
    Money PurchaseFees,
    Money Proceeds,
    Money SaleFees)
{
    /// <summary>
    /// Proceeds less cost and both commissions, in the currency of the trade. Not the taxable amount: Israeli
    /// tax is computed in shekels by the tax module, under its own assumptions.
    /// </summary>
    public Money NominalGain => Proceeds - Cost - PurchaseFees - SaleFees;
}

/// <summary>What is held of one instrument at one place: its open lots, earliest first.</summary>
public sealed record Position(HoldingPlaceId HoldingPlace, InstrumentId Instrument, IReadOnlyList<Lot> Lots)
{
    public Quantity Quantity => new(Lots.Sum(l => l.Quantity.Value));

    public Money Cost => Lots.Select(l => l.Cost).Aggregate((a, b) => a + b);

    public Money Fees => Lots.Select(l => l.Fees).Aggregate((a, b) => a + b);
}

/// <summary>The cash in one currency at one place. Currencies are never added together.</summary>
public sealed record CashBalance(HoldingPlaceId HoldingPlace, Money Amount);

/// <summary>
/// The owner's books on a date, derived from the ledger and from nothing else: positions with their lots, cash
/// by currency, and how every sale so far was matched to purchases. Dates are trade dates — a purchase counts
/// from the day of the trade, not from its settlement.
/// </summary>
/// <param name="NotInTheLots">
/// Corporate actions that paid or took cash for an instrument (cash in lieu of a fraction, a return of capital).
/// Their cash is in <paramref name="Cash"/>, but what they mean for the lots is not worked out: the cost of the
/// lots and the realised result of that instrument are incomplete while this list names it. Whoever computes a
/// gain or a tax from <paramref name="Disposals"/> has to refuse such an instrument, not pass over it.
/// </param>
public sealed record PortfolioBooks(
    LocalDate AsOf,
    LotMatching Method,
    IReadOnlyList<Position> Positions,
    IReadOnlyList<CashBalance> Cash,
    IReadOnlyList<Disposal> Disposals,
    IReadOnlyList<Transaction> NotInTheLots)
{
    /// <summary>
    /// Decimal places of a part of a lot or of a sale (the ledger itself keeps <see cref="Transaction.Scale"/>).
    /// The last part always takes the remainder, so parts add up to the whole exactly.
    /// </summary>
    public const int PartScale = 12;

    /// <summary>Shares held, zero when there are none.</summary>
    public Quantity QuantityOf(HoldingPlaceId holdingPlace, InstrumentId instrument) =>
        Positions.SingleOrDefault(p => p.HoldingPlace == holdingPlace && p.Instrument == instrument)?.Quantity
        ?? Quantity.Zero;

    /// <summary>Cash in the currency, zero when the place never had any.</summary>
    public Money CashOf(HoldingPlaceId holdingPlace, Currency currency) =>
        Cash.SingleOrDefault(c => c.HoldingPlace == holdingPlace && c.Amount.Currency == currency)?.Amount
        ?? Money.Zero(currency);

    /// <summary>
    /// Computes the books as of a trade date. The entries are given in the ledger's order (trade date, then the
    /// order of recording): within a day that order decides which purchase came first.
    ///
    /// An entry that was reversed and its reversal take no part, on any date. A commission linked to a trade
    /// belongs to that trade's lot or sale. A corporate action that changes the shares without cash is a split
    /// or a reverse split: the open lots keep their cost and share the new number of shares in proportion. The
    /// entry carries the change of shares, not a ratio, so it has to come before the trades of its day. A
    /// corporate action that only moves cash is listed in <see cref="NotInTheLots"/>.
    ///
    /// What the ledger cannot mean is refused, not smoothed over: a sale of more than is held, a split of
    /// something not held or one that follows a trade of the same day, a corporate action that moves shares
    /// and cash at once (not supported yet).
    /// </summary>
    public static PortfolioBooks From(IEnumerable<Transaction> ledger, LocalDate asOf, LotMatching method = LotMatching.Fifo)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (method != LotMatching.Fifo)
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown way of matching lots.");
        }

        var all = ledger as IReadOnlyCollection<Transaction> ?? [.. ledger];
        var cancelled = all.Where(e => e.Reverses is not null).Select(e => e.Reverses!.Value).ToHashSet();
        var entries = new List<Transaction>();
        var lastDate = new Dictionary<HoldingPlaceId, LocalDate>();
        foreach (var entry in all)
        {
            if (lastDate.TryGetValue(entry.HoldingPlace, out var previous) && entry.TradeDate < previous)
            {
                throw new ArgumentException(
                    $"The entries are not in the ledger's order: {entry.TradeDate:R} comes after {previous:R}.", nameof(ledger));
            }

            lastDate[entry.HoldingPlace] = entry.TradeDate;
            if (entry.TradeDate <= asOf && !entry.IsReversal && !cancelled.Contains(entry.Id))
            {
                entries.Add(entry);
            }
        }

        var work = new Workbook(entries);
        foreach (var entry in entries)
        {
            work.Apply(entry);
        }

        return new PortfolioBooks(asOf, method, work.Positions(), work.Cash(), work.Disposals, work.NotInTheLots);
    }

    /// <summary>The books while they are being computed.</summary>
    private sealed class Workbook
    {
        private readonly Dictionary<(HoldingPlaceId, InstrumentId), List<OpenLot>> _lots = [];
        private readonly List<(HoldingPlaceId Place, InstrumentId Instrument)> _held = []; // in the order first bought
        private readonly Dictionary<(HoldingPlaceId, Currency), decimal> _cash = [];
        private readonly List<(HoldingPlaceId Place, Currency Currency)> _currencies = []; // in the order first seen
        private readonly Dictionary<TransactionId, decimal> _fees = [];

        public Workbook(List<Transaction> entries)
        {
            // Commissions are gathered first: a fee line may be recorded after other entries of the same day.
            var trades = entries
                .Where(e => e.Kind is TransactionKind.Buy or TransactionKind.Sell)
                .ToDictionary(e => e.Id);
            foreach (var fee in entries.Where(e => e.Kind == TransactionKind.Fee && e.RelatedTo is not null))
            {
                if (!trades.TryGetValue(fee.RelatedTo!.Value, out var trade))
                {
                    continue; // a fee of something else, or of a trade that was reversed: cash only
                }

                if (fee.Cash.Currency != trade.Cash.Currency)
                {
                    throw Refused(fee, $"the commission is in {fee.Cash.Currency} and its trade in {trade.Cash.Currency}");
                }

                _fees[trade.Id] = _fees.GetValueOrDefault(trade.Id) - fee.Cash.Amount;
            }
        }

        public List<Disposal> Disposals { get; } = [];

        public List<Transaction> NotInTheLots { get; } = [];

        public void Apply(Transaction entry)
        {
            if (entry.Cash.Amount != 0m)
            {
                var key = (entry.HoldingPlace, entry.Cash.Currency);
                if (!_cash.TryGetValue(key, out var balance))
                {
                    _currencies.Add(key);
                }

                _cash[key] = balance + entry.Cash.Amount;
            }

            switch (entry.Kind)
            {
                case TransactionKind.Buy:
                    Open(entry);
                    break;
                case TransactionKind.Sell:
                    Sell(entry);
                    break;
                case TransactionKind.CorporateAction when entry.QuantityChange != 0m:
                    if (entry.Cash.Amount != 0m)
                    {
                        throw Refused(entry, "a corporate action that moves shares and cash at once is not supported yet");
                    }

                    Split(entry);
                    break;
                case TransactionKind.CorporateAction:
                    // Cash in lieu of a fraction is really a sale of it, a return of capital lowers the cost:
                    // neither is worked out here, so it is said aloud instead of being passed off as complete.
                    NotInTheLots.Add(entry);
                    break;
                default:
                    break; // cash only
            }
        }

        public List<Position> Positions() =>
            _held
                .Select(key => (key, Lots: _lots[key]))
                .Where(x => x.Lots.Count > 0)
                .Select(x => new Position(
                    x.key.Place,
                    x.key.Instrument,
                    x.Lots.ConvertAll(l => new Lot(
                        l.OpenedBy, x.key.Place, x.key.Instrument, l.Acquired, new Quantity(l.Quantity),
                        new Money(l.Cost, l.Currency), new Money(l.Fees, l.Currency)))))
                .ToList();

        public List<CashBalance> Cash() =>
            _currencies.ConvertAll(key => new CashBalance(key.Place, new Money(_cash[key], key.Currency)));

        private void Open(Transaction buy)
        {
            var lots = LotsOf(buy);
            if (lots.Count > 0 && lots[0].Currency != buy.Cash.Currency)
            {
                throw Refused(buy, $"the instrument is held in {lots[0].Currency} and this purchase is in {buy.Cash.Currency}");
            }

            lots.Add(new OpenLot
            {
                OpenedBy = buy.Id,
                Acquired = buy.TradeDate,
                Currency = buy.Cash.Currency,
                Quantity = buy.QuantityChange,
                Cost = -buy.Cash.Amount,
                Fees = _fees.GetValueOrDefault(buy.Id),
            });
        }

        private void Sell(Transaction sale)
        {
            var lots = LotsOf(sale);
            var toSell = -sale.QuantityChange;
            var held = lots.Sum(l => l.Quantity);
            if (toSell > held)
            {
                throw Refused(sale, Invariant($"it sells {toSell} shares and {held} are held"));
            }

            if (lots[0].Currency != sale.Cash.Currency)
            {
                throw Refused(sale, $"the instrument is held in {lots[0].Currency} and this sale is in {sale.Cash.Currency}");
            }

            var currency = sale.Cash.Currency;
            var saleFees = _fees.GetValueOrDefault(sale.Id);
            var (left, proceedsLeft, feesLeft) = (toSell, sale.Cash.Amount, saleFees);
            while (left > 0m)
            {
                var lot = lots[0]; // first in, first out
                var take = Math.Min(lot.Quantity, left);
                var wholeLot = take == lot.Quantity;
                var lastPart = take == left;
                // A part is a share of the whole; the last part is what remains, so the parts add up exactly.
                var cost = wholeLot ? lot.Cost : Part(lot.Cost, take, lot.Quantity);
                var purchaseFees = wholeLot ? lot.Fees : Part(lot.Fees, take, lot.Quantity);
                var proceeds = lastPart ? proceedsLeft : Part(sale.Cash.Amount, take, toSell);
                var fees = lastPart ? feesLeft : Part(saleFees, take, toSell);
                Disposals.Add(new Disposal(
                    sale.Id, lot.OpenedBy, sale.HoldingPlace, sale.Instrument!.Value, lot.Acquired, sale.TradeDate,
                    new Quantity(take), new Money(cost, currency), new Money(purchaseFees, currency),
                    new Money(proceeds, currency), new Money(fees, currency)));

                (lot.Quantity, lot.Cost, lot.Fees) = (lot.Quantity - take, lot.Cost - cost, lot.Fees - purchaseFees);
                (left, proceedsLeft, feesLeft) = (left - take, proceedsLeft - proceeds, feesLeft - fees);
                if (wholeLot)
                {
                    lots.RemoveAt(0);
                }
            }
        }

        private void Split(Transaction action)
        {
            var lots = LotsOf(action);
            var held = lots.Sum(l => l.Quantity);
            if (held == 0m)
            {
                throw Refused(action, "it changes the shares of an instrument that is not held");
            }

            // The entry says how many shares were added or taken, not in which ratio; the ratio comes from what
            // is held at this point. A trade of the same day listed before it would be counted on the wrong side
            // of the split - every lot would get a wrong number of shares while the totals still looked right.
            if (lots.Exists(l => l.Acquired == action.TradeDate)
                || Disposals.Exists(d => d.Sold == action.TradeDate && d.HoldingPlace == action.HoldingPlace && d.Instrument == action.Instrument))
            {
                throw Refused(action, "a split has to come before the trades of its day, and a trade of that day is listed first");
            }

            var total = held + action.QuantityChange;
            if (total <= 0m)
            {
                throw Refused(action, Invariant($"it takes {-action.QuantityChange} shares away and {held} are held"));
            }

            var given = 0m;
            for (var i = 0; i < lots.Count; i++)
            {
                // The last lot takes what remains, so the lots add up exactly to the new number of shares.
                lots[i].Quantity = i == lots.Count - 1 ? total - given : Part(total, lots[i].Quantity, held);
                given += lots[i].Quantity;
            }
        }

        private List<OpenLot> LotsOf(Transaction entry)
        {
            var key = (entry.HoldingPlace, entry.Instrument!.Value);
            if (!_lots.TryGetValue(key, out var lots))
            {
                _lots[key] = lots = [];
                _held.Add(key);
            }

            return lots;
        }

        /// <summary>
        /// <paramref name="whole"/> × <paramref name="share"/> / <paramref name="of"/>, kept to
        /// <see cref="PartScale"/> decimal places. A division has to stop somewhere; stopping at a fixed place,
        /// far finer than anything the ledger holds, keeps every later sum and difference exact.
        /// </summary>
        private static decimal Part(decimal whole, decimal share, decimal of) =>
            Math.Round(whole * share / of, PartScale, MidpointRounding.ToEven);

        private static InvalidOperationException Refused(Transaction entry, string why) =>
            new($"The books cannot be computed: {entry.Kind} {entry.Id} on {entry.TradeDate:R} - {why}.");

        private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class OpenLot
    {
        public required TransactionId OpenedBy { get; init; }

        public required LocalDate Acquired { get; init; }

        public required Currency Currency { get; init; }

        public decimal Quantity { get; set; }

        public decimal Cost { get; set; }

        public decimal Fees { get; set; }
    }
}
