using System.Globalization;
using System.Text.Json;
using ITrade.Application.Portfolio;
using ITrade.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Infrastructure.Persistence;

/// <summary>`instruments` and `instrument_former_tickers` behind <see cref="IInstrumentStore"/>.</summary>
public sealed class InstrumentStore(ITradeDbContext db, IClock clock) : IInstrumentStore
{
    public async Task<Instrument?> FindAsync(InstrumentId id, CancellationToken cancellationToken) =>
        (await db.Instruments.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken))?.ToDomain();

    public async Task<Instrument?> FindByConIdAsync(int ibkrConId, CancellationToken cancellationToken) =>
        (await db.Instruments.AsNoTracking().SingleOrDefaultAsync(i => i.IbkrConId == ibkrConId, cancellationToken))
        ?.ToDomain();

    public async Task<Instrument?> FindByTickerAsync(Ticker ticker, string exchange, CancellationToken cancellationToken)
    {
        var market = Market(exchange);
        var row = await db.Instruments.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Ticker == ticker && i.Exchange == market, cancellationToken);
        return row?.ToDomain();
    }

    public async Task<Instrument?> FindByTickerOnAsync(
        Ticker ticker, string exchange, LocalDate day, CancellationToken cancellationToken)
    {
        // Everything on the exchange that carries the ticker now or carried it once; the history decides the day.
        var market = Market(exchange);
        var candidates = await db.Instruments.AsNoTracking()
            .Where(i => i.Exchange == market
                && (i.Ticker == ticker || db.FormerTickers.Any(f => f.InstrumentId == i.Id && f.Ticker == ticker)))
            .ToListAsync(cancellationToken);
        // A ticker given up by one fund may be taken by another later. A dated period ("this fund was T until
        // that day") is evidence; a current ticker has no start date, so it only counts when no dated period
        // claims the day.
        var dated = new List<InstrumentRow>();
        var undated = new List<InstrumentRow>();
        foreach (var candidate in candidates)
        {
            var history = await HistoryOfAsync(candidate, cancellationToken);
            if (history.On(day) == ticker)
            {
                (history.Former.Any(f => day < f.Until) ? dated : undated).Add(candidate);
            }
        }

        var matches = dated.Count > 0 ? dated : undated;
        return matches.Count switch
        {
            0 => null,
            1 => matches[0].ToDomain(),
            _ => throw new InvalidOperationException(
                $"{matches.Count} instruments are recorded as {ticker} on {market} on {day:R}: the ticker histories overlap."),
        };
    }

    public async Task<TickerHistory?> TickersOfAsync(InstrumentId id, CancellationToken cancellationToken)
    {
        var row = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        return row is null ? null : await HistoryOfAsync(row, cancellationToken);
    }

    public async Task<IReadOnlyList<Instrument>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Instruments.AsNoTracking()
            .OrderBy(i => i.Ticker).ThenBy(i => i.Exchange)
            .ToListAsync(cancellationToken);
        return rows.ConvertAll(r => r.ToDomain());
    }

    public async Task AddAsync(Instrument instrument, CancellationToken cancellationToken)
    {
        db.Instruments.Add(InstrumentRow.From(instrument));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDetailsAsync(Instrument instrument, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        var changed = await db.Instruments
            .Where(i => i.Id == instrument.Id)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(i => i.Name, instrument.Name)
                    .SetProperty(i => i.Isin, instrument.Isin)
                    .SetProperty(i => i.IbkrConId, instrument.IbkrConId),
                cancellationToken);
        if (changed != 1)
        {
            throw new InvalidOperationException($"There is no instrument {instrument.Id}.");
        }
    }

    public async Task RenameAsync(
        InstrumentId id, Ticker newTicker, LocalDate effective, CancellationToken cancellationToken)
    {
        var to = newTicker;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.Instruments.SingleOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"There is no instrument {id}.");
        var history = await HistoryOfAsync(row, cancellationToken);
        _ = history.Renamed(to, effective); // refuses a rename out of order, also when two arrive at once
        var from = row.Ticker;
        db.FormerTickers.Add(new FormerTickerRow { InstrumentId = id, Ticker = from, Until = effective });
        row.Ticker = to;
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = clock.GetCurrentInstant(),
            Actor = "owner",
            Kind = "instrument.renamed",
            Payload = JsonSerializer.Serialize(new
            {
                instrument = id.Value,
                from = from.Value,
                to = to.Value,
                effective = effective.ToString("uuuu-MM-dd", CultureInfo.InvariantCulture),
            }),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<TickerHistory> HistoryOfAsync(InstrumentRow row, CancellationToken cancellationToken)
    {
        var former = await db.FormerTickers.AsNoTracking()
            .Where(f => f.InstrumentId == row.Id)
            .OrderBy(f => f.Until)
            .ToListAsync(cancellationToken);
        return new TickerHistory(row.Ticker, former.Select(f => new FormerTicker(f.Ticker, f.Until)));
    }

    private static string Market(string exchange) => (exchange ?? "").Trim().ToUpperInvariant();
}

/// <summary>`holding_places` behind <see cref="IHoldingPlaceStore"/>.</summary>
public sealed class HoldingPlaceStore(ITradeDbContext db) : IHoldingPlaceStore
{
    public async Task<IReadOnlyList<HoldingPlace>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.HoldingPlaces.AsNoTracking().OrderBy(h => h.Name).ToListAsync(cancellationToken);
        return rows.ConvertAll(r => r.ToDomain());
    }

    public async Task<HoldingPlace?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var wanted = (name ?? "").Trim();
        var row = await db.HoldingPlaces.AsNoTracking().SingleOrDefaultAsync(h => h.Name == wanted, cancellationToken);
        return row?.ToDomain();
    }

    public async Task AddAsync(HoldingPlace place, CancellationToken cancellationToken)
    {
        db.HoldingPlaces.Add(HoldingPlaceRow.From(place));
        await db.SaveChangesAsync(cancellationToken);
    }
}
