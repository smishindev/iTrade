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
/// only ever inserted (migration MarketSessions). A day the owner found the exchange closed on after all is
/// withdrawn by a further row of the next <see cref="Revision"/> with <see cref="Withdrawn"/> and the reason
/// (migration Withdrawals); the latest revision says whether the day is a session.
/// </summary>
public sealed class MarketSessionRow
{
    /// <summary>The calendar's code, as the exchange-calendar library names it: XNYS.</summary>
    public required string Calendar { get; init; }

    public LocalDate Session { get; init; }

    /// <summary>0 for the session as first imported; one more for each later row of the same day.</summary>
    public int Revision { get; init; }

    /// <summary>The owner withdrew the day: it is no session.</summary>
    public bool Withdrawn { get; init; }

    /// <summary>Why the owner withdrew it, in the owner's words; only on a withdrawal.</summary>
    public string? Reason { get; init; }

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
/// corrected is a further row of the same day and kind with the next <see cref="Revision"/>, and one the owner
/// withdrew a further row with <see cref="Withdrawn"/> and the reason (migration Withdrawals).
/// </summary>
public sealed class FundEventRow
{
    public required InstrumentId InstrumentId { get; init; }

    /// <summary>The ex-date of a dividend; the first day the shares trade split. A session of the exchange.</summary>
    public LocalDate Day { get; init; }

    public FundEventKind Kind { get; init; }

    public int Revision { get; init; }

    /// <summary>The dividend per share as paid, or the split as new shares per old one; a withdrawal keeps the value it withdraws.</summary>
    public decimal Value { get; init; }

    /// <summary>The owner withdrew the event: the fund did not do it.</summary>
    public bool Withdrawn { get; init; }

    /// <summary>Why the owner withdrew it, in the owner's words; only on a withdrawal.</summary>
    public string? Reason { get; init; }

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
        await Latest(calendar).Where(s => !s.Withdrawn)
            .OrderBy(s => s.Session)
            .Select(s => s.Session)
            .ToListAsync(cancellationToken);

    // Asked for every price of a valuation: walked backwards along the key and stopped at the first live day,
    // not aggregated over every session (MAX cannot take the key's order through the test for a later row).
    public async Task<LocalDate?> LatestSessionOnOrBeforeAsync(string calendar, LocalDate day, CancellationToken cancellationToken) =>
        await Latest(calendar).Where(s => !s.Withdrawn && s.Session <= day)
            .OrderByDescending(s => s.Session)
            .Select(s => (LocalDate?)s.Session)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<FundEvent>> ListEventsOnAsync(LocalDate day, CancellationToken cancellationToken) =>
        (await LatestEvents().Where(e => e.Day == day && !e.Withdrawn).ToListAsync(cancellationToken)).ConvertAll(e => e.ToDomain());

    public async Task<IReadOnlyList<WithdrawnSession>> ListWithdrawnSessionsAsync(string calendar, CancellationToken cancellationToken) =>
        (await Latest(calendar).Where(s => s.Withdrawn).OrderBy(s => s.Session).ToListAsync(cancellationToken))
            .ConvertAll(s => new WithdrawnSession(s.Session, s.Reason!));

    public async Task<IReadOnlyList<WithdrawnEvent>> ListWithdrawnEventsAsync(InstrumentId instrument, CancellationToken cancellationToken) =>
        (await LatestEvents().Where(e => e.InstrumentId == instrument && e.Withdrawn)
            .OrderBy(e => e.Day).ThenBy(e => e.Kind)
            .ToListAsync(cancellationToken))
            .ConvertAll(e => new WithdrawnEvent(e.ToDomain(), e.Reason!));

    public Task<bool> AnyCloseOnAsync(LocalDate session, CancellationToken cancellationToken) =>
        db.ClosingPrices.AsNoTracking().AnyAsync(p => p.Session == session, cancellationToken);

    public async Task<(int Sessions, int Events)> CountWithdrawnAsync(CancellationToken cancellationToken)
    {
        var sessions = await db.MarketSessions.AsNoTracking()
            .CountAsync(s => s.Withdrawn && !db.MarketSessions.Any(m => m.Calendar == s.Calendar && m.Session == s.Session && m.Revision > s.Revision), cancellationToken);
        var events = await LatestEvents().CountAsync(e => e.Withdrawn, cancellationToken);
        return (sessions, events);
    }

    /// <summary>The latest row of every day of the calendar: whether it is a session, or was withdrawn.</summary>
    private IQueryable<MarketSessionRow> Latest(string calendar) =>
        db.MarketSessions.AsNoTracking().Where(s => s.Calendar == calendar
            && !db.MarketSessions.Any(m => m.Calendar == s.Calendar && m.Session == s.Session && m.Revision > s.Revision));

