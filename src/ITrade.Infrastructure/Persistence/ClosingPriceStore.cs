using ITrade.Application.Prices;
using ITrade.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Infrastructure.Persistence;

/// <summary>
/// Table `closing_prices`: what an instrument closed at on a session, as it traded. The domain type is
/// <see cref="ClosingPrice"/>. Rows are only ever inserted — the database rejects UPDATE, DELETE and TRUNCATE
/// (migration ClosingPrices). A close the source corrected is a further row of the same session with the next
/// <see cref="Revision"/>; the latest revision is the price in use.
/// </summary>
public sealed class ClosingPriceRow
{
    public required InstrumentId InstrumentId { get; init; }

    /// <summary>The trading day on the exchange's calendar.</summary>
    public LocalDate Session { get; init; }

    /// <summary>0 for the close as first imported; one more each time the source stated another one.</summary>
    public int Revision { get; init; }

    public decimal Close { get; init; }

    public required Currency Currency { get; init; }

    public required string Source { get; init; }

    public Instant ImportedAt { get; init; }

    public ClosingPrice ToDomain() => new(InstrumentId, Session, new Price(Close, Currency), Source);
}

/// <summary>
/// Table `market_sessions`: the days an exchange calendar was open, as far as the exports of closing prices
/// have told. It is what tells a day the exchange was closed from a day whose price is missing. Rows are
/// only ever inserted (migration MarketSessions).
/// </summary>
public sealed class MarketSessionRow
{
    /// <summary>The calendar's code, as the exchange-calendar library names it: XNYS.</summary>
    public required string Calendar { get; init; }

    public LocalDate Session { get; init; }

    public Instant ImportedAt { get; init; }
}

/// <summary>
/// Table `price_imports`: one row for every import of closing prices that stored something — which export it
/// was. The latest <see cref="ExportedAt"/> is what a later import must not be older than. Rows are only
/// ever inserted (migration MarketSessions).
/// </summary>
public sealed class PriceImportRow
{
    public Guid Id { get; init; } = Ids.NewV7();

    public Instant ExportedAt { get; init; }

    /// <summary>SHA-256 of the export's table of prices, lower-case hex.</summary>
    public required string Sha256 { get; init; }

    public Instant ImportedAt { get; init; }
}

/// <summary>
/// Table `fund_events`: a dividend or a split of a fund on a day, as the data pipeline reports it. The domain
/// type is <see cref="FundEvent"/>. Rows are only ever inserted (migration FundEvents); an event the source
/// corrected is a further row of the same day and kind with the next <see cref="Revision"/>.
/// </summary>
public sealed class FundEventRow
{
    public required InstrumentId InstrumentId { get; init; }

    /// <summary>The ex-date of a dividend; the first day the shares trade split. A session of the exchange.</summary>
    public LocalDate Day { get; init; }

    public FundEventKind Kind { get; init; }

    public int Revision { get; init; }

    /// <summary>The dividend per share as paid, or the split as new shares per old one.</summary>
    public decimal Value { get; init; }

    public required string Source { get; init; }

    public Instant ImportedAt { get; init; }

    public FundEvent ToDomain() => new(InstrumentId, Day, Kind, Value, Source);
}

/// <summary>
/// `closing_prices`, `market_sessions`, `fund_events` and `price_imports` behind
/// <see cref="IClosingPriceStore"/>: reads the latest revisions, and inserts.
/// </summary>
public sealed class ClosingPriceStore(ITradeDbContext db) : IClosingPriceStore
{
    public async Task<IReadOnlyList<ClosingPrice>> ListAsync(InstrumentId instrument, CancellationToken cancellationToken)
    {
        var rows = await db.ClosingPrices.AsNoTracking()
            .Where(p => p.InstrumentId == instrument)
            .OrderBy(p => p.Session).ThenBy(p => p.Revision)
            .ToListAsync(cancellationToken);
        // The last revision of each session is the price in use.
        return rows.GroupBy(p => p.Session).Select(revisions => revisions.Last().ToDomain()).ToList();
    }

