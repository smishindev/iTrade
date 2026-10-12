using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ITrade.Application.Jobs;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Application.Fx;

/// <summary>Where official exchange rates come from (the Bank of Israel). One currency pair per source.</summary>
public interface IFxRateSource
{
    /// <summary>Stored with every rate: which source, and which of its rates.</summary>
    string Name { get; }

    Currency BaseCurrency { get; }

    Currency QuoteCurrency { get; }

    /// <summary>
    /// The rates the source published for the dates from <paramref name="first"/> to <paramref name="last"/>,
    /// both included, earliest first. Days without a rate (weekends, holidays) are simply absent. A source
    /// that cannot be reached throws <see cref="TemporaryFailureException"/>; an answer that is not what it
    /// should be throws anything else.
    /// </summary>
    Task<IReadOnlyList<FxRate>> FetchAsync(LocalDate first, LocalDate last, CancellationToken cancellationToken);
}

/// <summary>
/// Stored exchange rates (table `fx_rates`). Rows are only ever added: a rate the source corrected is a new
/// revision of its date, and the reads give the latest revision.
/// </summary>
public interface IFxRateStore
{
    /// <summary>The stored rates of the source for the dates from first to last, both included, earliest first.</summary>
    Task<IReadOnlyList<FxRate>> ListAsync(
        Currency baseCurrency, Currency quoteCurrency, string source, LocalDate first, LocalDate last,
        CancellationToken cancellationToken);

    /// <summary>The rate published for the date, or the latest one before it; null when there is none.</summary>
    Task<FxRate?> LatestOnOrBeforeAsync(
        Currency baseCurrency, Currency quoteCurrency, string source, LocalDate day, CancellationToken cancellationToken);

    /// <summary>
    /// Adds all of it or nothing. <paramref name="added"/> are the first rates of their dates.
    /// <paramref name="accepted"/> are rates the owner accepted, recorded with the <paramref name="reason"/>:
    /// the next revision of a date that has a rate, or the first rate of a date that has none.
    /// </summary>
    /// <param name="added">The first rates of their dates.</param>
    /// <param name="accepted">Rates the owner accepted (see the summary).</param>
    /// <param name="reason">The owner's reason; required when anything was accepted.</param>
    /// <param name="fetchedAt">When the rates were read from the source.</param>
    /// <param name="note">What the update did, written to the audit trail in the same step — also when there are no rates to add.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    Task AddAsync(
        IReadOnlyList<FxRate> added, IReadOnlyList<FxRate> accepted, string? reason, Instant fetchedAt, FxRatesNote note,
        CancellationToken cancellationToken);
}

/// <summary>An entry for the audit trail about an update of the rates: who, what kind, and the details as JSON.</summary>
public sealed record FxRatesNote(string Actor, string Kind, string Payload)
{
    /// <summary>New rates were stored, nothing else.</summary>
    public const string Updated = "fx-rates.updated";

    /// <summary>Something the owner should know: a rate arrived late for an earlier date, or the source left a stored rate out.</summary>
    public const string Noted = "fx-rates.noted";

    /// <summary>The owner accepted a changed or an unusual rate.</summary>
    public const string ChangesAccepted = "fx-rates.changes-accepted";
}

/// <summary>A stored rate and what the source states for the same date now.</summary>
public sealed record FxRateChange(FxRate Stored, FxRate Stated);

/// <summary>
/// The owner's acceptance of what an update found: the <paramref name="Code"/> the finding was shown with, so
/// that exactly what was seen is accepted and nothing else, and the <paramref name="Reason"/> recorded with it.
/// </summary>
public sealed record FxAcceptance(string Code, string Reason);

/// <summary>
/// What an update of the rates did. <paramref name="Compared"/> rates of the last days were read again and
/// found unchanged. <paramref name="Revised"/> and <paramref name="Unusual"/> are what the owner accepted.
/// <paramref name="Late"/> are new rates for dates before the latest stored one: until now those dates used a
/// carried rate. <paramref name="NotRepeated"/> are stored rates the source left out of its answer; they stay.
/// </summary>
public sealed record FxRatesUpdate(
    int Added,
    int Compared,
    LocalDate Latest,
    IReadOnlyList<FxRateChange> Revised,
    IReadOnlyList<FxRate> Unusual,
    IReadOnlyList<FxRate> Late,
    IReadOnlyList<FxRate> NotRepeated);

/// <summary>
/// The update found something only the owner can decide about; nothing was stored. <see cref="Exception.Message"/>
/// says what; <see cref="Code"/> identifies exactly that finding, and accepting it takes the code.
/// </summary>
public sealed class FxRatesDisagreeException : Exception
{
    public FxRatesDisagreeException()
    {
    }

    public FxRatesDisagreeException(string message)
        : base(message)
    {
    }

