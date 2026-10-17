using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ITrade.Application.Portfolio;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Prices;

/// <summary>One line of an export of closing prices: a ticker, a session and the close as it traded.</summary>
public sealed record ClosingPriceLine(string Ticker, LocalDate Session, decimal Close);

/// <summary>One line of the export's table of fund events: a dividend per share as paid, or a split as new shares per old.</summary>
public sealed record FundEventLine(string Ticker, LocalDate Day, FundEventKind Kind, decimal Value);

/// <summary>
/// What the export says about one ticker: when its data was fetched, the currency the prices are in (when the
/// pipeline knows it) and the days of its splits — on those the price rightly jumps.
/// </summary>
public sealed record ExportedTicker(Instant FetchedAt, string? Currency, IReadOnlySet<LocalDate> SplitDays);

/// <summary>
/// An export of closing prices as the data pipeline wrote it (`itrade export-closes`): the closes, the
/// sessions of the exchange they cover — every one of them over when the data was fetched — and where it
/// all came from. The SHA-256 is the one that vouched for the table.
/// </summary>
public sealed record ClosingPricesExport(
    string Source,
    string Sha256,
    Instant ExportedAt,
    string Calendar,
    IReadOnlyList<LocalDate> Sessions,
    IReadOnlyDictionary<string, ExportedTicker> Tickers,
    IReadOnlyList<ClosingPriceLine> Lines,
    IReadOnlyList<FundEventLine> Events);

/// <summary>The export that was imported last: a later import must not be of an older one.</summary>
public sealed record ImportedExport(Instant ExportedAt, string Sha256);

/// <summary>
/// What one import adds, all of it or nothing. <paramref name="Added"/> are the first closes of their sessions;
/// <paramref name="Revised"/> are each the next revision of a session that has a close;
/// <paramref name="Sessions"/> are sessions of <paramref name="Calendar"/> not stored yet;
/// <paramref name="Events"/> are fund events not stored yet and <paramref name="RevisedEvents"/> each the next
/// revision of a stored one.
/// </summary>
/// <paramref name="Withdrawn"/> is what the owner withdraws with this import.
public sealed record PricesToStore(
    IReadOnlyList<ClosingPrice> Added,
    IReadOnlyList<ClosingPrice> Revised,
    string Calendar,
    IReadOnlyList<LocalDate> Sessions,
    IReadOnlyList<FundEvent> Events,
    IReadOnlyList<FundEvent> RevisedEvents,
    Withdrawal? Withdrawn = null)
{
    public bool IsEmpty =>
        Added.Count + Revised.Count + Sessions.Count + Events.Count + RevisedEvents.Count
        + (Withdrawn is null ? 0 : Withdrawn.Sessions.Count + Withdrawn.Events.Count) == 0;
}

/// <summary>
/// What the owner withdraws: stored sessions of the calendar the exchange was closed on after all, and stored fund
/// events the source no longer states - each kept, followed by a row that says it is withdrawn and why.
/// </summary>
public sealed record Withdrawal(IReadOnlyList<LocalDate> Sessions, IReadOnlyList<FundEvent> Events, string Reason);

/// <summary>The owner's step: withdraw exactly what an import found, by its code, for a reason in the owner's words.</summary>
public sealed record OwnersWithdrawal(string Code, string Reason);

/// <summary>A stored session the owner withdrew, and why.</summary>
public sealed record WithdrawnSession(LocalDate Session, string Reason);

/// <summary>A stored fund event the owner withdrew, and why.</summary>
public sealed record WithdrawnEvent(FundEvent Event, string Reason);

/// <summary>
/// Stored closing prices, the sessions they belong to and the funds' dividends and splits (tables
/// `closing_prices`, `market_sessions`, `fund_events`). Rows are only ever added: a price or an event the
/// source corrected is a new revision, and the reads give the latest revision. A session or an event the owner
/// withdrew is left out of every read but the ones that list withdrawals.
/// </summary>
public interface IClosingPriceStore
{
    /// <summary>Every stored close of the instrument, earliest first.</summary>
    Task<IReadOnlyList<ClosingPrice>> ListAsync(InstrumentId instrument, CancellationToken cancellationToken);

    /// <summary>The close of the instrument on that session, if stored.</summary>
    Task<ClosingPrice?> FindAsync(InstrumentId instrument, LocalDate session, CancellationToken cancellationToken);

    /// <summary>The latest stored close of the instrument before the day.</summary>
    Task<ClosingPrice?> LatestBeforeAsync(InstrumentId instrument, LocalDate day, CancellationToken cancellationToken);

    /// <summary>The latest session of each instrument that has a close at all.</summary>
    Task<IReadOnlyDictionary<InstrumentId, LocalDate>> LatestPerInstrumentAsync(CancellationToken cancellationToken);

    /// <summary>The sessions of the calendar that are stored, earliest first; a withdrawn day is none.</summary>
    Task<IReadOnlyList<LocalDate>> ListSessionsAsync(string calendar, CancellationToken cancellationToken);

    /// <summary>The latest stored session of the calendar on or before the day (not a withdrawn one); null when there is none.</summary>
    Task<LocalDate?> LatestSessionOnOrBeforeAsync(string calendar, LocalDate day, CancellationToken cancellationToken);

    Task<ImportedExport?> LastImportAsync(CancellationToken cancellationToken);