    public async Task<ClosingPrice?> FindAsync(InstrumentId instrument, LocalDate session, CancellationToken cancellationToken)
    {
        var row = await db.ClosingPrices.AsNoTracking()
            .Where(p => p.InstrumentId == instrument && p.Session == session)
            .OrderByDescending(p => p.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        return row?.ToDomain();
    }

    public async Task<ClosingPrice?> LatestBeforeAsync(InstrumentId instrument, LocalDate day, CancellationToken cancellationToken)
    {
        var row = await db.ClosingPrices.AsNoTracking()
            .Where(p => p.InstrumentId == instrument && p.Session < day)
            .OrderByDescending(p => p.Session).ThenByDescending(p => p.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyDictionary<InstrumentId, LocalDate>> LatestPerInstrumentAsync(CancellationToken cancellationToken)
    {
        var latest = await db.ClosingPrices.AsNoTracking()
            .GroupBy(p => p.InstrumentId)
            .Select(prices => new { Instrument = prices.Key, Session = prices.Max(p => p.Session) })
            .ToListAsync(cancellationToken);
        return latest.ToDictionary(l => l.Instrument, l => l.Session);
    }

    public async Task<IReadOnlyList<LocalDate>> ListSessionsAsync(string calendar, CancellationToken cancellationToken) =>
        await db.MarketSessions.AsNoTracking()
            .Where(s => s.Calendar == calendar)
            .OrderBy(s => s.Session)
            .Select(s => s.Session)
            .ToListAsync(cancellationToken);

    public async Task<LocalDate?> LatestSessionOnOrBeforeAsync(string calendar, LocalDate day, CancellationToken cancellationToken) =>
        await db.MarketSessions.AsNoTracking()
            .Where(s => s.Calendar == calendar && s.Session <= day)
            .MaxAsync(s => (LocalDate?)s.Session, cancellationToken);

    public async Task<ImportedExport?> LastImportAsync(CancellationToken cancellationToken)
    {
        var row = await db.PriceImports.AsNoTracking()
            .OrderByDescending(i => i.ExportedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new ImportedExport(row.ExportedAt, row.Sha256);
    }

    public async Task<IReadOnlyList<FundEvent>> ListEventsAsync(InstrumentId instrument, CancellationToken cancellationToken)
    {
        var rows = await db.FundEvents.AsNoTracking()
            .Where(e => e.InstrumentId == instrument)
            .OrderBy(e => e.Day).ThenBy(e => e.Kind).ThenBy(e => e.Revision)
            .ToListAsync(cancellationToken);
        return rows.GroupBy(e => (e.Day, e.Kind)).Select(revisions => revisions.Last().ToDomain()).ToList();
    }

    public async Task AddAsync(
        PricesToStore what, Instant importedAt, PricesNote note, ImportedExport importedExport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(note);
        var (added, revised, calendar, sessions) = (what.Added, what.Revised, what.Calendar, what.Sessions);
        ArgumentNullException.ThrowIfNull(importedExport);
        if (db.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                "Closing prices are written on their own: save or discard the other changes of this unit of work first.");
        }

        var rows = new List<ClosingPriceRow>(added.Count + revised.Count);
        rows.AddRange(added.Select(p => Row(p, revision: 0, importedAt)));
        foreach (var price in revised)
        {
            var latest = await db.ClosingPrices.AsNoTracking()
                .Where(p => p.InstrumentId == price.Instrument && p.Session == price.Session)
                .MaxAsync(p => (int?)p.Revision, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"There is no close of {price.Session:R} to revise for instrument {price.Instrument}.");
            rows.Add(Row(price, latest + 1, importedAt));
        }

        var events = what.Events.Select(e => Row(e, revision: 0, importedAt)).ToList();
        foreach (var fundEvent in what.RevisedEvents)
        {
            var latest = await db.FundEvents.AsNoTracking()
                .Where(e => e.InstrumentId == fundEvent.Instrument && e.Day == fundEvent.Day && e.Kind == fundEvent.Kind)
                .MaxAsync(e => (int?)e.Revision, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"There is no {fundEvent.Kind} of {fundEvent.Day:R} to revise for instrument {fundEvent.Instrument}.");
            events.Add(Row(fundEvent, latest + 1, importedAt));
        }

        // A first import is a couple of hundred thousand rows: no change detection over them.
        var detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            // One transaction: the sessions, the prices, the fund events, the mark of the export and the note
            // about it all - or nothing. The sessions go first: the database takes a close or an event only
            // for a day that is a session.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.MarketSessions.AddRange(
                sessions.Select(s => new MarketSessionRow { Calendar = calendar, Session = s, ImportedAt = importedAt }));
            await db.SaveChangesAsync(cancellationToken);
            db.ClosingPrices.AddRange(rows);
            db.FundEvents.AddRange(events);
            db.PriceImports.Add(
                new PriceImportRow { ExportedAt = importedExport.ExportedAt, Sha256 = importedExport.Sha256, ImportedAt = importedAt });
            db.AuditEvents.Add(
                new AuditEvent { OccurredAt = importedAt, Actor = note.Actor, Kind = note.Kind, Payload = note.Payload });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            // Nothing stays tracked: a refused row must not ride along with the next thing this context saves.
            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }
    }

    private static FundEventRow Row(FundEvent fundEvent, int revision, Instant importedAt) => new()
    {
        InstrumentId = fundEvent.Instrument,
        Day = fundEvent.Day,
        Kind = fundEvent.Kind,
        Revision = revision,
        Value = fundEvent.Value,
        Source = fundEvent.Source,
        ImportedAt = importedAt,
    };

    private static ClosingPriceRow Row(ClosingPrice price, int revision, Instant importedAt) => new()
    {
        InstrumentId = price.Instrument,
        Session = price.Session,
        Revision = revision,
        Close = price.Close.Value,
        Currency = price.Close.Currency,
        Source = price.Source,
        ImportedAt = importedAt,
    };
}