    public FxRatesDisagreeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FxRatesDisagreeException(string message, string code)
        : base(message) => Code = code;

    public string Code { get; } = "";
}

/// <summary>
/// The official exchange rates the platform converts with (PLAN §6: the Bank of Israel representative rate).
/// They are loaded once from the start of the history and then kept up to date. A stored rate is never
/// overwritten: when the source states another one, the update stops until the owner accepts the change, and
/// the accepted rate is recorded next to the old one.
/// </summary>
public sealed class FxRates(IFxRateSource source, IFxRateStore store, IClock clock)
{
    /// <summary>History is kept from here: earlier than anything the owner can have bought.</summary>
    public static readonly LocalDate HistoryStart = new(2005, 1, 1);

    /// <summary>
    /// Every update reads this many days again and compares them with what is stored. It is also what makes
    /// an empty answer a fault: a window with stored rates in it cannot come back empty.
    /// </summary>
    public const int OverlapDays = 14;

    /// <summary>
    /// How old a carried-forward rate may be. The source's longest silence since 2005 is 7 days (Passover),
    /// so a rate is never rightly carried for more than 6; anything beyond 7 means the rates were not updated,
    /// and then there is no rate — not an old one. (Checked on the full history, 2026-10-12.)
    /// </summary>
    public const int MaxDaysCarried = 7;

    /// <summary>
    /// The largest move from one published rate to the next since 2005 is 3.76% (March 2020; checked on the
    /// full history, 2026-10-12). A new rate that moves more than this from the one before it is more likely
    /// a slip of the source than a rate, and a slip that got stored would stay: it is held back until the
    /// owner accepts it.
    /// </summary>
    public const decimal LargestUsualMove = 0.08m;

    /// <summary>
    /// Brings the stored rates up to today (the date in Israel). Running it again adds nothing.
    ///
    /// Two things stop an update without storing anything (<see cref="FxRatesDisagreeException"/>): the source
    /// states another rate for a stored date, or a new rate moves unusually far from the one before. The owner
    /// gets past that with <paramref name="accepted"/>, after looking at the source: it carries the code the
    /// finding was shown with. What the source says is read again, and only if it is still exactly that
    /// finding the changed rate becomes the next revision of its date and the unusual one is stored, each with
    /// the owner's reason. Anything else — another finding, or none — is refused: nobody accepts what they
    /// have not seen.
    ///
    /// A stored rate the source leaves out of its answer is kept and reported, and does not stop the update:
    /// nothing wrong can be stored because of it. An answer that repeats none of the stored rates of the last
    /// days cannot be compared at all and is a fault.
    /// </summary>
    public async Task<FxRatesUpdate> UpdateAsync(FxAcceptance? accepted, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var today = now.InZone(Zones.Israel).Date;
        var latest = await store.LatestOnOrBeforeAsync(
            source.BaseCurrency, source.QuoteCurrency, source.Name, today, cancellationToken);
        var start = latest is null ? HistoryStart : latest.Date.PlusDays(-OverlapDays);

        var fetched = await source.FetchAsync(start, today, cancellationToken);
        if (fetched.Count == 0)
        {
            throw new InvalidOperationException(
                $"{source.Name} returned no rate for {start:R} … {today:R}; at least the stored ones had to be there.");
        }

        var byDate = new Dictionary<LocalDate, FxRate>();
        foreach (var rate in fetched)
        {
            if (rate.From != source.BaseCurrency || rate.To != source.QuoteCurrency || rate.Source != source.Name
                || rate.Date < start || rate.Date > today || !byDate.TryAdd(rate.Date, rate))
            {
                throw new InvalidOperationException(Invariant(
                    $"{source.Name} returned something that was not asked for: {rate.From}/{rate.To} {rate.Rate} of {rate.Date:R} from '{rate.Source}' (asked: {start:R} … {today:R}, one rate a day)."));
            }
        }

        var stored = await store.ListAsync(
            source.BaseCurrency, source.QuoteCurrency, source.Name, start, today, cancellationToken);
        var storedDates = stored.Select(r => r.Date).ToHashSet();
        var changed = stored
            .Where(known => byDate.TryGetValue(known.Date, out var stated) && stated.Rate != known.Rate)
            .Select(known => new FxRateChange(known, byDate[known.Date]))
            .ToList();
        var notRepeated = stored.Where(known => !byDate.ContainsKey(known.Date)).ToList();
        if (stored.Count > 0 && notRepeated.Count == stored.Count)
        {
            throw new InvalidOperationException(
                $"{source.Name} repeated none of the {stored.Count} rates stored for {start:R} … {today:R}: " +
                "its answer cannot be compared with what is stored, so nothing of it was taken.");
        }

        var held = Held(fetched, stored, storedDates, tableIsEmpty: latest is null);
        var code = changed.Count + held.Count == 0 ? null : CodeOf(changed, held);
        if (accepted is null && code is not null)
        {
            throw new FxRatesDisagreeException(
                $"{Describe(changed, held)}. Nothing was stored. A stored rate is never overwritten: look at the " +
                $"source, and if it is right, accept exactly this (acceptance code {code}) - it is then recorded next " +
                "to the old rate.",
                code);
        }

        if (accepted is not null && code is null)
        {
            throw new InvalidOperationException(
                $"There is nothing to accept: {source.Name} and the stored rates agree. Run the plain update.");
        }

        if (accepted is not null && !string.Equals(accepted.Code.Trim(), code, StringComparison.OrdinalIgnoreCase))
        {
            throw new FxRatesDisagreeException(
                $"What {source.Name} says now is not what was accepted with code {accepted.Code.Trim()}: " +
                $"{Describe(changed, held)}. Nothing was stored. To accept this instead: acceptance code {code}.",
                code!);
        }

        var unusual = held.ConvertAll(h => h.Rate);
        var unusualDates = unusual.Select(r => r.Date).ToHashSet();
        var added = fetched.Where(r => !storedDates.Contains(r.Date)).ToList();
        var plain = added.Where(r => !unusualDates.Contains(r.Date)).ToList();
        List<FxRate> taken = [.. changed.Select(c => c.Stated), .. unusual];
        var update = new FxRatesUpdate(
            added.Count,
            stored.Count - changed.Count - notRepeated.Count,
            fetched.Max(r => r.Date),
            changed,
            unusual,
            added.Where(r => latest is not null && r.Date < latest.Date).ToList(),
            notRepeated);

        // Written down in the same step as the rates - by the daily job as much as by the owner's command:
        // a late or left-out rate changes or questions a figure of a past date, and nobody watches the job.
        if (added.Count > 0 || taken.Count > 0 || update.Late.Count > 0 || notRepeated.Count > 0)
        {
            await store.AddAsync(plain, taken, accepted?.Reason, now, NoteOf(update, accepted), cancellationToken);
        }

        return update;
    }

