using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ITrade.Application.Portfolio;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Broker.IBKR.Flex;

/// <summary>
/// An IBKR account as the platform keeps it: a key that tells two accounts apart (a SHA-256 hash of the number)
/// and a label to show (`U…123`). The key is not a secret: there are few account numbers, and trying them all
/// gives the number back. It keeps the number out of what is shown and logged, nothing more; the database and its
/// backups are the owner's, like the statement files of the data folder that carry the number in plain text.
/// </summary>
public sealed record FlexAccount(string Key, string Label)
{
    public static FlexAccount Of(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var number = account.Trim().ToUpperInvariant();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"itrade-ibkr-account:{number}")));
        return new FlexAccount(key, Mask(number));
    }

    /// <summary>Paper and demo accounts of IBKR start with a D (DU…, DF…, DI…); real ones with U, F or I.</summary>
    public static bool IsPaper(string account) => account.Trim().StartsWith('D');

    /// <summary>The first letter and the last three characters: `U…123`.</summary>
    public static string Mask(string account) => account.Length <= 4 ? "…" : $"{account[0]}…{account[^3..]}";
}

/// <summary>
/// What an import would record, or why it records nothing (<see cref="Refusals"/>). Built by
/// <see cref="FlexImport.PrepareAsync"/>, recorded by <see cref="FlexImport.RecordAsync"/>.
/// </summary>
public sealed record FlexImportPlan(
    FlexAccount Account,
    LocalDate From,
    LocalDate To,
    IReadOnlyList<Transaction> Entries,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<LocalDate> DaysNotChecked,
    IReadOnlyList<LocalDate> CheckedOnlyAsAWhole,
    HoldingPlace? PlaceToCreate)
{
    public bool CanBeRecorded => Refusals.Count == 0;

    internal FlexPeriod? First { get; init; }

    internal FlexPeriod? Last { get; init; }

    internal IReadOnlyDictionary<int, Instrument> Catalogue { get; init; } = new Dictionary<int, Instrument>();
}

/// <summary>
/// What recording did: the ledger's own account of it, and where the ledger as it now is still disagrees with
/// IBKR's cash and positions at the end of the statement — empty when it agrees, as it should.
/// </summary>
public sealed record FlexRecorded(LedgerResult Ledger, IReadOnlyList<string> Disagreements);