    /// <summary>Every stored dividend and split of the instrument, earliest first, but the withdrawn ones.</summary>
    Task<IReadOnlyList<FundEvent>> ListEventsAsync(InstrumentId instrument, CancellationToken cancellationToken);

    /// <summary>The sessions of the calendar the owner withdrew, earliest first.</summary>
    Task<IReadOnlyList<WithdrawnSession>> ListWithdrawnSessionsAsync(string calendar, CancellationToken cancellationToken);

    /// <summary>The dividends and splits of the instrument the owner withdrew, earliest first.</summary>
    Task<IReadOnlyList<WithdrawnEvent>> ListWithdrawnEventsAsync(InstrumentId instrument, CancellationToken cancellationToken);

    /// <summary>The dividends and splits in use on the day, of every instrument.</summary>
    Task<IReadOnlyList<FundEvent>> ListEventsOnAsync(LocalDate day, CancellationToken cancellationToken);

    /// <summary>Whether any instrument has a close stored for the session: then something traded that day.</summary>
    Task<bool> AnyCloseOnAsync(LocalDate session, CancellationToken cancellationToken);

    /// <summary>How many sessions (of any calendar) and fund events are withdrawn now.</summary>
    Task<(int Sessions, int Events)> CountWithdrawnAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds all of it or nothing, with the note for the audit trail and the mark of the export in the same step.
    /// An event stored before and withdrawn comes back as its next revision.
    /// </summary>
    Task AddAsync(
        PricesToStore what, Instant importedAt, PricesNote note, ImportedExport importedExport, CancellationToken cancellationToken);
}

/// <summary>An entry for the audit trail about an import of prices: who, what kind, and the details as JSON.</summary>
public sealed record PricesNote(string Actor, string Kind, string Payload)
{
    /// <summary>New prices were stored, nothing else.</summary>
    public const string Imported = "prices.imported";

    /// <summary>Something to know: a stored price was revised, or an unusual one was accepted.</summary>
    public const string Noted = "prices.noted";

    /// <summary>The owner withdrew stored sessions or fund events with this import.</summary>
    public const string Withdrawn = "prices.withdrawn";
}

/// <summary>A stored close and what the source states for the same session now.</summary>
public sealed record ClosingPriceChange(string Ticker, ClosingPrice Stored, ClosingPrice Stated);

/// <summary>A stored fund event and what the source states for the same day now.</summary>
public sealed record FundEventChange(string Ticker, FundEvent Stored, FundEvent Stated);

/// <summary>A new close that moves far from the close before it on a day that is not a split day.</summary>
public sealed record UnusualClose(string Ticker, ClosingPrice Before, ClosingPrice Close);

/// <summary>
/// What an import did. <paramref name="Revised"/> prices were recorded next to the old ones;
/// <paramref name="Unusual"/> ones were accepted by the owner. <paramref name="NotRepeated"/> counts stored
/// closes the export left out (they stay). <paramref name="Behind"/> are instruments whose last close is of
/// an earlier session than the export's latest; <paramref name="UnknownTickers"/> are in the export and not
/// in the catalogue (skipped); <paramref name="WithoutPrices"/> are instruments the export says nothing about.
/// <paramref name="EventsAdded"/> counts the dividends and splits stored for the first time;
/// <paramref name="EventsRevised"/> are the ones the source now states differently — recorded next to the old.
/// <paramref name="EventsNotRepeated"/> are stored dividends and splits, within the sessions the export covers
/// for their fund, that the export no longer states: they stay, and an index is followed with them — if the
/// source moved or withdrew one, the comparison is off by it until the owner withdraws it with
/// <paramref name="WithdrawCode"/>. <paramref name="SessionsWithdrawn"/> and <paramref name="EventsWithdrawn"/> are
/// what the owner withdrew with this import; <paramref name="EventsBack"/> are events the owner withdrew before
/// and the source states again - in use again.
/// </summary>
public sealed record ClosingPricesImport(
    int Added,
    int Unchanged,
    IReadOnlyList<ClosingPriceChange> Revised,
    IReadOnlyList<UnusualClose> Unusual,
    int NotRepeated,
    int NewSessions,
    LocalDate? LatestSession,
    IReadOnlyList<(string Ticker, LocalDate Last)> Behind,
    IReadOnlyList<string> UnknownTickers,
    IReadOnlyList<string> WithoutPrices,
    int EventsAdded,
    IReadOnlyList<FundEventChange> EventsRevised,
    IReadOnlyList<(string Ticker, FundEvent Event)> EventsNotRepeated,
    string? WithdrawCode = null,
    IReadOnlyList<LocalDate>? SessionsWithdrawn = null,
    IReadOnlyList<(string Ticker, FundEvent Event)>? EventsWithdrawn = null,
    IReadOnlyList<(string Ticker, FundEvent Event)>? EventsBack = null);

/// <summary>
/// The import found something only the owner can decide about; nothing was stored.
/// <see cref="Exception.Message"/> says what; <see cref="Code"/> identifies exactly that finding, and
/// accepting it takes the code.
/// </summary>
public sealed class PricesDisagreeException : Exception
{
    public PricesDisagreeException()
    {
    }

    public PricesDisagreeException(string message)
        : base(message)
    {
    }

    public PricesDisagreeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PricesDisagreeException(string message, string code)
        : base(message) => Code = code;