    /// <summary>The latest row of every fund event: the one in use, or its withdrawal.</summary>
    private IQueryable<FundEventRow> LatestEvents() =>
        db.FundEvents.AsNoTracking().Where(e =>
            !db.FundEvents.Any(f => f.InstrumentId == e.InstrumentId && f.Day == e.Day && f.Kind == e.Kind && f.Revision > e.Revision));

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
        return rows.GroupBy(e => (e.Day, e.Kind)).Select(revisions => revisions.Last())
            .Where(latest => !latest.Withdrawn).Select(latest => latest.ToDomain()).ToList();
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

        // A new event is revision 0 - or, when the owner withdrew it before and the source states it again, the
        // next revision after the withdrawal. One that is stored and in use is not new: revision 0 again, which the
        // database refuses.
        var withdrawn = what.Withdrawn ?? new Withdrawal([], [], "");
        var instrumentsWithEvents = what.Events.Concat(withdrawn.Events).Select(e => e.Instrument).Distinct().ToList();
        var lastRevision = (await LatestEvents()
                .Where(e => instrumentsWithEvents.Contains(e.InstrumentId))
                .Select(e => new { e.InstrumentId, e.Day, e.Kind, e.Revision, e.Withdrawn })
                .ToListAsync(cancellationToken))
            .ToDictionary(e => (e.InstrumentId, e.Day, e.Kind), e => (e.Revision, e.Withdrawn));
        var events = what.Events
            .Select(e => Row(e, lastRevision.TryGetValue((e.Instrument, e.Day, e.Kind), out var was) && was.Withdrawn ? was.Revision + 1 : 0, importedAt))
            .ToList();
        foreach (var fundEvent in what.RevisedEvents)
        {
            var latest = await db.FundEvents.AsNoTracking()
                .Where(e => e.InstrumentId == fundEvent.Instrument && e.Day == fundEvent.Day && e.Kind == fundEvent.Kind)
                .MaxAsync(e => (int?)e.Revision, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"There is no {fundEvent.Kind} of {fundEvent.Day:R} to revise for instrument {fundEvent.Instrument}.");
            events.Add(Row(fundEvent, latest + 1, importedAt));
        }

        foreach (var fundEvent in withdrawn.Events)
        {
            var latest = lastRevision.TryGetValue((fundEvent.Instrument, fundEvent.Day, fundEvent.Kind), out var was) && !was.Withdrawn
                ? was.Revision
                : throw new InvalidOperationException($"There is no {fundEvent.Kind} of {fundEvent.Day:R} in use to withdraw for instrument {fundEvent.Instrument}.");
            events.Add(Row(fundEvent, latest + 1, importedAt, withdrawnFor: withdrawn.Reason));
        }

        var sessionWithdrawals = new List<MarketSessionRow>();
        foreach (var day in withdrawn.Sessions)
        {
            var latest = await db.MarketSessions.AsNoTracking()
                .Where(s => s.Calendar == calendar && s.Session == day)
                .MaxAsync(s => (int?)s.Revision, cancellationToken)
                ?? throw new InvalidOperationException($"{day:R} is no stored session of {calendar} to withdraw.");
            sessionWithdrawals.Add(new MarketSessionRow
            {
                Calendar = calendar,
                Session = day,
                Revision = latest + 1,
                Withdrawn = true,
                Reason = withdrawn.Reason,
                ImportedAt = importedAt,
            });
        }

        // A first import is a couple of hundred thousand rows: no change detection over them.
        var detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            // One transaction: the sessions, the prices, the fund events, the withdrawals, the mark of the export
            // and the note about it all - or nothing. The sessions go first: the database takes a close or an
            // event only for a day that is a session; a day is withdrawn last, after the events that were on it.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.MarketSessions.AddRange(
                sessions.Select(s => new MarketSessionRow { Calendar = calendar, Session = s, ImportedAt = importedAt }));
            await db.SaveChangesAsync(cancellationToken);
            db.ClosingPrices.AddRange(rows);
            db.FundEvents.AddRange(events);
            await db.SaveChangesAsync(cancellationToken);
            db.MarketSessions.AddRange(sessionWithdrawals);
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

    /// <param name="withdrawnFor">The owner's reason when the row withdraws the event; null for the event itself.</param>
    private static FundEventRow Row(FundEvent fundEvent, int revision, Instant importedAt, string? withdrawnFor = null) => new()
    {
        InstrumentId = fundEvent.Instrument,
        Day = fundEvent.Day,
        Kind = fundEvent.Kind,
        Revision = revision,
        Value = fundEvent.Value,
        // A withdrawal is the owner's word, not the vendor's.
        Source = withdrawnFor is null ? fundEvent.Source : "owner",
        Withdrawn = withdrawnFor is not null,
        Reason = withdrawnFor,
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