/// <summary>
/// Records an IBKR Flex statement in the ledger of the holding place <see cref="HoldingPlaceName"/>, through
/// <see cref="TransactionLedger"/>: as often as wanted, a line is recorded once. Before anything is recorded the
/// statement is held against the ledger: the cash the ledger holds the evening before its first day has to be the
/// cash IBKR starts with, and after its last day the ledger with the new entries has to hold IBKR's cash of every
/// currency and IBKR's positions. A gap between two downloads, a line that is not read, a split or a transfer
/// that moved shares without cash — each shows as a difference, and nothing is recorded. A statement of a paper
/// (demo) account is never recorded: its money is not the owner's, and the ledger keeps every entry for good.
/// </summary>
public sealed class FlexImport(
    IInstrumentStore instruments, IHoldingPlaceStore places, ITransactionStore transactions, TransactionLedger ledger,
    IClock clock)
{
    public const string HoldingPlaceName = "IBKR";

    /// <param name="statement">The statement as read from the file.</param>
    /// <param name="knownAccount">
    /// The account of the statements imported before, or null for the first one. A statement of another account is
    /// refused: the holding place is one account.
    /// </param>
    public async Task<FlexImportPlan> PrepareAsync(
        FlexStatement statement, FlexAccount? knownAccount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var first = statement.Periods.MinBy(p => p.From)!;
        var last = statement.Periods.MaxBy(p => p.To)!;
        var accounts = statement.Accounts;
        var account = accounts.Count == 1
            ? FlexAccount.Of(accounts[0])
            : new FlexAccount("", string.Join(", ", accounts.Select(FlexAccount.Mask)));
        FlexImportPlan Refused(params string[] why) => new(account, first.From, last.To, [], why, [], [], null);

        if (accounts.Count != 1)
        {
            return Refused($"The statement speaks of {accounts.Count} accounts ({account.Label}); it has to be of one.");
        }

        if (FlexAccount.IsPaper(accounts[0]))
        {
            return Refused(
                $"{account.Label} is a paper (demo) account. Its statements are never recorded in the owner's books: the money " +
                "is not real and the ledger keeps every entry for good. Download the statement of the real account.");
        }

        if (knownAccount is not null && knownAccount.Key != account.Key)
        {
            return Refused($"The statements imported so far are of {knownAccount.Label}; this one is of {account.Label}.");
        }

        var place = await places.FindByNameAsync(HoldingPlaceName, cancellationToken);
        var placeId = place?.Id ?? HoldingPlaceId.New();
        var catalogue = new Dictionary<int, Instrument>();
        var conIds = statement.Periods
            .SelectMany(p => p.Trades.Select(t => t.ConId).Concat(p.Cash.Select(c => (int?)c.ConId)).Concat(p.OpenPositions.Select(o => (int?)o.ConId)))
            .OfType<int>()
            .Distinct();
        foreach (var conId in conIds)
        {
            if (await instruments.FindByConIdAsync(conId, cancellationToken) is { } instrument)
            {
                catalogue[conId] = instrument;
            }
        }

        var built = FlexLedgerEntries.From(statement, placeId, catalogue, clock.GetCurrentInstant());
        if (built.Refusals.Count > 0)
        {
            return Refused([.. built.Refusals]);
        }

        // What the ledger itself would add - its own rules decide, a line it would leave out (held differently) or
        // record again as the next revision (its earlier recording reversed) included - held against IBKR's figures.
        var held = place is null ? [] : await transactions.ListAsync(place.Id, cancellationToken);
        var wouldAdd = await ledger.PlanAsync(built.Entries, cancellationToken);
        var against = await AgainstTheLedgerAsync(first, last, placeId, held, wouldAdd.ToAdd, catalogue, cancellationToken);
        if (against.Count > 0)
        {
            return Refused([.. against, .. wouldAdd.NotRecorded.Select(why => $"The ledger would leave out {why}")]);
        }

        var create = place is null && built.Entries.Count > 0
            ? new HoldingPlace(placeId, HoldingPlaceName, HoldingPlaceKind.Broker, IntegrationKind.Broker)
            : null;
        return new FlexImportPlan(account, first.From, last.To, built.Entries, [], built.DaysNotChecked, built.CheckedOnlyAsAWhole, create)
        {
            First = first,
            Last = last,
            Catalogue = catalogue,
        };
    }

    /// <summary>
    /// Records a plan that can be recorded; what the ledger already holds is recognised and left as it is. Then
    /// the ledger is read again and held against IBKR's figures of the statement's ends: what was written, not what
    /// was meant to be.
    /// </summary>
    public async Task<FlexRecorded> RecordAsync(FlexImportPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanBeRecorded || plan.First is null || plan.Last is null)
        {
            throw new InvalidOperationException("Only a plan PrepareAsync made, without refusals, is recorded.");
        }

        if (plan.PlaceToCreate is { } create)
        {
            await places.AddAsync(create, cancellationToken);
        }

        var result = plan.Entries.Count == 0
            ? new LedgerResult(0, 0, [])
            : await ledger.RecordAsync(plan.Entries, cancellationToken);
        if (await places.FindByNameAsync(HoldingPlaceName, cancellationToken) is not { } place)
        {
            return new FlexRecorded(result, []); // nothing was recorded, and there was nothing to hold
        }

        // From here on the entries are in the ledger: a check that fails is said as that, not as "not recorded".
        try
        {
            var now = await transactions.ListAsync(place.Id, cancellationToken);
            var disagreements = await AgainstTheLedgerAsync(
                plan.First, plan.Last, place.Id, now, [], new Dictionary<int, Instrument>(plan.Catalogue), cancellationToken);
            return new FlexRecorded(result, disagreements);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new FlexRecorded(result, [$"The entries were recorded, but the ledger could not be checked against IBKR afterwards: {e.Message}"]);
        }
    }

    /// <summary>
    /// Where the statement and the ledger disagree: the cash on the evening before its first day, and the cash of
    /// every currency and the shares of every contract after its last day, with the entries the ledger would add.
    /// </summary>
    private async Task<List<string>> AgainstTheLedgerAsync(
        FlexPeriod first, FlexPeriod last, HoldingPlaceId place, IReadOnlyList<Transaction> held,
        IReadOnlyList<Transaction> fresh, Dictionary<int, Instrument> catalogue, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var before = first.From.PlusDays(-1);

        // The ledger's order: by trade date, then as recorded - new entries come after the stored ones of their day.
        var projected = held.Select((e, i) => (Entry: e, Order: i))
            .Concat(fresh.Select((e, i) => (Entry: e, Order: held.Count + i)))
            .OrderBy(x => x.Entry.TradeDate)
            .ThenBy(x => x.Order)
            .Select(x => x.Entry)
            .ToList();
        PortfolioBooks opening, closing;
        try
        {
            opening = PortfolioBooks.From(held, before);
            closing = PortfolioBooks.From(projected, last.To);
        }
        catch (InvalidOperationException e)
        {
            return [$"With this statement the books cannot be worked out: {e.Message}"];
        }

        var baseCurrency = first.BaseCurrency.Length > 0 ? first.BaseCurrency : last.BaseCurrency;
        CompareCash(
            Invariant($"the evening of {before:R}"), first.PerCurrency.ToDictionary(r => r.Currency, r => r.StartingCash, StringComparer.Ordinal),
            first.BaseSummary?.StartingCash, opening, place, baseCurrency, problems,
            "the ledger was not brought up to the day before this statement: a statement between the last import and this one is missing, or the history before this statement was never imported - download a period that starts where the ledger ends");
        CompareCash(
            Invariant($"the end of {last.To:R}"), last.PerCurrency.ToDictionary(r => r.Currency, r => r.EndingCash, StringComparer.Ordinal),
            last.BaseSummary?.EndingCash, closing, place, baseCurrency, problems,
            "something that moved cash is not in the ledger or not in this statement");

        // Shares: IBKR's positions at the end of the last day against the books with this statement.
        var conIdOf = catalogue.Values.ToDictionary(i => i.Id, i => i.IbkrConId);
        foreach (var position in closing.Positions.Where(p => p.HoldingPlace == place && !conIdOf.ContainsKey(p.Instrument)))
        {
            conIdOf[position.Instrument] = (await instruments.FindAsync(position.Instrument, cancellationToken))?.IbkrConId;
        }

        foreach (var other in last.OpenPositions.Where(p => p.Category != "STK"))
        {
            problems.Add(Invariant($"IBKR holds {Plain(other.Quantity)} of {other.Symbol} ({other.Category}) at the end of {last.To:R}; only shares and funds have been seen yet."));
        }

        var ibkr = last.OpenPositions.Where(p => p.Category == "STK").GroupBy(p => p.ConId).ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity));
        // An instrument without a contract id (0) cannot be IBKR's, and is reported as such.
        var ours = closing.Positions.Where(p => p.HoldingPlace == place)
            .GroupBy(p => conIdOf.GetValueOrDefault(p.Instrument) ?? 0)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity.Value));
        foreach (var conId in ibkr.Keys.Union(ours.Keys).Order())
        {
            var (theirs, mine) = (ibkr.GetValueOrDefault(conId), ours.GetValueOrDefault(conId));
            if (theirs != mine)
            {
                var name = catalogue.TryGetValue(conId, out var known) ? known.Ticker.Value : Invariant($"contract {conId}");
                problems.Add(Invariant(
                    $"At the end of {last.To:R} IBKR holds {Plain(theirs)} of {name} and the ledger with this statement {Plain(mine)}: shares moved without cash (a split, a transfer) or a trade is missing."));
            }
        }

        return problems;
    }

    private static void CompareCash(
        string when, Dictionary<string, decimal> perCurrency, decimal? baseSummary, PortfolioBooks books,
        HoldingPlaceId place, string baseCurrency, List<string> problems, string why)
    {
        var ours = books.Cash.Where(c => c.HoldingPlace == place && c.Amount.Amount != 0m)
            .ToDictionary(c => c.Amount.Currency.Code, c => c.Amount.Amount, StringComparer.Ordinal);
        if (perCurrency.Count == 0)
        {
            // Without rows per currency IBKR's sum is in the base currency, the others translated into it: it can
            // stand only for books that hold nothing but the base currency.
            if (baseSummary is not { } total || ours.Keys.Any(c => c != baseCurrency))
            {
                problems.Add($"IBKR's Cash Report of {when} has no row per currency, and the ledger holds {string.Join(", ", ours.Keys)}: the cash cannot be held against it. Download the statement again (IBKR sometimes leaves those rows out) and import that one.");
                return;
            }

            perCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal) { [baseCurrency] = total };
        }

        foreach (var code in perCurrency.Keys.Union(ours.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var (theirs, mine) = (perCurrency.GetValueOrDefault(code), ours.GetValueOrDefault(code));
            if (theirs != mine)
            {
                problems.Add(Invariant($"At {when} IBKR's cash is {Plain(theirs)} {code} and the ledger's {Plain(mine)} {code}: {why}."));
            }
        }
    }

    /// <summary>A number as it is written, without the trailing zeros of the column it was stored in.</summary>
    private static string Plain(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
