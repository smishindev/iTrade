using System.Globalization;
using ITrade.Domain;
using NodaTime;

namespace ITrade.Broker.IBKR.Flex;

/// <summary>
/// The ledger entries of a statement, or why it cannot be recorded. A statement with any refusal is recorded
/// not at all: a missing line would leave the books wrong while looking complete. <paramref name="DaysNotChecked"/>
/// are the periods whose cash IBKR reported only in the base currency, so the cash of each currency could not be
/// held against it day by day (the import then holds the whole statement against its first and last day).
/// <paramref name="CheckedOnlyAsAWhole"/> are the periods of several days (a query without Breakout by Day) whose
/// cash was held against IBKR's only for the period, not for each of its days; both lists name a period by its first day.
/// </summary>
public sealed record FlexEntries(
    IReadOnlyList<Transaction> Entries,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<LocalDate> DaysNotChecked,
    IReadOnlyList<LocalDate> CheckedOnlyAsAWhole);

/// <summary>
/// Turns the rows of a Flex statement into ledger entries (docs/roadmap/P3-accounting.md, P3.2.01 and P3.2.02).
/// <list type="bullet">
/// <item>Every execution is its own entry, known by its `transactionID`; its commission is a fee linked to it
/// (`&lt;id&gt;/commission`). One order filled on several exchanges is several entries and several lots, as IBKR
/// books it: the first execution carries IBKR's minimum commission, in the platform as in IBKR's own lots.</item>
/// <item>A currency conversion is two linked legs, the base currency first (`&lt;id&gt;`, then
/// `&lt;id&gt;/&lt;currency&gt;`), its commission a fee linked to the first leg.</item>
/// <item>Cash transactions: deposits and withdrawals, ordinary dividends, the tax withheld from them (linked to
/// the dividend by IBKR's `actionID`), interest, other fees. The summary rows without an id are skipped.</item>
/// <item>Within a day: deposits and withdrawals, then the trades in the order they were executed, then dividends,
/// then the other cash transactions.</item>
/// </list>
/// Anything the import has not seen on a real statement yet — a cancelled or corrected trade, a note on a trade
/// other than a partial fill, a corporate action, a transfer of shares, a dividend that is not an ordinary one,
/// another kind of cash transaction, a row booked on another day than it is dated (but for a deposit booked a few
/// days after its day, which the ledger takes on the day of the booking) — is refused and named, not
/// guessed. A period that lacks a section the import needs is refused. And on every day IBKR reports its cash per
/// currency, the cash has to come out as its `CashReport` says: what is not read cannot hide.
/// </summary>
public static class FlexLedgerEntries
{
    /// <summary>How far the cash of a trade may be from shares × price: IBKR rounds proceeds to the cent.</summary>
    public const decimal ProceedsTolerance = 0.01m;

    /// <summary>Notes on an execution seen on a real statement: none, or P (a partial fill of the order).</summary>
    public static readonly IReadOnlySet<string> NotesSeen = new HashSet<string>(StringComparer.Ordinal) { "", "P" };

    /// <summary>How IBKR describes the only kind of dividend the import records.</summary>
    public const string OrdinaryDividend = "(Ordinary Dividend)";

    /// <summary>
    /// How many days after its date a deposit may be booked: one was seen; four cover a weekend and a holiday.
    /// Widened only when a real statement shows more.
    /// </summary>
    public const int MostDaysBookedLater = 4;

    private const string Execution = "EXECUTION";
    private const string ExchangeTrade = "ExchTrade";

    public static FlexEntries From(
        FlexStatement statement, HoldingPlaceId place, IReadOnlyDictionary<int, Instrument> instruments, Instant recordedAt)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(instruments);
        var entries = new List<Transaction>();
        var refusals = new List<string>();
        var notChecked = new List<LocalDate>();
        var asAWhole = new List<LocalDate>();
        var dividends = new Dictionary<string, List<Transaction>>(StringComparer.Ordinal); // actionID → dividends
        foreach (var period in statement.Periods)
        {
            var day = new Day(period, place, instruments, recordedAt, dividends);
            day.Build();
            entries.AddRange(day.Entries);
            refusals.AddRange(day.Refusals);
            if (!day.CashChecked)
            {
                notChecked.Add(period.From);
            }
            else if (period.From != period.To)
            {
                asAWhole.Add(period.From);
            }
        }

