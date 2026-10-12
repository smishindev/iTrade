using ITrade.Domain;
using NodaTime;

namespace ITrade.Infrastructure.Persistence;

/// <summary>Table `instruments`: what can be held. The domain type is <see cref="Instrument"/>.</summary>
public sealed class InstrumentRow
{
    public required InstrumentId Id { get; init; }

    /// <summary>The current ticker; earlier ones are in <see cref="FormerTickerRow"/>.</summary>
    public required Ticker Ticker { get; set; }

    public required string Exchange { get; set; }

    public required Currency Currency { get; set; }

    public required string Name { get; set; }

    public Isin? Isin { get; set; }

    public int? IbkrConId { get; set; }

    public Instrument ToDomain() => new(Id, Ticker, Exchange, Currency, Name, Isin, IbkrConId);

    public static InstrumentRow From(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return new InstrumentRow
        {
            Id = instrument.Id,
            Ticker = instrument.Ticker,
            Exchange = instrument.Exchange,
            Currency = instrument.Currency,
            Name = instrument.Name,
            Isin = instrument.Isin,
            IbkrConId = instrument.IbkrConId,
        };
    }
}

/// <summary>
/// Table `instrument_former_tickers`: a ticker the instrument traded under until (not including) a date.
/// Only renames are stored; the key makes two renames of one instrument on one date impossible.
/// </summary>
public sealed class FormerTickerRow
{
    public required InstrumentId InstrumentId { get; init; }

    public required Ticker Ticker { get; init; }

    public required LocalDate Until { get; init; }
}

/// <summary>
/// Table `transactions`: the accounting ledger. The domain type is <see cref="Transaction"/>. Rows are only
/// ever inserted — the database rejects UPDATE, DELETE and TRUNCATE (migration Transactions).
/// </summary>
public sealed class TransactionRow
{
    public required TransactionId Id { get; init; }

    public required HoldingPlaceId HoldingPlaceId { get; init; }

    public TransactionKind Kind { get; init; }

    public LocalDate TradeDate { get; init; }

    public LocalDate SettlementDate { get; init; }

    public InstrumentId? InstrumentId { get; init; }

    public decimal QuantityChange { get; init; }

    public decimal? Price { get; init; }

    public decimal CashAmount { get; init; }

    public required Currency CashCurrency { get; init; }

    public TransactionSource Source { get; init; }

    public string? ExternalId { get; init; }

    public TransactionId? RelatedTo { get; init; }

    public TransactionId? Reverses { get; init; }

    public Instant RecordedAt { get; init; }

    public string? Note { get; init; }

    public Transaction ToDomain() => new(
        Id, HoldingPlaceId, Kind, TradeDate, SettlementDate, new Money(CashAmount, CashCurrency), Source, RecordedAt,
        InstrumentId, QuantityChange, Price is { } price ? new Price(price, CashCurrency) : null, ExternalId, RelatedTo,
        Reverses, Note);

    public static TransactionRow From(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new TransactionRow
        {
            Id = transaction.Id,
            HoldingPlaceId = transaction.HoldingPlace,
            Kind = transaction.Kind,
            TradeDate = transaction.TradeDate,
            SettlementDate = transaction.SettlementDate,
            InstrumentId = transaction.Instrument,
            QuantityChange = transaction.QuantityChange,
            Price = transaction.Price?.Value,
            CashAmount = transaction.Cash.Amount,
            CashCurrency = transaction.Cash.Currency,
            Source = transaction.Source,
            ExternalId = transaction.ExternalId,
            RelatedTo = transaction.RelatedTo,
            Reverses = transaction.Reverses,
            RecordedAt = transaction.RecordedAt,
            Note = transaction.Note,
        };
    }
}

/// <summary>Table `holding_places`: where money is held. The domain type is <see cref="HoldingPlace"/>.</summary>
public sealed class HoldingPlaceRow
{
    public required HoldingPlaceId Id { get; init; }

    public required string Name { get; set; }

    public HoldingPlaceKind Kind { get; set; }

    public IntegrationKind Integration { get; set; }

    public HoldingPlace ToDomain() => new(Id, Name, Kind, Integration);

    public static HoldingPlaceRow From(HoldingPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return new HoldingPlaceRow { Id = place.Id, Name = place.Name, Kind = place.Kind, Integration = place.Integration };
    }
}
