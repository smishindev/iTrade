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
/// One of IBKR's figures beside the ledger's: the cash of a currency or the shares of a contract, at a moment.
/// <paramref name="AllCurrencies"/>: IBKR gave no row per currency, so its figure is the cash of every currency told
/// in the base currency - it agrees with the ledger's base currency only while the ledger holds nothing else, and it
/// cannot tell a currency missing at IBKR from one translated into the total.
/// </summary>
public sealed record FlexComparison(string When, string What, decimal Ledger, decimal Ibkr, bool AllCurrencies = false)
{
    public bool Agrees => Ledger == Ibkr;
}

/// <summary>
/// The ledger held against a statement it already imported (<see cref="FlexImport.ReconcileAsync"/>): every figure
/// compared, with both numbers, and every difference in words - a figure that differs, a line of the statement the
/// ledger does not hold or holds otherwise, what the statement holds that the import cannot read. Nothing repaired.
/// </summary>
public sealed record FlexReconciliation(
    FlexAccount Account, LocalDate From, LocalDate To, IReadOnlyList<FlexComparison> Compared, IReadOnlyList<string> Differences)
{
    public bool Agrees => Differences.Count == 0;
}

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
        var catalogue = await CatalogueAsync(statement, cancellationToken);
        var built = FlexLedgerEntries.From(statement, placeId, catalogue, clock.GetCurrentInstant());
        if (built.Refusals.Count > 0)
        {
            return Refused([.. built.Refusals]);
        }

        // What the ledger itself would add - its own rules decide, a line it would leave out (held differently) or
        // record again as the next revision (its earlier recording reversed) included - held against IBKR's figures.
        var held = place is null ? [] : await transactions.ListAsync(place.Id, cancellationToken);
        var wouldAdd = await ledger.PlanAsync(built.Entries, cancellationToken);
        var against = await AgainstTheLedgerAsync(first, last, placeId, held, wouldAdd.ToAdd, catalogue, WithThisStatement, null, cancellationToken);
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
                plan.First, plan.Last, place.Id, now, [], new Dictionary<int, Instrument>(plan.Catalogue), WithThisStatement, null, cancellationToken);
            return new FlexRecorded(result, disagreements);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new FlexRecorded(result, [$"The entries were recorded, but the ledger could not be checked against IBKR afterwards: {e.Message}"]);
        }
    }

    /// <summary>
    /// The ledger as it is now held against a statement, as late as wanted after its import: the same figures the
    /// import checked (the cash on the evening before its first day; the cash of every currency and the shares of
    /// every contract after its last day), each given with both numbers; the lines of the statement the ledger does
    /// not hold (a reversal since, a statement never imported) or holds otherwise; the entries of the ledger on the
    /// statement's days that it does not state. Records nothing, repairs nothing.
    /// </summary>
    /// <param name="knownAccount">The account of the statements imported, or null when none was.</param>
    public async Task<FlexReconciliation> ReconcileAsync(
        FlexStatement statement, FlexAccount? knownAccount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var first = statement.Periods.MinBy(p => p.From)!;
        var last = statement.Periods.MaxBy(p => p.To)!;
        var accounts = statement.Accounts;
        var account = accounts.Count == 1
            ? FlexAccount.Of(accounts[0])
            : new FlexAccount("", string.Join(", ", accounts.Select(FlexAccount.Mask)));
        FlexReconciliation Cannot(string why) => new(account, first.From, last.To, [], [why]);

        if (accounts.Count != 1)
        {
            return Cannot($"The statement speaks of {accounts.Count} accounts ({account.Label}); it has to be of one.");
        }

        if (FlexAccount.IsPaper(accounts[0]))
        {
            return Cannot($"{account.Label} is a paper (demo) account; the ledger keeps the real account's books. Reconcile with a statement of the real account.");
        }

        var place = await places.FindByNameAsync(HoldingPlaceName, cancellationToken);
        if (knownAccount is null || place is null)
        {
            return Cannot("No statement of IBKR has been imported: there are no books of IBKR to hold this one against. Import it first.");
        }

        if (knownAccount.Key != account.Key)
        {
            return Cannot($"The ledger keeps the books of {knownAccount.Label}; this statement is of {account.Label}.");
        }

        var catalogue = await CatalogueAsync(statement, cancellationToken);
        var built = FlexLedgerEntries.From(statement, place.Id, catalogue, clock.GetCurrentInstant());
        var differences = built.Refusals
            .Select(why => $"The statement holds what the import does not read, so the ledger cannot hold it: {why}")
            .ToList();
        var plan = await ledger.PlanAsync(built.Entries, cancellationToken);
        foreach (var missing in plan.ToAdd)
        {
            differences.Add(Invariant(
                $"IBKR's line {missing.ExternalId} ({missing.Kind} of {missing.TradeDate:R}, {Plain(missing.Cash.Amount)} {missing.Cash.Currency}) is not in the ledger: the statement was not imported, or its recording was reversed - import the statement."));
        }

        differences.AddRange(plan.NotRecorded.Select(why => $"The ledger does not hold IBKR's line as stated: {why}"));
        var held = await transactions.ListAsync(place.Id, cancellationToken);

        // The other way: what the ledger holds on the days of the statement and the statement does not state - an
        // entry typed in by hand, a line IBKR no longer states. Entries that cancel each other leave both ends whole.
        var stated = built.Entries.Select(e => e.ExternalId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var reversed = held.Select(e => e.Reverses).OfType<TransactionId>().ToHashSet();
        bool OnItsDays(LocalDate day) => statement.Periods.Any(p => p.From <= day && day <= p.To);
        foreach (var extra in held.Where(e => e.Reverses is null && !reversed.Contains(e.Id) && OnItsDays(e.TradeDate)
            && !(e.Source == TransactionSource.Flex && e.ExternalId is { } id && stated.Contains(id))))
        {
            var shares = extra.QuantityChange == 0m ? "" : Invariant($", {Plain(extra.QuantityChange)} shares");
            var line = extra.ExternalId is { } external ? $" {external}" : "";
            // With rows the import cannot read, IBKR may state the line in one of them (a cancellation, say).
            var notStated = built.Refusals.Count == 0 ? "does not state" : "does not state in a form the import reads";
            differences.Add(Invariant(
                $"The ledger holds a {extra.Kind} of {extra.TradeDate:R}, {Plain(extra.Cash.Amount)} {extra.Cash.Currency}{shares} ({extra.Source}{line}; entry {extra.Id}), that IBKR's statement {notStated}: if it is a mistake, it goes out with a reversal (the owner's step)."));
        }

        var compared = new List<FlexComparison>();
        differences.AddRange(await AgainstTheLedgerAsync(first, last, place.Id, held, [], catalogue, "the ledger", compared, cancellationToken));
        return new FlexReconciliation(account, first.From, last.To, compared, differences);
    }

    private const string WithThisStatement = "the ledger with this statement";

    /// <summary>The catalogue's instruments of every contract the statement names, by IBKR's contract id.</summary>
    private async Task<Dictionary<int, Instrument>> CatalogueAsync(FlexStatement statement, CancellationToken cancellationToken)
    {
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

        return catalogue;
    }

    /// <summary>
    /// Where the statement and the ledger disagree: the cash on the evening before its first day, and the cash of
    /// every currency and the shares of every contract after its last day, with the entries the ledger would add.
    /// Every figure compared goes into <paramref name="compared"/> when it is given, agreeing or not.
    /// </summary>
    private async Task<List<string>> AgainstTheLedgerAsync(
        FlexPeriod first, FlexPeriod last, HoldingPlaceId place, IReadOnlyList<Transaction> held,
        IReadOnlyList<Transaction> fresh, Dictionary<int, Instrument> catalogue, string ledgerName,
        List<FlexComparison>? compared, CancellationToken cancellationToken)
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
            first.BaseSummary?.StartingCash, opening, place, baseCurrency, problems, compared,
            "the ledger was not brought up to the day before this statement: a statement between the last import and this one is missing, or the history before this statement was never imported - download a period that starts where the ledger ends");
        CompareCash(
            Invariant($"the end of {last.To:R}"), last.PerCurrency.ToDictionary(r => r.Currency, r => r.EndingCash, StringComparer.Ordinal),
            last.BaseSummary?.EndingCash, closing, place, baseCurrency, problems, compared,
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
            var name = catalogue.TryGetValue(conId, out var known) ? known.Ticker.Value
                : conId == 0 ? "instruments without an IBKR contract id"
                : Invariant($"contract {conId}");
            compared?.Add(new FlexComparison(Invariant($"the end of {last.To:R}"), $"shares of {name}", mine, theirs));
            if (theirs != mine)
            {
                problems.Add(Invariant(
                    $"At the end of {last.To:R} IBKR holds {Plain(theirs)} of {name} and {ledgerName} {Plain(mine)}: shares moved without cash (a split, a transfer) or a trade is missing."));
            }
        }

        return problems;
    }

    private static void CompareCash(
        string when, Dictionary<string, decimal> perCurrency, decimal? baseSummary, PortfolioBooks books,
        HoldingPlaceId place, string baseCurrency, List<string> problems, List<FlexComparison>? compared, string why)
    {
        var ours = books.Cash.Where(c => c.HoldingPlace == place && c.Amount.Amount != 0m)
            .ToDictionary(c => c.Amount.Currency.Code, c => c.Amount.Amount, StringComparer.Ordinal);
        var allCurrencies = perCurrency.Count == 0;
        if (allCurrencies)
        {
            // Without rows per currency IBKR's sum is in the base currency, the others translated into it: it can
            // stand only for books that hold nothing but the base currency.
            if (baseSummary is not { } total || ours.Keys.Any(c => c != baseCurrency))
            {
                var holds = ours.Count == 0 ? "no cash" : string.Join(", ", ours.Keys);
                problems.Add($"IBKR's Cash Report of {when} has no row per currency, and the ledger holds {holds}: the cash cannot be held against it. Download the statement again (IBKR sometimes leaves those rows out) and import that one.");
                return;
            }

            perCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal) { [baseCurrency] = total };
        }

        foreach (var code in perCurrency.Keys.Union(ours.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var (theirs, mine) = (perCurrency.GetValueOrDefault(code), ours.GetValueOrDefault(code));
            compared?.Add(allCurrencies
                ? new FlexComparison(when, $"cash of all currencies told in {code} (IBKR gave no row per currency)", mine, theirs, AllCurrencies: true)
                : new FlexComparison(when, $"cash {code}", mine, theirs));
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