    /// <param name="isWithdrawal">The code is for withdrawing (`--withdraw`), not for accepting (`--accept-changes`).</param>
    public PricesDisagreeException(string message, string code, bool isWithdrawal)
        : base(message) => (Code, IsWithdrawal) = (code, isWithdrawal);

    public string Code { get; } = "";

    public bool IsWithdrawal { get; }
}

/// <summary>
/// Closing prices for valuing the portfolio — never for tax and never for an order. They come from the data
/// pipeline's export of prices as traded; a stored price is never overwritten.
/// </summary>
public sealed class ClosingPrices(IClosingPriceStore store, IInstrumentStore instruments, IClock clock)
{
    /// <summary>The calendar every instrument of the catalogue trades on today: the New York exchanges.</summary>
    public const string Calendar = "XNYS";

    /// <summary>
    /// A source that fixes a bad tick changes a price or two, by a little. More changed prices than this in
    /// one import is something else — a broken export, a vendor re-basing its history.
    /// </summary>
    public const int MostRevisionsTakenUnasked = 20;

    /// <summary>
    /// A correction of a close is a matter of cents. A stored close restated by more than this is not taken
    /// unasked however few there are: around a split the vendor is known to adjust a recent bar twice, or not
    /// at all.
    /// </summary>
    public const decimal LargestRevisionTakenUnasked = 0.02m;

    /// <summary>
    /// The largest move from one close to the next in the stored history, split days aside, is 25.6% (EWZ,
    /// 13 October 2008; measured on the full export, 2026-10-12). A new close farther than this from the one
    /// before it, on a day the export names no split for, is more likely a wrong number than a price.
    /// </summary>
    public const decimal LargestUsualMove = 0.30m;

    /// <summary>
    /// For how many days after the last stored session that session's close may still be used, marked
    /// provisional: a weekend with a holiday on either side. Sessions may have passed since — nothing stored
    /// says they did not.
    /// </summary>
    public const int MostDaysCarriedUnconfirmed = 4;

    /// <summary>
    /// The most sessions withdrawn with one code. The longest unscheduled closure of the New York exchanges in
    /// the stored history is four sessions (11-14 September 2001); more not listed is a broken calendar.
    /// </summary>
    public const int MostSessionsWithdrawnAtOnce = 5;

    /// <summary>
    /// The most dividends and splits withdrawn with one code. A vendor that moves an ex-date or drops a doubled
    /// dividend changes one or two; many at once is an incomplete export, not a correction.
    /// </summary>
    public const int MostEventsWithdrawnAtOnce = 20;

    /// <summary>The longest reason a withdrawal keeps (the column holds 500 characters).</summary>
    public const int LongestReason = 500;

    /// <summary>When a full session of the New York exchanges ends.</summary>
    private static readonly LocalTime SessionEnd = new(16, 0);

    /// <summary>
    /// Stores the closes and the sessions of the export that are not stored yet. Running it again adds
    /// nothing. Tickers are matched to the instruments of the catalogue by their current ticker; the price is
    /// in the instrument's currency, and an export that states another currency is refused.
    ///
    /// The export is refused whole when it is older than the one imported last (it would undo corrections),
    /// when a close is of a day that is not one of its sessions or was fetched before that session ended, or
    /// when its sessions contradict the stored ones.
    ///
    /// A stored close the export now states differently is recorded as the next revision of its session and
    /// reported — when there are at most <see cref="MostRevisionsTakenUnasked"/> and none moves more than
    /// <see cref="LargestRevisionTakenUnasked"/>. More or larger revisions, and a new close that moves more
    /// than <see cref="LargestUsualMove"/> on a day that is not a split day, stop the import
    /// (<see cref="PricesDisagreeException"/>, nothing stored) until the owner accepts exactly that set with
    /// <paramref name="acceptedCode"/>. A stored close the export leaves out is kept and counted.
    ///
    /// The export's dividends and splits of the same instruments are stored with the prices. One the source
    /// now states differently is the next revision of its day and is reported; nothing about them is asked.
    ///
    /// What only the owner can withdraw, and only with <paramref name="withdrawal"/> - its code is that of exactly
    /// what this export finds: a stored session within the export's days that the export does not list (the
    /// import is refused until then: the two calendars disagree), and a stored event the export no longer states
    /// (the import goes on; the code is offered). Withdrawn rows stay, followed by rows that say so; a session on
    /// which something closed is never withdrawn, and an export that lists a withdrawn session again is refused.
    /// </summary>
    public Task<ClosingPricesImport> ImportAsync(
        ClosingPricesExport export, string? acceptedCode, CancellationToken cancellationToken) =>
        ImportAsync(export, acceptedCode, null, cancellationToken);