        return new FlexEntries(entries, refusals, notChecked, asAWhole);
    }

    /// <summary>The cash each currency moved by the entries of a set.</summary>
    public static IReadOnlyDictionary<string, decimal> CashMoved(IEnumerable<Transaction> entries) =>
        entries.GroupBy(e => e.Cash.Currency.Code).ToDictionary(g => g.Key, g => g.Sum(e => e.Cash.Amount), StringComparer.Ordinal);

    /// <summary>The entries of one statement period, built in the ledger's order.</summary>
    private sealed class Day(
        FlexPeriod period,
        HoldingPlaceId place,
        IReadOnlyDictionary<int, Instrument> instruments,
        Instant recordedAt,
        Dictionary<string, List<Transaction>> dividends)
    {
        private readonly string _where = period.From == period.To
            ? Invariant($"{period.From:R}")
            : Invariant($"{period.From:R} to {period.To:R}");

        public List<Transaction> Entries { get; } = [];

        public List<string> Refusals { get; } = [];

        public bool CashChecked { get; private set; }

        public void Build()
        {
            foreach (var section in period.Missing)
            {
                Refuse($"the section {section} is not in the statement; the Flex query has to include it (even empty), or shares or money could pass unseen");
            }

            foreach (var section in period.Unread)
            {
                Refuse($"the section {section} has rows; the import does not read them yet (never seen on a statement of this account)");
            }

            var cash = period.Cash.Where(c => c.LevelOfDetail != "SUMMARY").ToList();
            foreach (var line in cash.Where(c => c.Type == "Deposits/Withdrawals"))
            {
                Cash(line);
            }

            // In the order they were executed: a sale has to come after the purchase it sells.
            var trades = period.Trades
                .OrderBy(t => t.ExecutedAt)
                .ThenBy(t => t.TransactionId.Length)
                .ThenBy(t => t.TransactionId, StringComparer.Ordinal);
            foreach (var trade in trades)
            {
                Trade(trade);
            }

            foreach (var line in cash.Where(c => c.Type == "Dividends"))
            {
                Cash(line);
            }

            foreach (var line in cash.Where(c => c.Type is not ("Deposits/Withdrawals" or "Dividends")))
            {
                Cash(line);
            }

            CheckCash();
        }

        private void Trade(FlexTrade trade)
        {
            var label = $"trade {trade.TransactionId} ({trade.Symbol})";
            if (trade.LevelOfDetail != Execution)
            {
                Refuse($"{label} is at the level '{trade.LevelOfDetail}'; the Trades section of the Flex query has to be at the level of executions");
                return;
            }

            if (trade.TransactionType != ExchangeTrade || !trade.Notes.Split(';').All(NotesSeen.Contains))
            {
                Refuse($"{label} is a '{trade.TransactionType}' (notes '{trade.Notes}'); only plain executions, or partial fills (P), have been seen yet - a cancelled or corrected trade is not guessed");
                return;
            }

            if (trade.TradeDate < period.From || trade.TradeDate > period.To)
            {
                Refuse(Invariant($"{label} is of {trade.TradeDate:R}, outside the period it is listed in"));
                return;
            }

            try
            {
                switch (trade.Category)
                {
                    case "STK":
                        Share(trade, label);
                        break;
                    case "CASH":
                        Conversion(trade, label);
                        break;
                    default:
                        Refuse($"{label} is of the asset category '{trade.Category}'; the import reads shares and funds (STK) and conversions (CASH)");
                        break;
                }
            }
            catch (ArgumentException e)
            {
                // An unsupported currency, more decimals than the ledger keeps, an impossible sign.
                Refuse($"{label}: {e.Message}");
            }
        }

        private void Share(FlexTrade trade, string label)
        {
            if (Instrument(trade.ConId, trade.Isin, label) is not { } instrument)
            {
                return;
            }

            var currency = Currency.Of(trade.Currency);
            if (instrument.Currency != currency)
            {
                Refuse($"{label} is in {currency}; {instrument.Ticker} is in {instrument.Currency} in the catalogue");
                return;
            }

            var buy = trade.BuySell == "BUY";
            if (!(buy ? trade.Quantity > 0m && trade.Proceeds < 0m : trade.BuySell == "SELL" && trade.Quantity < 0m && trade.Proceeds > 0m))
            {
                Refuse(Invariant($"{label}: '{trade.BuySell}' of {trade.Quantity} shares for {trade.Proceeds} does not add up"));
                return;
            }

            if (Math.Abs(trade.Proceeds + (trade.Quantity * trade.TradePrice)) > ProceedsTolerance)
            {
                Refuse(Invariant($"{label}: {trade.Quantity} x {trade.TradePrice} is not the cash of the trade, {trade.Proceeds}"));
                return;
            }

            var commission = Currency.Of(trade.CommissionCurrency);
            if (commission != currency)
            {
                // The books keep a commission with its purchase, in one currency.
                Refuse($"{label}: the commission is in {commission} and the trade in {currency}");
                return;
            }

            if (trade.Proceeds + trade.Commission != trade.NetCash)
            {
                Refuse(Invariant($"{label}: proceeds {trade.Proceeds} and commission {trade.Commission} are not the net cash {trade.NetCash}"));
                return;
            }

            var entry = new Transaction(
                TransactionId.New(), place, buy ? TransactionKind.Buy : TransactionKind.Sell, trade.TradeDate,
                trade.SettleDate, new Money(trade.Proceeds, currency), TransactionSource.Flex, recordedAt,
                instrument.Id, trade.Quantity, new Price(trade.TradePrice, currency), trade.TransactionId);
            var fee = Commission(trade, entry, instrument.Id, commission);
            Entries.Add(entry);
            if (fee is not null)
            {
                Entries.Add(fee);
            }
        }

        private void Conversion(FlexTrade trade, string label)
        {
            // The pair is written base currency first: USD.ILS sells or buys dollars for shekels.
            var pair = trade.Symbol.Split('.');
            if (pair.Length != 2 || pair[1] != trade.Currency || pair[0] == pair[1])
            {
                Refuse($"{label}: '{trade.Symbol}' is not a pair ending in the currency of the row, {trade.Currency}");
                return;
            }

            var (sold, bought) = (Currency.Of(pair[0]), Currency.Of(pair[1]));
            if (trade.Quantity == 0m || Math.Sign(trade.Quantity) == Math.Sign(trade.Proceeds)
                || Math.Abs(Math.Abs(trade.Proceeds) - Math.Abs(trade.Quantity * trade.TradePrice)) > ProceedsTolerance)
            {
                Refuse(Invariant($"{label}: {trade.Quantity} {sold} at {trade.TradePrice} is not {trade.Proceeds} {bought}"));
                return;
            }

            var first = new Transaction(
                TransactionId.New(), place, TransactionKind.FxConversion, trade.TradeDate, trade.SettleDate,
                new Money(trade.Quantity, sold), TransactionSource.Flex, recordedAt, externalId: trade.TransactionId,
                note: Invariant($"{trade.Symbol} at {trade.TradePrice}"));
            var second = new Transaction(
                TransactionId.New(), place, TransactionKind.FxConversion, trade.TradeDate, trade.SettleDate,
                new Money(trade.Proceeds, bought), TransactionSource.Flex, recordedAt,
                externalId: $"{trade.TransactionId}/{bought.Code}", relatedTo: first.Id);
            var fee = Commission(trade, first, null, Currency.Of(trade.CommissionCurrency));
            Entries.Add(first);
            Entries.Add(second);
            if (fee is not null)
            {
                Entries.Add(fee);
            }
        }

        private Transaction? Commission(FlexTrade trade, Transaction main, InstrumentId? instrument, Currency currency) =>
            trade.Commission == 0m
                ? null
                : new Transaction(
                    TransactionId.New(), place, TransactionKind.Fee, trade.TradeDate, trade.SettleDate,
                    new Money(trade.Commission, currency), TransactionSource.Flex, recordedAt, instrument,
                    externalId: $"{trade.TransactionId}/commission", relatedTo: main.Id, note: "commission");

        private void Cash(FlexCash line)
        {
            var label = $"cash transaction {line.TransactionId ?? "(no id)"} ({line.Type})";
            if (line.TransactionId is null)
            {
                Refuse($"{label} has no transaction id; the ledger cannot tell it from itself in the next import");
                return;
            }

            if (line.Amount == 0m)
            {
                return; // nothing moved
            }

            // Only what has been seen: booked on the day it is dated, in the period of that day, settling then or later;
            // or a deposit IBKR booked a few days after the day it is dated (see BookedLater).
            var bookedLater = BookedLater(line);
            if (bookedLater is null
                && (line.ReportDate != line.Date || line.Date < period.From || line.Date > period.To
                    || line.SettleDate is { } early && early < line.Date))
            {
                Refuse(Invariant($"{label} is dated {line.Date:R}, booked on {line.ReportDate?.ToString("R", CultureInfo.InvariantCulture) ?? "no date"} and settles on {line.SettleDate?.ToString("R", CultureInfo.InvariantCulture) ?? "no date"}; a row whose dates differ has not been seen yet and is not guessed"));
                return;
            }

            try
            {
                var money = new Money(line.Amount, Currency.Of(line.Currency));
                var date = bookedLater ?? line.Date;
                var settlement = bookedLater ?? line.SettleDate ?? line.Date;
                Transaction Entry(TransactionKind kind, InstrumentId? instrument = null, TransactionId? relatedTo = null, string? note = null) =>
                    new(TransactionId.New(), place, kind, date, settlement, money, TransactionSource.Flex, recordedAt,
                        instrument, externalId: line.TransactionId, relatedTo: relatedTo, note: note);

                switch (line.Type)
                {
                    case "Deposits/Withdrawals":
                        // The description can carry the owner's bank details; it is left out.
                        Entries.Add(Entry(
                            line.Amount > 0m ? TransactionKind.Deposit : TransactionKind.Withdrawal,
                            note: bookedLater is null ? null : Invariant($"dated {line.Date:R} by IBKR, booked {bookedLater:R}")));
                        break;
                    case "Dividends":
                        Dividend(line, label, Entry);
                        break;
                    case "Withholding Tax":
                        Withheld(line, label, Entry);
                        break;
                    case "Broker Interest Received" or "Broker Interest Paid":
                        Entries.Add(Entry(TransactionKind.Interest, note: line.Description));
                        break;
                    case "Other Fees":
                        Entries.Add(Entry(TransactionKind.Fee, note: line.Description));
                        break;
                    default:
                        Refuse($"{label}: this kind of cash transaction has not been seen yet and is not guessed");
                        break;
                }
            }
            catch (ArgumentException e)
            {
                Refuse($"{label}: {e.Message}");
            }
        }

        /// <summary>
        /// The day IBKR booked a deposit that is dated (and settles) a few days earlier, as the owner's first deposit
        /// on the real account (statement of 17 October 2026: dated and settling 13 October, booked 14 October, the
        /// account's `dateFunded` 14 October). The ledger takes the day of the booking: IBKR's own cash of each day
        /// counts the money from then (`EquitySummaryByReportDateInBase`: nothing on the 13th, the deposit on the
        /// 14th; `StatementOfFunds.reportDate`, `dateFunded`). An assumption of 17 October 2026, not yet seen on a
        /// statement broken out by day - the first such statement over a deposit confirms it, or its per-day cash
        /// check refuses it. Known difference: IBKR names the dated day as `availableForTradingDate`, so the index
        /// comparison buys one close later than the money could have been. The dated day is kept in the note. Null for anything
        /// else, which stays refused: a withdrawal (a returned deposit would read as the owner taking money out),
        /// a longer gap than <see cref="MostDaysBookedLater"/>, interest or a dividend booked later.
        /// </summary>
        private LocalDate? BookedLater(FlexCash line) =>
            line.Type == "Deposits/Withdrawals" && line.Amount > 0m
            && line.ReportDate is { } booked && booked > line.Date && booked >= period.From && booked <= period.To
            && Period.Between(line.Date, booked, PeriodUnits.Days).Days <= MostDaysBookedLater
            && line.SettleDate is { } settles && settles >= line.Date && settles <= booked
                ? booked
                : null;

        private void Dividend(
            FlexCash line, string label, Func<TransactionKind, InstrumentId?, TransactionId?, string?, Transaction> entry)
        {
            // A return of capital or a capital gain comes as "Dividends" too and means something else for the
            // cost and the tax: only the ordinary dividend is recorded until another kind has been seen.
            if (!line.Description.Contains(OrdinaryDividend, StringComparison.Ordinal))
            {
                Refuse($"{label}: '{line.Description}' is not described as an ordinary dividend; another kind of distribution is not guessed");
                return;
            }

            if (line.ActionId.Length == 0)
            {
                Refuse($"{label} has no actionID; the tax withheld from it could not be told to belong to it");
                return;
            }

            if (Instrument(line.ConId, null, label) is not { } paying)
            {
                return;
            }

            var dividend = entry(TransactionKind.Dividend, paying.Id, null, line.Description);
            Entries.Add(dividend);
            if (!dividends.TryGetValue(line.ActionId, out var same))
            {
                dividends[line.ActionId] = same = [];
            }

            same.Add(dividend);
        }

        private void Withheld(
            FlexCash line, string label, Func<TransactionKind, InstrumentId?, TransactionId?, string?, Transaction> entry)
        {
            if (Instrument(line.ConId, null, label) is not { } taxed)
            {
                return;
            }

            // The tax belongs to the dividend of the same corporate event, in this statement.
            var of = dividends.GetValueOrDefault(line.ActionId)?.Where(d => d.Instrument == taxed.Id).ToList() ?? [];
            if (line.ActionId.Length == 0 || of.Count != 1)
            {
                Refuse($"{label}: the dividend it was withheld from (actionID '{line.ActionId}') is {(of.Count == 0 ? "not in this statement" : "there more than once")}; a tax that cannot be told to its dividend is not guessed");
                return;
            }

            Entries.Add(entry(TransactionKind.TaxWithheld, taxed.Id, of[0].Id, line.Description));
        }

        private Instrument? Instrument(int? conId, string? isin, string label)
        {
            if (conId is not { } id)
            {
                Refuse($"{label} names no contract id (conid)");
                return null;
            }

            if (!instruments.TryGetValue(id, out var instrument))
            {
                Refuse(Invariant($"{label}: IBKR contract {id}{(isin is null ? "" : $" ({isin})")} is not in the instrument catalogue - add it there and import the catalogue first"));
                return null;
            }

            if (isin is not null && instrument.Isin is { } known && known.Value != isin)
            {
                Refuse($"{label}: ISIN {isin} in the statement, {known} in the catalogue for the same contract");
                return null;
            }

            return instrument;
        }

        /// <summary>
        /// The cash the entries move in each currency has to be the change IBKR reports for the period, currency by
        /// currency. On a day IBKR reports the cash only in the base currency, that sum includes the translation of
        /// the other currencies, and the day is left to the checks of the whole statement (<see cref="FlexImport"/>).
        /// </summary>
        private void CheckCash()
        {
            var perCurrency = period.PerCurrency;
            if (perCurrency.Count == 0)
            {
                return;
            }

            CashChecked = true;
            var moved = CashMoved(Entries);
            var reported = perCurrency.ToDictionary(r => r.Currency, r => r.EndingCash - r.StartingCash, StringComparer.Ordinal);
            foreach (var code in moved.Keys.Union(reported.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var ours = moved.GetValueOrDefault(code);
                if (!reported.TryGetValue(code, out var theirs))
                {
                    Refuse(Invariant($"the entries move {ours} {code} and the Cash Report says nothing of {code}"));
                }
                else if (ours != theirs)
                {
                    Refuse(Invariant($"the entries move {ours} {code} and IBKR's Cash Report {theirs} {code}: something in the statement is not read"));
                }
            }
        }

        private void Refuse(string why) => Refusals.Add($"{_where}: {why}.");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
