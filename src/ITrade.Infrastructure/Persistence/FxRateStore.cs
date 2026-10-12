using ITrade.Application.Fx;
using ITrade.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Infrastructure.Persistence;

/// <summary>
/// Table `fx_rates`: "1 base = rate quote" as a source published it for a date. The domain type is
/// <see cref="FxRate"/>. Rows are only ever inserted — the database rejects UPDATE, DELETE and TRUNCATE
/// (migration FxRatesRevisions). A rate the source corrected is a further row of the same date with the next
/// <see cref="Revision"/>; the latest revision is the rate in use, the earlier ones show what was used before.
/// </summary>
public sealed class FxRateRow
{
    public required Currency BaseCurrency { get; init; }

    public required Currency QuoteCurrency { get; init; }

    /// <summary>The calendar date the source published the rate for (a date in Israel for the Bank of Israel).</summary>
    public LocalDate RateDate { get; init; }

    public required string Source { get; init; }

    /// <summary>0 for the first rate of the date; one more each time the owner accepted a change by the source.</summary>
    public int Revision { get; init; }

    public decimal Rate { get; init; }

    /// <summary>When the platform got the rate from the source.</summary>
    public Instant FetchedAt { get; init; }

    /// <summary>The owner's reason for accepting this rate (a revision, or a first rate that moved unusually far); none otherwise.</summary>
    public string? Reason { get; init; }

    public FxRate ToDomain() => new(BaseCurrency, QuoteCurrency, Rate, RateDate, Source);
}

/// <summary>`fx_rates` behind <see cref="IFxRateStore"/>: reads the latest revisions, and inserts.</summary>
public sealed class FxRateStore(ITradeDbContext db) : IFxRateStore
{
    public const int MaxReasonLength = 300;

    public async Task<IReadOnlyList<FxRate>> ListAsync(
        Currency baseCurrency, Currency quoteCurrency, string source, LocalDate first, LocalDate last,
        CancellationToken cancellationToken)
    {
        var rows = await Of(baseCurrency, quoteCurrency, source)
            .Where(r => r.RateDate >= first && r.RateDate <= last)
            .OrderBy(r => r.RateDate).ThenBy(r => r.Revision)
            .ToListAsync(cancellationToken);
        // The last revision of each date is the rate in use.
        return rows.GroupBy(r => r.RateDate).Select(revisions => revisions.Last().ToDomain()).ToList();
    }

    public async Task<FxRate?> LatestOnOrBeforeAsync(
        Currency baseCurrency, Currency quoteCurrency, string source, LocalDate day, CancellationToken cancellationToken)
    {
        var row = await Of(baseCurrency, quoteCurrency, source)
            .Where(r => r.RateDate <= day)
            .OrderByDescending(r => r.RateDate).ThenByDescending(r => r.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        return row?.ToDomain();
    }

    public async Task AddAsync(
        IReadOnlyList<FxRate> added, IReadOnlyList<FxRate> accepted, string? reason, Instant fetchedAt, FxRatesNote note,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(note);
        if (db.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                "Exchange rates are written on their own: save or discard the other changes of this unit of work first.");
        }

        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (accepted.Count > 0 && why is null)
        {
            throw new ArgumentException("An accepted rate is recorded with the reason for accepting it.", nameof(reason));
        }

        if (why is { Length: > MaxReasonLength })
        {
            throw new ArgumentException($"The reason is longer than {MaxReasonLength} characters.", nameof(reason));
        }

        if (added.Concat(accepted).FirstOrDefault(r => decimal.Round(r.Rate, FxRate.Scale) != r.Rate) is { } tooFine)
        {
            // The column would round it without a word, and the stored rate would no longer be the source's.
            throw new ArgumentException(
                $"The rate of {tooFine.Date:R} has more than {FxRate.Scale} decimal places and cannot be stored exactly.", nameof(added));
        }

        var rows = added.Select(r => Row(r, revision: 0, reason: null, fetchedAt)).ToList();
        foreach (var rate in accepted)
        {
            // The next revision of a date that has a rate; the first rate of a date that has none.
            var latest = await Of(rate.From, rate.To, rate.Source)
                .Where(r => r.RateDate == rate.Date)
                .MaxAsync(r => (int?)r.Revision, cancellationToken);
            rows.Add(Row(rate, latest + 1 ?? 0, why, fetchedAt));
        }

        var entry = new AuditEvent { OccurredAt = fetchedAt, Actor = note.Actor, Kind = note.Kind, Payload = note.Payload };
        db.FxRates.AddRange(rows);
        db.AuditEvents.Add(entry);
        try
        {
            // One SaveChanges is one database transaction: all the rates with the note about them, or nothing.
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // Nothing stays tracked: a refused rate must not ride along with the next thing this context saves.
            db.Entry(entry).State = EntityState.Detached;
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    private static FxRateRow Row(FxRate rate, int revision, string? reason, Instant fetchedAt) => new()
    {
        BaseCurrency = rate.From,
        QuoteCurrency = rate.To,
        RateDate = rate.Date,
        Source = rate.Source,
        Revision = revision,
        Rate = rate.Rate,
        FetchedAt = fetchedAt,
        Reason = reason,
    };

    private IQueryable<FxRateRow> Of(Currency baseCurrency, Currency quoteCurrency, string source) =>
        db.FxRates.AsNoTracking()
            .Where(r => r.BaseCurrency == baseCurrency && r.QuoteCurrency == quoteCurrency && r.Source == source);
}
