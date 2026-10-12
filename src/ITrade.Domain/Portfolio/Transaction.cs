using NodaTime;

namespace ITrade.Domain;

/// <summary>What happened. Each kind fixes which way the shares and the cash may move (see <see cref="Transaction"/>).</summary>
public enum TransactionKind
{
    Buy,
    Sell,
    Dividend,

    /// <summary>Tax withheld at the source, e.g. the US tax on a dividend.</summary>
    TaxWithheld,
    Fee,

    /// <summary>Interest the broker paid or charged on a cash balance.</summary>
    Interest,

    /// <summary>One leg of a currency conversion; the other leg is a second transaction linked to it.</summary>
    FxConversion,
    Deposit,
    Withdrawal,

    /// <summary>A split, merger, spin-off or transfer: shares and/or cash change without a trade.</summary>
    CorporateAction,
}

/// <summary>Where the record came from; with the external id it identifies a transaction for a repeated import.</summary>
public enum TransactionSource
{
    /// <summary>Typed in by the owner or read from a statement file of a place without integration.</summary>
    Manual,

    /// <summary>An IBKR Flex statement: the broker's own books, the source of truth for money.</summary>
    Flex,

    /// <summary>An execution reported by the IBKR API on the day; confirmed later by the statement.</summary>
    ApiExecution,
}

/// <summary>
/// One entry of the accounting ledger: a change of the number of shares of one instrument and/or of the cash in
/// one currency, at one holding place. Signs are effects: a buy adds shares and takes cash, a sale does the
/// opposite, so balances on any date are plain sums. A purchase with its commission is two entries (the fee
/// linked to the buy by <see cref="RelatedTo"/>); a currency conversion is two linked legs.
///
/// An entry is never changed or deleted. A mistake is corrected by a reversal — an entry with the opposite
/// effects that names the one it cancels in <see cref="Reverses"/> — followed by the right entry.
/// </summary>
public sealed record Transaction
{
    /// <summary>Digits after the decimal point the ledger keeps. A value with more is refused, never rounded silently.</summary>
    public const int Scale = 6;

    public const int MaxExternalIdLength = 100;
    public const int MaxNoteLength = 500;

    public Transaction(
        TransactionId id,
        HoldingPlaceId holdingPlace,
        TransactionKind kind,
        LocalDate tradeDate,
        LocalDate settlementDate,
        Money cash,
        TransactionSource source,
        Instant recordedAt,
        InstrumentId? instrument = null,
        decimal quantityChange = 0m,
        Price? price = null,
        string? externalId = null,
        TransactionId? relatedTo = null,
        TransactionId? reverses = null,
        string? note = null)
    {
        if (id.Value == Guid.Empty || holdingPlace.Value == Guid.Empty)
        {
            throw new ArgumentException("A transaction needs an id and a holding place.", nameof(id));
        }

        if (cash.Currency.Code is null)
        {
            throw new ArgumentException("A transaction needs the currency of its cash.", nameof(cash));
        }

        if (!Enum.IsDefined(kind) || !Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind or source of a transaction.");
        }

        if (settlementDate < tradeDate)
        {
            throw new ArgumentException(
                $"Settlement on {settlementDate:R} is before the trade date {tradeDate:R}.", nameof(settlementDate));
        }

        if (!Fits(cash.Amount) || !Fits(quantityChange) || (price is { } given && !Fits(given.Value)))
        {
            throw new ArgumentException(
                $"The ledger keeps {Scale} decimal places; a value with more would be rounded silently.", nameof(cash));
        }

        if (reverses == id || relatedTo == id)
        {
            throw new ArgumentException("A transaction cannot reverse or relate to itself.", nameof(reverses));
        }

        var externalKey = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();
        var remark = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (externalKey is { Length: > MaxExternalIdLength } || remark is { Length: > MaxNoteLength })
        {
            throw new ArgumentException("The external id or the note is too long.", nameof(externalId));
        }

        // A reversal carries the opposite effects of the entry it cancels; the rules apply to that entry.
        var direction = reverses is null ? 1 : -1;
        if (Problem(kind, instrument, quantityChange * direction, cash.Amount * direction, price, cash.Currency) is { } problem)
        {
            throw new ArgumentException($"{kind}: {problem}", nameof(kind));
        }

        Id = id;
        HoldingPlace = holdingPlace;
        Kind = kind;
        TradeDate = tradeDate;
        SettlementDate = settlementDate;
        Cash = cash;
        Source = source;
        RecordedAt = recordedAt;
        Instrument = instrument;
        QuantityChange = quantityChange;
        Price = price;
        ExternalId = externalKey;
        RelatedTo = relatedTo;
        Reverses = reverses;
        Note = remark;
    }

    public TransactionId Id { get; }