    /// <summary>
    /// The rate to use on a date: the one published for it or, on a day without one, the latest earlier rate,
    /// marked as carried forward — and as provisional while no later rate is stored, because then nothing
    /// shows that the source has moved past that date (it may yet publish for it, or the rates were simply not
    /// updated). There is no rate for a date after today, and none when the latest earlier one is more than
    /// <see cref="MaxDaysCarried"/> days old: the caller is told so instead of getting a stale rate.
    /// </summary>
    public async Task<AppliedFxRate> OnAsync(LocalDate date, CancellationToken cancellationToken)
    {
        var today = clock.GetCurrentInstant().InZone(Zones.Israel).Date;
        var pair = $"{source.BaseCurrency}/{source.QuoteCurrency}";
        if (date > today)
        {
            throw new InvalidOperationException($"There is no {pair} rate for {date:R} yet: today is {today:R}.");
        }

        var rate = await store.LatestOnOrBeforeAsync(
            source.BaseCurrency, source.QuoteCurrency, source.Name, date, cancellationToken);
        if (rate is null)
        {
            throw new InvalidOperationException(
                $"There is no {pair} rate ({source.Name}) on or before {date:R}. Update the rates first.");
        }

        var days = Period.DaysBetween(rate.Date, date);
        if (days > MaxDaysCarried)
        {
            throw new InvalidOperationException(
                $"The latest {pair} rate ({source.Name}) before {date:R} is of {rate.Date:R} - {days} days earlier. " +
                "A rate that old is not used: update the rates first.");
        }

        // The newest stored rate of all tells whether the source has published anything after the date.
        var newest = await store.LatestOnOrBeforeAsync(
            source.BaseCurrency, source.QuoteCurrency, source.Name, today, cancellationToken);
        return new AppliedFxRate(rate, date, isProvisional: days > 0 && newest!.Date <= rate.Date);
    }

    /// <summary>
    /// The new rates that are not taken without the owner: one that moves unusually far from the rate before
    /// it, and one that has no rate before it to be judged by. "Before it" is the nearest earlier rate the
    /// platform knows of — in the answer or stored — so an answer that leaves days out cannot carry a slip past
    /// this check. On the very first load there is nothing to compare the first rate with, and it is taken.
    /// </summary>
    private static List<(FxRate Rate, FxRate? Before)> Held(
        IReadOnlyList<FxRate> fetched, IReadOnlyList<FxRate> stored, HashSet<LocalDate> storedDates, bool tableIsEmpty)
    {
        var known = new SortedDictionary<LocalDate, FxRate>();
        foreach (var rate in stored.Concat(fetched))
        {
            known[rate.Date] = rate; // what the source states now wins over what is stored
        }

        var held = new List<(FxRate, FxRate?)>();
        FxRate? before = null;
        foreach (var rate in known.Values)
        {
            if (!storedDates.Contains(rate.Date))
            {
                var near = before is not null && Period.DaysBetween(before.Date, rate.Date) <= MaxDaysCarried;
                if (near && Math.Abs((rate.Rate / before!.Rate) - 1m) > LargestUsualMove)
                {
                    held.Add((rate, before));
                }
                else if (!near && !tableIsEmpty)
                {
                    held.Add((rate, null));
                }
            }

            before = rate;
        }

        return held;
    }