    /// <inheritdoc cref="ImportAsync(ClosingPricesExport, string?, CancellationToken)"/>
    public async Task<ClosingPricesImport> ImportAsync(
        ClosingPricesExport export, string? acceptedCode, OwnersWithdrawal? withdrawal, CancellationToken cancellationToken)
    {
        if (withdrawal is not null && (string.IsNullOrWhiteSpace(withdrawal.Code) || string.IsNullOrWhiteSpace(withdrawal.Reason)
            || withdrawal.Reason.Trim().Length > LongestReason))
        {
            throw new ArgumentException(
                $"A withdrawal takes the code of the finding and the owner's reason, in words (at most {LongestReason} characters).", nameof(withdrawal));
        }

        ArgumentNullException.ThrowIfNull(export);
        if (export.Calendar != Calendar)
        {
            throw new InvalidOperationException($"The export is of the calendar {export.Calendar}; the platform keeps {Calendar}.");
        }

        var last = await store.LastImportAsync(cancellationToken);
        if (last is not null && export.ExportedAt < last.ExportedAt)
        {
            throw new InvalidOperationException(
                $"This export was written on {export.ExportedAt:uuuu-MM-dd HH:mm} UTC, before the one imported last " +
                $"({last.ExportedAt:uuuu-MM-dd HH:mm} UTC). An older export would undo the corrections of the newer one.");
        }

        var today = TradeDate.Of(clock.GetCurrentInstant()).Value;
        var sessions = export.Sessions.ToHashSet();
        if (export.Sessions.Zip(export.Sessions.Skip(1)).Any(pair => pair.First >= pair.Second))
        {
            throw new InvalidOperationException("The export's sessions are not in order of date, each once.");
        }

        if (export.Sessions.Count > 0 && export.Sessions[^1] > today)
        {
            throw new InvalidOperationException(
                $"The export names {export.Sessions[^1]:R} as a finished session, and today in New York is {today:R}.");
        }

        // Both the stored sessions and the export's are every session of a stretch of the calendar, so where
        // the two stretches overlap they must name the same days. A day only one of them takes for a session
        // would turn every instrument's price of that day into a gap, or hide one.
        var storedSessions = await store.ListSessionsAsync(Calendar, cancellationToken);
        // A day the owner withdrew stays withdrawn: a calendar that lists it again is the one that is wrong.
        foreach (var withdrawnDay in (await store.ListWithdrawnSessionsAsync(Calendar, cancellationToken)).Where(w => sessions.Contains(w.Session)).Take(1))
        {
            throw new InvalidOperationException(
                $"The export lists {withdrawnDay.Session:R} as a session of {Calendar}; the owner withdrew that day ({withdrawnDay.Reason}). The calendar of the export is wrong - correct it and export again.");
        }

        var sessionsNotListed = new List<LocalDate>();
        if (export.Sessions.Count > 0 && storedSessions.Count > 0)
        {
            var storedDays = storedSessions.ToHashSet();
            // Only the owner can say the exchange was closed on a day stored as a session (decided below).
            sessionsNotListed.AddRange(storedSessions.Where(s => s >= export.Sessions[0] && s <= export.Sessions[^1] && !sessions.Contains(s)));
            foreach (var day in sessionsNotListed)
            {
                if (await store.AnyCloseOnAsync(day, cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"{day:R} is stored as a session of {Calendar} and the export does not list it, but closing prices are stored for it: something traded that day, so it was a session. The calendar of the export is wrong.");
                }
            }

            foreach (var day in export.Sessions.Where(s => s > storedSessions[0] && s < storedSessions[^1] && !storedDays.Contains(s)).Take(1))
            {
                throw new InvalidOperationException(
                    $"The export lists {day:R} as a session of {Calendar}, and the stored sessions around it do not: the two calendars disagree.");
            }
        }

        var known = new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in await instruments.ListAsync(cancellationToken))
        {
            if (!known.TryAdd(instrument.Ticker.Value, instrument))
            {
                // The export names a fund by its ticker alone; two listings of one ticker cannot be told apart.
                throw new InvalidOperationException(
                    $"Two instruments trade as {instrument.Ticker}: prices cannot be matched by ticker.");
            }
        }

