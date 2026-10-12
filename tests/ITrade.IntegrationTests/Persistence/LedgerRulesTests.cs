using System.Globalization;
using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace ITrade.IntegrationTests.Persistence;

/// <summary>
/// P3.1.02 (review): a row of the ledger is permanent, so the database itself repeats the rules of the domain
/// type — a wrong row must not get in even past the application. Each rule is shown a planted violation.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class LedgerRulesTests(PostgresFixture pg)
{
    private static readonly Instant At = Instant.FromUtc(2026, 10, 12, 21, 0);
    private static readonly LocalDate Traded = new(2026, 10, 12);

    private sealed record World(string Database, HoldingPlaceId Ibkr, HoldingPlaceId Other, InstrumentId Voo);

    private async Task<World> NewAsync()
    {
        var database = await pg.NewEmptyDatabaseAsync();
        await using var db = DbSetup.Create(database);
        await db.Database.MigrateAsync();
        var ibkr = new HoldingPlace(HoldingPlaceId.New(), "IBKR", HoldingPlaceKind.Broker, IntegrationKind.Broker);
        var other = new HoldingPlace(HoldingPlaceId.New(), "Another broker", HoldingPlaceKind.Broker, IntegrationKind.Manual);
        var voo = new Instrument(InstrumentId.New(), new Ticker("VOO"), "ARCA", Currency.Usd, "Vanguard S&P 500 ETF");
        db.HoldingPlaces.AddRange(HoldingPlaceRow.From(ibkr), HoldingPlaceRow.From(other));
        db.Instruments.Add(InstrumentRow.From(voo));
        await db.SaveChangesAsync();
        return new World(database, ibkr.Id, other.Id, voo.Id);
    }

    private static Transaction Entry(
        World w, TransactionKind kind, decimal cash, decimal quantity = 0m, bool instrument = false, decimal? price = null,
        string? externalId = null, TransactionId? relatedTo = null, HoldingPlaceId? place = null) =>
        new(
            TransactionId.New(), place ?? w.Ibkr, kind, Traded, Traded.PlusDays(1), Money.Usd(cash),
            externalId is null ? TransactionSource.Manual : TransactionSource.Flex, At, instrument ? w.Voo : null, quantity,
            price is { } p ? new Price(p, Currency.Usd) : null, externalId, relatedTo);

    private static async Task AddAsync(World w, params Transaction[] entries)
    {
        await using var db = DbSetup.Create(w.Database);
        foreach (var entry in entries)
        {
            db.Transactions.Add(TransactionRow.From(entry));
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Inserts a row past the application: a valid deposit typed in by hand, with some columns replaced.</summary>
    private static async Task<PostgresException> RefusedAsync(World w, params (string Column, string Sql)[] replaced)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "gen_random_uuid()",
            ["holding_place_id"] = $"'{w.Ibkr}'",
            ["kind"] = "'Deposit'",
            ["trade_date"] = "'2026-10-12'",
            ["settlement_date"] = "'2026-10-13'",
            ["quantity_change"] = "0",
            ["cash_amount"] = "10",
            ["cash_currency"] = "'USD'",
            ["source"] = "'Manual'",
            ["recorded_at"] = "now()",
        };
        foreach (var (column, sql) in replaced)
        {
            row[column] = sql;
        }

        await using var conn = new NpgsqlConnection(w.Database);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            string.Create(CultureInfo.InvariantCulture, $"INSERT INTO transactions ({string.Join(", ", row.Keys)}) VALUES ({string.Join(", ", row.Values)})"),
            conn);
        return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task EverythingTheDomainAllowsTheDatabaseAllowsToo()
    {
        var w = await NewAsync();
        var buy = Entry(w, TransactionKind.Buy, -1203.50m, 2m, instrument: true, price: 601.75m, externalId: "T-1");
        var fee = Entry(w, TransactionKind.Fee, -0.35m, instrument: true, externalId: "T-1:fee", relatedTo: buy.Id);
        var dividend = Entry(w, TransactionKind.Dividend, 3.42m, instrument: true, externalId: "DIV-1");
        var sold = Entry(w, TransactionKind.FxConversion, -100m, externalId: "FX-1");
        var buyReversal = buy.Reversal(TransactionId.New(), At, "the broker cancelled the trade");
        Transaction[] entries =
        [
            buy,
            fee,
            Entry(w, TransactionKind.Sell, 601.75m, -1m, instrument: true, price: 601.75m, externalId: "T-2"),
            dividend,
            Entry(w, TransactionKind.TaxWithheld, -0.86m, instrument: true, externalId: "DIV-1:tax", relatedTo: dividend.Id),
            Entry(w, TransactionKind.Dividend, -3.42m, instrument: true, externalId: "DIV-1:back"), // taken back by the broker
            Entry(w, TransactionKind.TaxWithheld, 0.86m, instrument: true, externalId: "DIV-1:refund"),
            Entry(w, TransactionKind.Fee, 0.10m, externalId: "REBATE-1"),
            Entry(w, TransactionKind.Fee, -10m),
            Entry(w, TransactionKind.Interest, 0.12m, externalId: "INT-1"),
            Entry(w, TransactionKind.Interest, -0.07m, externalId: "INT-2"),
            sold,
            Entry(w, TransactionKind.FxConversion, 27.32m, externalId: "FX-1:in", relatedTo: sold.Id),
            Entry(w, TransactionKind.Deposit, 1000m),
            Entry(w, TransactionKind.Withdrawal, -500m),
            Entry(w, TransactionKind.CorporateAction, 0m, 4m, instrument: true, externalId: "CA-1"),
            Entry(w, TransactionKind.CorporateAction, 1.15m, instrument: true, externalId: "CA-2"),
            buyReversal,
            fee.Reversal(TransactionId.New(), At, "the broker cancelled the trade").Relinked(buyReversal.Id, fee.Id),
            // The corrected line, recorded again after the reversal.
            Entry(w, TransactionKind.Buy, -1203.60m, 2m, instrument: true, price: 601.80m, externalId: "T-1").Revised(1),
        ];

        await AddAsync(w, entries);

        await using var db = DbSetup.Create(w.Database);
        var rows = await db.Transactions.AsNoTracking().OrderBy(t => t.Sequence).ToListAsync();
        Assert.Equal(entries, rows.Select(r => r.ToDomain()));
        Assert.Equal([0, 1], rows.Where(r => r.ExternalId == "T-1").Select(r => r.ExternalRevision));
    }

    [Fact]
    public async Task DatabaseRefusesARowThatBreaksARuleOfTheLedger()
    {
        var w = await NewAsync();
        var voo = $"'{w.Voo}'";
        (string Constraint, (string, string)[] Row)[] planted =
        [
            ("ck_transactions_kind_is_known", [("kind", "'Gift'")]),
            ("ck_transactions_source_is_known", [("source", "'Email'"), ("external_id", "'x'")]),
            ("ck_transactions_imported_entry_has_its_external_id", [("source", "'Flex'")]),
            ("ck_transactions_imported_entry_has_its_external_id", [("external_id", "'  '")]),
            ("ck_transactions_revision_belongs_to_an_external_id", [("external_revision", "1")]),
            ("ck_transactions_revision_belongs_to_an_external_id", [("external_revision", "-1"), ("external_id", "'x'")]),
            ("ck_transactions_does_not_point_at_itself",
                [("id", "'00000000-0000-7000-8000-000000000001'"), ("related_to", "'00000000-0000-7000-8000-000000000001'")]),
            ("ck_transactions_only_a_trade_has_a_price", [("price", "5")]),
            ("ck_transactions_only_a_trade_has_a_price",
                [("kind", "'Buy'"), ("instrument_id", voo), ("quantity_change", "1"), ("cash_amount", "-10")]),
            ("ck_transactions_only_a_trade_has_a_price",
                [("kind", "'Buy'"), ("instrument_id", voo), ("quantity_change", "1"), ("cash_amount", "-10"), ("price", "0")]),
            ("ck_transactions_instrument_fits_the_kind", [("instrument_id", voo)]), // a deposit of an instrument
            ("ck_transactions_instrument_fits_the_kind", [("kind", "'Dividend'")]), // a dividend of nothing
            ("ck_transactions_effects_fit_the_kind", [("cash_amount", "-10")]), // a deposit that takes cash away
            ("ck_transactions_effects_fit_the_kind", [("kind", "'Withdrawal'")]), // a withdrawal that brings cash
            ("ck_transactions_effects_fit_the_kind", [("kind", "'Interest'"), ("quantity_change", "1")]),
            ("ck_transactions_effects_fit_the_kind",
                [("kind", "'Buy'"), ("instrument_id", voo), ("quantity_change", "-1"), ("cash_amount", "-10"), ("price", "10")]),
            ("ck_transactions_effects_fit_the_kind",
                [("kind", "'Sell'"), ("instrument_id", voo), ("quantity_change", "-1"), ("cash_amount", "-10"), ("price", "10")]),
        ];

        foreach (var (constraint, row) in planted)
        {
            var error = await RefusedAsync(w, row);
            Assert.Equal((PostgresErrorCodes.CheckViolation, constraint), (error.SqlState, error.ConstraintName));
        }

        await using var db = DbSetup.Create(w.Database);
        Assert.Equal(0, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task DatabaseRefusesALinkThatIsNotSound()
    {
        var w = await NewAsync();
        var buy = Entry(w, TransactionKind.Buy, -1203.50m, 2m, instrument: true, price: 601.75m, externalId: "T-1");
        var fee = Entry(w, TransactionKind.Fee, -0.35m, instrument: true, externalId: "T-1:fee", relatedTo: buy.Id);
        var deposit = Entry(w, TransactionKind.Deposit, 1000m);
        var depositReversal = deposit.Reversal(TransactionId.New(), At, "entered twice");
        await AddAsync(w, buy, fee, deposit, depositReversal);
        (string Says, (string, string)[] Row)[] planted =
        [
            // Hanging on an entry of another place, and hanging on something that itself hangs on a trade.
            ("cannot hang on", [("holding_place_id", $"'{w.Other}'"), ("related_to", $"'{buy.Id}'")]),
            ("cannot hang on", [("related_to", $"'{fee.Id}'")]),
            // "Reversing" a deposit of 1000 with 1, at another place, on another day, and reversing a reversal.
            ("not the exact opposite", [("cash_amount", "-1"), ("reverses", $"'{deposit.Id}'")]),
            ("not the exact opposite", [("cash_amount", "-1000"), ("reverses", $"'{deposit.Id}'"), ("holding_place_id", $"'{w.Other}'")]),
            ("not the exact opposite", [("cash_amount", "-1000"), ("reverses", $"'{deposit.Id}'"), ("trade_date", "'2026-10-13'")]),
            ("not the exact opposite", [("cash_amount", "-1000"), ("reverses", $"'{depositReversal.Id}'")]),
        ];

        foreach (var (says, row) in planted)
        {
            var error = await RefusedAsync(w, row);
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Contains(says, error.MessageText, StringComparison.Ordinal);
        }

        await using var db = DbSetup.Create(w.Database);
        Assert.Equal(4, await db.Transactions.CountAsync());
    }
}