    private string Describe(List<FxRateChange> changed, List<(FxRate Rate, FxRate? Before)> held)
    {
        var findings = new List<string>();
        findings.AddRange(changed.Select(c => Invariant(
            $"{source.Name} now states {c.Stated.Rate:0.######} for {c.Stored.Date:R}; {c.Stored.Rate:0.######} is stored")));
        findings.AddRange(held.Select(h => h.Before is { } before
            ? Invariant($"the new rate {h.Rate.Rate:0.######} of {h.Rate.Date:R} moves more than {LargestUsualMove * 100m:0}% from {before.Rate:0.######} of {before.Date:R}")
            : Invariant($"the new rate {h.Rate.Rate:0.######} of {h.Rate.Date:R} cannot be judged: no rate of the {MaxDaysCarried} days before it is known")));
        return string.Join("; ", findings);
    }

    /// <summary>
    /// A short code of exactly these findings — the dates and the rates on both sides. The owner passes it back
    /// to accept them; if the source says anything else by then, the code no longer fits.
    /// </summary>
    private static string CodeOf(List<FxRateChange> changed, List<(FxRate Rate, FxRate? Before)> held)
    {
        var text = new StringBuilder();
        foreach (var change in changed.OrderBy(c => c.Stored.Date))
        {
            text.Append(Invariant($"changed {change.Stored.Date:R} {change.Stored.Rate:0.######} {change.Stated.Rate:0.######};"));
        }

        foreach (var (rate, before) in held.OrderBy(h => h.Rate.Date))
        {
            text.Append(Invariant($"unusual {rate.Date:R} {rate.Rate:0.######} after {before?.Date:R} {before?.Rate:0.######};"));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..8];
    }

    private FxRatesNote NoteOf(FxRatesUpdate update, FxAcceptance? accepted)
    {
        static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var remarks = new List<string>();
        remarks.AddRange(update.Late.Select(r => Invariant(
            $"{r.Rate:0.######} for {r.Date:R} arrived after later days were stored; until then that date used a carried rate")));
        remarks.AddRange(update.NotRepeated.Select(r => Invariant(
            $"the source did not repeat the stored rate of {r.Date:R} ({r.Rate:0.######}); it is kept as it is")));
        var kind = accepted is not null ? FxRatesNote.ChangesAccepted
            : remarks.Count > 0 ? FxRatesNote.Noted
            : FxRatesNote.Updated;
        var payload = JsonSerializer.Serialize(new
        {
            source = source.Name,
            added = update.Added,
            compared = update.Compared,
            latest = Iso(update.Latest),
            reason = accepted?.Reason.Trim(),
            code = accepted?.Code.Trim(),
            revised = update.Revised.Select(c => new { date = Iso(c.Stored.Date), was = c.Stored.Rate, now = c.Stated.Rate }),
            unusual = update.Unusual.Select(r => new { date = Iso(r.Date), rate = r.Rate }),
            late = update.Late.Select(r => new { date = Iso(r.Date), rate = r.Rate }),
            notRepeated = update.NotRepeated.Select(r => new { date = Iso(r.Date), rate = r.Rate }),
            remarks,
        });
        return new FxRatesNote(accepted is null ? FxRatesJob.JobName : "owner", kind, payload);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Keeps the exchange rates up to date, once per session. The job is due at 11:00 New York — 18:00 in Israel
/// (17:00 in the weeks when only one of the two countries has changed its clocks), after the bank's afternoon
/// publication. The exact hour of publication is not documented here; on 2026-10-09, a Friday, the day's rate
/// was out by 12:21 Israel time. A rate that is late, and a day the source skips, simply add nothing; the next
/// session brings it. A source that cannot be reached is tried again every hour for as long as it takes.
/// </summary>
public sealed class FxRatesJob(FxRates rates) : IDailyJob
{
    public const string JobName = "fx-rates";

    public string Name => JobName;

    public LocalTime DueAt => new(11, 0);

    public Task RunAsync(TradeDate day, CancellationToken cancellationToken) => rates.UpdateAsync(null, cancellationToken);
}