    public HoldingPlaceId HoldingPlace { get; }

    public TransactionKind Kind { get; }

    /// <summary>The date of the trade or event on the calendar of the place (New York for IBKR).</summary>
    public LocalDate TradeDate { get; }

    /// <summary>The date the cash and the shares actually move; equal to the trade date for cash-only events.</summary>
    public LocalDate SettlementDate { get; }

    /// <summary>The change of cash: positive when money comes in, negative when it goes out.</summary>
    public Money Cash { get; }

    public TransactionSource Source { get; }

    /// <summary>When the platform wrote the entry down (not when the event happened).</summary>
    public Instant RecordedAt { get; }

    public InstrumentId? Instrument { get; }

    /// <summary>The change of the number of shares: positive for a buy, negative for a sale, zero for cash-only events.</summary>
    public decimal QuantityChange { get; }

    /// <summary>The price per share of a buy or a sale, in the currency of <see cref="Cash"/>.</summary>
    public Price? Price { get; }

    /// <summary>The source's own identifier of the entry (a trade or transaction id of the statement).</summary>
    public string? ExternalId { get; }

    /// <summary>The entry this one belongs to: the trade of a fee, the dividend of a withheld tax, the other leg of a conversion.</summary>
    public TransactionId? RelatedTo { get; }

    /// <summary>The entry this one cancels.</summary>
    public TransactionId? Reverses { get; }

    public string? Note { get; }

    public bool IsReversal => Reverses is not null;

    /// <summary>The entry that cancels this one: the same facts with the opposite effects.</summary>
    public Transaction Reversal(
        TransactionId id, Instant recordedAt, string reason, TransactionSource source = TransactionSource.Manual,
        string? externalId = null)
    {
        if (IsReversal)
        {
            throw new InvalidOperationException("A reversal is not reversed: record the right entry instead.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new Transaction(
            id, HoldingPlace, Kind, TradeDate, SettlementDate, new Money(-Cash.Amount, Cash.Currency), source, recordedAt,
            Instrument, -QuantityChange, Price, externalId, relatedTo: null, reverses: Id, note: reason);
    }

    /// <summary>
    /// True when the other entry states the same facts — what a repeated import compares. The id, the time of
    /// recording, the note and the links are the platform's own and are left out.
    /// </summary>
    public bool SameFactsAs(Transaction other) =>
        other is not null
        && HoldingPlace == other.HoldingPlace
        && Kind == other.Kind
        && TradeDate == other.TradeDate
        && SettlementDate == other.SettlementDate
        && Cash == other.Cash
        && Source == other.Source
        && Instrument == other.Instrument
        && QuantityChange == other.QuantityChange
        && Price == other.Price
        && ExternalId == other.ExternalId
        && IsReversal == other.IsReversal;

    private static bool Fits(decimal value) => decimal.Round(value, Scale) == value;

    /// <summary>Why these effects cannot be an entry of the kind, or null when they can.</summary>
    private static string? Problem(
        TransactionKind kind, InstrumentId? instrument, decimal quantity, decimal cash, Price? price, Currency currency)
    {
        var trade = kind is TransactionKind.Buy or TransactionKind.Sell;
        if (trade != price is not null)
        {
            return trade ? "a trade needs its price" : "only a buy or a sale has a price";
        }

        if (price is { } stated && stated.Currency != currency)
        {
            return $"the price is in {stated.Currency} and the cash in {currency}";
        }

        var needsInstrument = trade || kind is TransactionKind.Dividend or TransactionKind.CorporateAction;
        var mayHaveInstrument = needsInstrument || kind is TransactionKind.TaxWithheld or TransactionKind.Fee;
        if (needsInstrument && instrument is null)
        {
            return "the instrument is missing";
        }

        if (!mayHaveInstrument && instrument is not null)
        {
            return "it concerns cash only and cannot name an instrument";
        }

        return kind switch
        {
            TransactionKind.Buy => quantity > 0m && cash < 0m ? null : "shares come in and cash goes out",
            TransactionKind.Sell => quantity < 0m && cash > 0m ? null : "shares go out and cash comes in",
            TransactionKind.Dividend => quantity == 0m && cash > 0m ? null : "cash comes in and the shares do not change",
            TransactionKind.Deposit => quantity == 0m && cash > 0m ? null : "cash comes in",
            TransactionKind.TaxWithheld or TransactionKind.Fee or TransactionKind.Withdrawal =>
                quantity == 0m && cash < 0m ? null : "cash goes out and the shares do not change",
            TransactionKind.Interest or TransactionKind.FxConversion =>
                quantity == 0m && cash != 0m ? null : "cash moves and the shares do not change",
            TransactionKind.CorporateAction => quantity != 0m || cash != 0m ? null : "something has to change",
            _ => "unknown kind",
        };
    }
}