        var byTicker = export.Lines.GroupBy(l => l.Ticker, StringComparer.OrdinalIgnoreCase).ToList();
        var added = new List<ClosingPrice>();
        var revised = new List<ClosingPriceChange>();
        var unusual = new List<UnusualClose>();
        var behind = new List<(string, LocalDate)>();
        var events = new List<FundEvent>();
        var eventsRevised = new List<FundEventChange>();
        var eventsNotRepeated = new List<(string Ticker, FundEvent Event)>();
        var eventsBack = new List<(string, FundEvent)>();
        var eventLines = export.Events.ToLookup(e => e.Ticker, StringComparer.OrdinalIgnoreCase);
        var (unchanged, notRepeated) = (0, 0);
        foreach (var lines in byTicker)
        {
            if (!known.TryGetValue(lines.Key, out var instrument))
            {
                continue; // not in the catalogue: listed in the result
            }

            var about = export.Tickers.GetValueOrDefault(lines.Key);
            if (about?.Currency is { } currency && !string.Equals(currency, instrument.Currency.Code, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The export's prices of {instrument.Ticker} are in {currency}; the instrument is kept in {instrument.Currency}.");
            }

            var stored = (await store.ListAsync(instrument.Id, cancellationToken)).ToDictionary(p => p.Session);
            var stated = new HashSet<LocalDate>();
            ClosingPrice? before = null;
            foreach (var line in lines.OrderBy(l => l.Session))
            {
                // An export that starts later than the stored closes: the close before its first line is a stored one.
                before ??= stored.Values.Where(p => p.Session < line.Session).MaxBy(p => p.Session);
                if (!stated.Add(line.Session))
                {
                    throw new InvalidOperationException(
                        $"The export states two closes of {instrument.Ticker} for {line.Session:R}.");
                }

                if (!sessions.Contains(line.Session))
                {
                    throw new InvalidOperationException(
                        $"The export states a close of {instrument.Ticker} for {line.Session:R}, which is not one of its finished sessions.");
                }

                var price = new ClosingPrice(
                    instrument.Id, line.Session, new Price(line.Close, instrument.Currency), export.Source);
                if (!stored.TryGetValue(line.Session, out var existing))
                {
                    added.Add(price);
                    if (before is not null && about?.SplitDays.Contains(line.Session) != true
                        && Math.Abs((price.Close.Value / before.Close.Value) - 1m) > LargestUsualMove)
                    {
                        unusual.Add(new UnusualClose(instrument.Ticker.Value, before, price));
                    }
                }
                else if (existing.Close == price.Close)
                {
                    unchanged++;
                }
                else
                {
                    revised.Add(new ClosingPriceChange(instrument.Ticker.Value, existing, price));
                }

                before = price;
            }

            // The pipeline leaves out a bar fetched while its session was open - it is the last trade so far,
            // not a close. Checked here once more, without the exchange's calendar: no session ends before
            // 16:00 in New York but a half day, and on one of those the data is to be fetched after 16:00.
            if (about is not null && stated.Count > 0
                && about.FetchedAt < (stated.Max() + SessionEnd).InZoneLeniently(Zones.NewYork).ToInstant())
            {
                throw new InvalidOperationException(
                    $"The export states a close of {instrument.Ticker} for {stated.Max():R}, and its data was fetched at " +
                    $"{about.FetchedAt.InZone(Zones.NewYork):uuuu-MM-dd HH:mm} in New York, before that session ended. " +
                    "Fetch it again after 16:00 New York time.");
            }

            notRepeated += stored.Keys.Count(session => !stated.Contains(session));
            var storedEvents = (await store.ListEventsAsync(instrument.Id, cancellationToken)).ToDictionary(e => (e.Day, e.Kind));
            var withdrawnEvents = (await store.ListWithdrawnEventsAsync(instrument.Id, cancellationToken))
                .Select(w => (w.Event.Day, w.Event.Kind)).ToHashSet();
            var statedEvents = new HashSet<(LocalDate, FundEventKind)>();
            foreach (var line in eventLines[lines.Key])
            {
                if (!statedEvents.Add((line.Day, line.Kind)))
                {
                    throw new InvalidOperationException(
                        $"The export states two {Name(line.Kind)}s of {instrument.Ticker} for {line.Day:R}.");
                }

                if (!sessions.Contains(line.Day))
                {
                    throw new InvalidOperationException(
                        $"The export states a {Name(line.Kind)} of {instrument.Ticker} for {line.Day:R}, which is not one of its finished sessions.");
                }

                var statedEvent = new FundEvent(instrument.Id, line.Day, line.Kind, line.Value, export.Source);
                if (!storedEvents.TryGetValue((line.Day, line.Kind), out var was))
                {
                    // The owner withdrew it because the source no longer stated it; the source states it again.
                    if (withdrawnEvents.Contains((line.Day, line.Kind)))
                    {
                        eventsBack.Add((instrument.Ticker.Value, statedEvent));
                    }

                    events.Add(statedEvent);
                }
                else if (was.Value != statedEvent.Value)
                {
                    eventsRevised.Add(new FundEventChange(instrument.Ticker.Value, was, statedEvent));
                }
            }

            // Within the days the export covers for this fund it states every event it knows: a stored one it
            // leaves out was moved or withdrawn by the source. It cannot be removed here; it is said.
            eventsNotRepeated.AddRange(storedEvents.Values
                .Where(e => !statedEvents.Contains((e.Day, e.Kind)) && e.Day >= stated.Min() && e.Day <= stated.Max())
                .Select(e => (instrument.Ticker.Value, e)));

            if (before is not null && export.Sessions.Count > 0 && before.Session < export.Sessions[^1])
            {
                behind.Add((instrument.Ticker.Value, before.Session));
            }
        }

        // What only the owner can withdraw, as two findings with a code each, every item named. The sessions the
        // export does not list go with every event that is on them, of any fund (a day that is none has none);
        // the events it no longer states are the rest. More than a correction is a broken export: no code.
        var nameOf = known.Values.ToDictionary(i => i.Id, i => i.Ticker.Value);
        var onThoseDays = new List<(string Ticker, FundEvent Event)>();
        foreach (var day in sessionsNotListed)
        {
            onThoseDays.AddRange((await store.ListEventsOnAsync(day, cancellationToken))
                .Select(e => (nameOf.GetValueOrDefault(e.Instrument) ?? e.Instrument.ToString(), e)));
        }

        var toOffer = eventsNotRepeated.Where(e => !sessionsNotListed.Contains(e.Event.Day)).ToList();
        var sessionCode = sessionsNotListed.Count is > 0 and <= MostSessionsWithdrawnAtOnce
            ? WithdrawCodeOf("sessions", sessionsNotListed, onThoseDays) : null;
        var eventCode = toOffer.Count is > 0 and <= MostEventsWithdrawnAtOnce
            ? WithdrawCodeOf("events", [], toOffer) : null;
        Withdrawal? withdrawn = null;
        var withdrawnNow = new List<(string Ticker, FundEvent Event)>();
        var given = withdrawal?.Code.Trim();
        // No code (too many sessions) matches nothing - not even no code given.
        if (sessionsNotListed.Count > 0 && (sessionCode is null || !string.Equals(given, sessionCode, StringComparison.OrdinalIgnoreCase)))
        {
            if (sessionCode is null)
            {
                throw new InvalidOperationException(
                    $"{DescribeWithdrawable(sessionsNotListed, [])} That is more than an unscheduled closure (at most {MostSessionsWithdrawnAtOnce} sessions are withdrawn at once): the calendar of the export is broken. Nothing was stored.");
            }

            throw new PricesDisagreeException(
                $"{(given is null ? "" : $"The code {given} is not the one of these sessions. ")}{DescribeWithdrawable(sessionsNotListed, onThoseDays)} The two calendars disagree. If the exchange was closed on that day after all (see the exchange's own holiday list), the stored session is wrong and the owner withdraws it - with the dividends and splits named on it. Nothing was stored.",
                sessionCode, isWithdrawal: true);
        }

        if (given is not null)
        {
            if (string.Equals(given, sessionCode, StringComparison.OrdinalIgnoreCase))
            {
                withdrawn = new Withdrawal(sessionsNotListed, onThoseDays.ConvertAll(e => e.Event), withdrawal!.Reason.Trim());
                withdrawnNow = onThoseDays;
            }
            else if (string.Equals(given, eventCode, StringComparison.OrdinalIgnoreCase))
            {
                withdrawn = new Withdrawal([], toOffer.ConvertAll(e => e.Event), withdrawal!.Reason.Trim());
                withdrawnNow = toOffer;
            }
            else if (eventCode is null)
            {
                throw new InvalidOperationException(toOffer.Count > MostEventsWithdrawnAtOnce
                    ? $"The export no longer states {toOffer.Count} stored dividend(s) or split(s): more than a correction (at most {MostEventsWithdrawnAtOnce} are withdrawn at once) - the export is probably incomplete. Nothing was withdrawn; fetch and export again."
                    : "There is nothing to withdraw: the export lists every stored session of its days and states every stored dividend and split. Run the plain import.");
            }
            else
            {
                throw new PricesDisagreeException(
                    $"The code {given} is not the one of what this export finds now. {DescribeWithdrawable([], toOffer)} Nothing was stored or withdrawn.",
                    eventCode, isWithdrawal: true);
            }
        }

        var needsTheOwner = unusual.Count > 0 || revised.Count > MostRevisionsTakenUnasked
            || revised.Exists(c => Math.Abs((c.Stated.Close.Value / c.Stored.Close.Value) - 1m) > LargestRevisionTakenUnasked);
        var code = needsTheOwner ? CodeOf(revised, unusual) : null;
        if (code is not null && !string.Equals(acceptedCode?.Trim(), code, StringComparison.OrdinalIgnoreCase))
        {
            throw new PricesDisagreeException($"{Describe(revised, unusual)} Nothing was stored. If the export is right, accept exactly this (acceptance code {code}).", code);
        }

        if (acceptedCode is not null && code is null)
        {
            throw new InvalidOperationException(
                "There is nothing to accept: the export holds nothing that is not taken unasked. Run the plain import.");
        }

        var unknown = byTicker.Select(g => g.Key).Where(t => !known.ContainsKey(t)).Order(StringComparer.Ordinal).ToList();
        var without = known.Values
            .Where(i => !byTicker.Exists(g => string.Equals(g.Key, i.Ticker.Value, StringComparison.OrdinalIgnoreCase)))
            .Select(i => i.Ticker.Value).Order(StringComparer.Ordinal).ToList();
        var newSessions = export.Sessions.Except(storedSessions).Order().ToList();
        var result = new ClosingPricesImport(
            added.Count, unchanged, revised, unusual, notRepeated, newSessions.Count,
            export.Sessions.Count > 0 ? export.Sessions[^1] : null, behind, unknown, without, events.Count - eventsBack.Count, eventsRevised,
            withdrawn is not null && withdrawn.Sessions.Count == 0 ? [] : toOffer,
            WithdrawCode: withdrawn is not null && withdrawn.Sessions.Count == 0 ? null : eventCode,
            SessionsWithdrawn: withdrawn?.Sessions ?? [],
            EventsWithdrawn: withdrawnNow,
            EventsBack: eventsBack);
        var toStore = new PricesToStore(
            added, revised.ConvertAll(c => c.Stated), Calendar, newSessions, events, eventsRevised.ConvertAll(c => c.Stated), withdrawn);
        if (!toStore.IsEmpty)
        {
            await store.AddAsync(
                toStore, clock.GetCurrentInstant(), NoteOf(export, result, code, withdrawn?.Reason),
                new ImportedExport(export.ExportedAt, export.Sha256), cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// The stored dividends and splits of the instrument on the days after <paramref name="after"/> up to
    /// <paramref name="through"/>, earliest first — what the fund did to a holder between those two evenings.
    /// </summary>
    public async Task<IReadOnlyList<FundEvent>> EventsAsync(
        InstrumentId instrument, LocalDate after, LocalDate through, CancellationToken cancellationToken) =>
        (await store.ListEventsAsync(instrument, cancellationToken)).Where(e => e.Day > after && e.Day <= through).ToList();

    /// <summary>
    /// The close to value the instrument with on a date, or why there is none. Whether the exchange was open
    /// on a day is answered by the stored sessions of its calendar, which reach as far as the last import.
    /// <list type="bullet">
    /// <item>The date is a session and the instrument has its close: that close.</item>
    /// <item>The date is a stored session, or follows one, for which the instrument has no close: missing.
    /// A gap is said, not filled with an older price.</item>
    /// <item>The exchange was closed on the date: the close of the last session before it, carried.</item>
    /// <item>The date is after the last stored session: that session's close, carried and provisional — the
    /// exchange may have been open since — for up to <see cref="MostDaysCarriedUnconfirmed"/> days.</item>
    /// <item>The date is after today (New York), before the instrument's first close, or too far past the
    /// last stored session: unavailable.</item>
    /// </list>
    /// </summary>
    public async Task<PriceOnDate> OnAsync(InstrumentId instrument, LocalDate date, CancellationToken cancellationToken)
    {
        var today = TradeDate.Of(clock.GetCurrentInstant()).Value;
        if (date > today)
        {
            return PriceOnDate.Unavailable(instrument, date);
        }

        if (await store.FindAsync(instrument, date, cancellationToken) is { } close)
        {
            return PriceOnDate.OfTheSession(close);
        }

        // The session whose close the date goes by: itself when it is one, else the last one before it.
        if (await store.LatestSessionOnOrBeforeAsync(Calendar, date, cancellationToken) is not { } session)
        {
            return PriceOnDate.Unavailable(instrument, date);
        }

        var lastStored = await store.LatestSessionOnOrBeforeAsync(Calendar, today, cancellationToken);
        var beyondWhatIsStored = session == lastStored && date > session;
        if (beyondWhatIsStored && Period.DaysBetween(session, date) > MostDaysCarriedUnconfirmed)
        {
            return PriceOnDate.Unavailable(instrument, date);
        }

        if (await store.FindAsync(instrument, session, cancellationToken) is not { } carried)
        {
            // No close for that session. A gap if the instrument was priced before it; otherwise the date is
            // simply before the instrument's first close.
            return await store.LatestBeforeAsync(instrument, session, cancellationToken) is null
                ? PriceOnDate.Unavailable(instrument, date)
                : PriceOnDate.Missing(instrument, date);
        }

        return PriceOnDate.Carried(carried, date, isProvisional: beyondWhatIsStored);
    }

    private static string Describe(List<ClosingPriceChange> revised, List<UnusualClose> unusual)
    {
        var findings = new List<string>();
        if (revised.Count > 0)
        {
            findings.Add(Invariant(
                $"The export states {revised.Count} stored closing price(s) differently, for example {string.Join("; ", revised.Take(3).Select(Describe))}. A source that corrects a bad tick changes a few prices by a little (up to {MostRevisionsTakenUnasked}, each within {LargestRevisionTakenUnasked * 100m:0}%, are recorded as revisions unasked); this usually means the export is wrong."));
        }

        if (unusual.Count > 0)
        {
            findings.Add(Invariant(
                $"{unusual.Count} new close(s) move more than {LargestUsualMove * 100m:0}% from the close before on a day the export names no split for, for example {string.Join("; ", unusual.Take(3).Select(Describe))}."));
        }

        return string.Join(" ", findings);
    }

    private static string Name(FundEventKind kind) => kind == FundEventKind.Split ? "split" : "dividend";

    private static string Describe(string ticker, FundEvent e) => Invariant($"{ticker} {e.Day:R} {Name(e.Kind)} {e.Value:0.######}");

    /// <summary>Every item, none left out: the owner withdraws exactly what is named here.</summary>
    private static string DescribeWithdrawable(List<LocalDate> sessions, List<(string Ticker, FundEvent Event)> events)
    {
        var findings = new List<string>();
        if (sessions.Count > 0)
        {
            findings.Add(Invariant(
                $"{string.Join(", ", sessions.Select(s => s.ToString("R", CultureInfo.InvariantCulture)))} {(sessions.Count == 1 ? "is" : "are")} stored as session(s) of {Calendar} and the export does not list {(sessions.Count == 1 ? "it" : "them")}."));
        }

        if (events.Count > 0)
        {
            var named = string.Join("; ", events.Select(e => Describe(e.Ticker, e.Event)));
            findings.Add(sessions.Count > 0
                ? $"On {(sessions.Count == 1 ? "it" : "them")} stored: {named}."
                : Invariant($"The export no longer states {events.Count} stored dividend(s) or split(s): {named}."));
        }

        return string.Join(" ", findings);
    }

    /// <summary>A short code of exactly what can be withdrawn: every session and every event with its value.</summary>
    private static string WithdrawCodeOf(string what, List<LocalDate> sessions, List<(string Ticker, FundEvent Event)> events)
    {
        var text = new StringBuilder(Invariant($"withdraw {what};"));
        foreach (var day in sessions.Order())
        {
            text.Append(Invariant($"session {day:R};"));
        }

        foreach (var (ticker, e) in events.OrderBy(e => e.Ticker, StringComparer.Ordinal).ThenBy(e => e.Event.Day).ThenBy(e => e.Event.Kind))
        {
            text.Append(Invariant($"event {ticker} {e.Day:R} {e.Kind} {e.Value:0.######};"));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..8];
    }

    private static string Describe(FundEventChange change) => Invariant(
        $"{change.Ticker} {change.Stored.Day:R} {Name(change.Stored.Kind)}: {change.Stored.Value:0.######} stored, {change.Stated.Value:0.######} stated");

    private static string Describe(ClosingPriceChange change) => Invariant(
        $"{change.Ticker} {change.Stored.Session:R}: {change.Stored.Close.Value:0.######} stored, {change.Stated.Close.Value:0.######} stated");

    private static string Describe(UnusualClose close) => Invariant(
        $"{close.Ticker} {close.Close.Session:R}: {close.Close.Close.Value:0.######} after {close.Before.Close.Value:0.######} on {close.Before.Session:R}");

    /// <summary>A short code of exactly this finding: every session with the price on both sides.</summary>
    private static string CodeOf(List<ClosingPriceChange> revised, List<UnusualClose> unusual)
    {
        var text = new StringBuilder();
        foreach (var change in revised.OrderBy(c => c.Ticker, StringComparer.Ordinal).ThenBy(c => c.Stored.Session))
        {
            text.Append(Invariant(
                $"revised {change.Ticker} {change.Stored.Session:R} {change.Stored.Close.Value:0.######} {change.Stated.Close.Value:0.######};"));
        }

        foreach (var close in unusual.OrderBy(c => c.Ticker, StringComparer.Ordinal).ThenBy(c => c.Close.Session))
        {
            text.Append(Invariant(
                $"unusual {close.Ticker} {close.Close.Session:R} {close.Close.Close.Value:0.######} after {close.Before.Close.Value:0.######};"));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..8];
    }

    private static PricesNote NoteOf(ClosingPricesExport export, ClosingPricesImport result, string? acceptedCode, string? withdrawalReason)
    {
        static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var remarks = new List<string>();
        if (result.Revised.Count > 0)
        {
            remarks.Add(Invariant(
                $"{result.Revised.Count} stored price(s) revised, e.g. {string.Join("; ", result.Revised.Take(3).Select(Describe))}"));
        }

        if (result.Unusual.Count > 0)
        {
            remarks.Add(Invariant(
                $"{result.Unusual.Count} unusual new close(s) accepted, e.g. {string.Join("; ", result.Unusual.Take(3).Select(Describe))}"));
        }

        if (result.EventsRevised.Count > 0)
        {
            remarks.Add(Invariant(
                $"{result.EventsRevised.Count} stored dividend(s) or split(s) revised, e.g. {string.Join("; ", result.EventsRevised.Take(3).Select(Describe))}"));
        }

        var sessionsWithdrawn = result.SessionsWithdrawn ?? [];
        var eventsWithdrawn = result.EventsWithdrawn ?? [];
        var eventsBack = result.EventsBack ?? [];
        if (sessionsWithdrawn.Count + eventsWithdrawn.Count > 0)
        {
            remarks.Add(Invariant(
                $"withdrawn by the owner ({withdrawalReason}): {sessionsWithdrawn.Count} session(s), {eventsWithdrawn.Count} dividend(s) or split(s)"));
        }

        if (eventsBack.Count > 0)
        {
            remarks.Add(Invariant(
                $"{eventsBack.Count} withdrawn dividend(s) or split(s) stated again and in use again, e.g. {string.Join("; ", eventsBack.Take(3).Select(e => Describe(e.Ticker, e.Event)))}"));
        }

        var payload = JsonSerializer.Serialize(new
        {
            source = export.Source,
            sha256 = export.Sha256,
            exportedAt = export.ExportedAt.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            added = result.Added,
            unchanged = result.Unchanged,
            newSessions = result.NewSessions,
            latestSession = result.LatestSession is { } latest ? Iso(latest) : null,
            notRepeated = result.NotRepeated,
            acceptedCode,
            revised = result.Revised.Select(c => new
            {
                ticker = c.Ticker,
                session = Iso(c.Stored.Session),
                was = c.Stored.Close.Value,
                now = c.Stated.Close.Value,
            }),
            unusual = result.Unusual.Select(c => new
            {
                ticker = c.Ticker,
                session = Iso(c.Close.Session),
                close = c.Close.Close.Value,
                before = c.Before.Close.Value,
            }),
            eventsAdded = result.EventsAdded,
            eventsNotRepeated = result.EventsNotRepeated.Select(e => new
            {
                ticker = e.Ticker,
                day = Iso(e.Event.Day),
                kind = Name(e.Event.Kind),
                value = e.Event.Value,
            }),
            eventsRevised = result.EventsRevised.Select(c => new
            {
                ticker = c.Ticker,
                day = Iso(c.Stored.Day),
                kind = Name(c.Stored.Kind),
                was = c.Stored.Value,
                now = c.Stated.Value,
            }),
            withdrawCode = result.WithdrawCode,
            withdrawalReason,
            sessionsWithdrawn = sessionsWithdrawn.Select(Iso),
            eventsWithdrawn = eventsWithdrawn.Select(e => new { ticker = e.Ticker, day = Iso(e.Event.Day), kind = Name(e.Event.Kind), value = e.Event.Value }),
            eventsBack = eventsBack.Select(e => new { ticker = e.Ticker, day = Iso(e.Event.Day), kind = Name(e.Event.Kind), value = e.Event.Value }),
            remarks,
        });
        var kind = withdrawalReason is not null ? PricesNote.Withdrawn
            : remarks.Count > 0 ? PricesNote.Noted
            : PricesNote.Imported;
        return new PricesNote(acceptedCode is null && withdrawalReason is null ? "prices" : "owner", kind, payload);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
