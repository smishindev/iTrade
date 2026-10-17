using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using ITrade.Application.Portfolio;
using ITrade.Application.Prices;
using ITrade.Broker.IBKR.Flex;
using ITrade.Domain;
using ITrade.Host;
using ITrade.Host.Portfolio;
using ITrade.Infrastructure.Persistence;
using ITrade.IntegrationTests.Host;
using ITrade.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace ITrade.IntegrationTests.Portfolio;

/// <summary>
/// P3.2.02 through the real Host and a real database: `import-statement` records the anonymised statement of
/// the paper account once, however often it is run; the books it leaves are IBKR's own; a paper account's
/// statement, another account's, and a statement the import cannot read whole record nothing.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class StatementImportTests(PostgresFixture pg)
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flex-statement.xml");
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 16, 12, 0);

    private sealed record World(string Database, string DataDir);

    private async Task<World> NewAsync()
    {
        var world = new World(await pg.NewEmptyDatabaseAsync(), RunningHost.NewDataDir());
        await using var app = ITradeHost.Build(RunningHost.Args(world.Database, world.DataDir));
        using var output = new StringWriter();
        Assert.Equal(0, await ImportInstrumentsCommand.RunAsync(app, "config/instruments.csv", output));
        return world;
    }

    private static async Task<(int Code, string[] Output)> ImportAsync(World world, string file)
    {
        await using var app = ITradeHost.Build(
            RunningHost.Args(world.Database, world.DataDir), services => services.AddSingleton<IClock>(new FakeClock(Now)));
        using var output = new StringWriter();
        var code = await ImportStatementCommand.RunAsync(app, file, output);
        return (code, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The fixture with a change, written next to the data folder.</summary>
    private static string Variant(World world, string name, Action<XDocument> change)
    {
        var document = XDocument.Load(Fixture);
        change(document);
        var path = Path.Combine(world.DataDir, name);
        document.Save(path);
        return path;
    }

    /// <summary>Keeps only the statements of the given days (yyyyMMdd), as a download of a shorter period would.</summary>
    private static Action<XDocument> Only(params string[] days) => document =>
    {
        var statements = document.Root!.Element("FlexStatements")!;
        statements.Elements("FlexStatement").Where(s => !days.Contains(s.Attribute("fromDate")!.Value)).Remove();
        statements.SetAttributeValue("count", statements.Elements("FlexStatement").Count());
    };

    private static void Account(XDocument document, string account)
    {
        foreach (var attribute in document.Descendants().Attributes("accountId").Where(a => a.Value == "U0000000"))
        {
            attribute.Value = account;
        }
    }

    [Fact]
    public async Task TheStatementIsRecordedOnceAndTheBooksAreIbkrsOwn()
    {
        var world = await NewAsync();

        var first = await ImportAsync(world, Fixture);
        var again = await ImportAsync(world, Fixture);

        Assert.Equal(0, first.Code);
        Assert.Equal(
            "Statement of U…000, 2026-10-07 to 2026-10-15 (4 day(s)): 46 entries; 46 recorded, 0 already in the ledger, 0 left out.",
            first.Output[0]);
        Assert.Equal(
            "The ledger agrees with IBKR: the cash at the start and the end of the statement, and the shares at its end; 1 period(s) IBKR reported only in the base currency were not checked one by one.",
            first.Output[1]);
        Assert.Equal(0, again.Code);
        Assert.Equal(
            "Statement of U…000, 2026-10-07 to 2026-10-15 (4 day(s)): 46 entries; 0 recorded, 46 already in the ledger, 0 left out.",
            again.Output[0]);

        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Fixture)));
        var kept = Path.Combine(ImportStatementCommand.ArchiveDir(world.DataDir), $"20261015-{sha[..12]}.xml");
        Assert.Equal($"Kept as {kept}.", first.Output[^1]);
        Assert.Equal(await File.ReadAllBytesAsync(Fixture), await File.ReadAllBytesAsync(kept));

        await using var app = ITradeHost.Build(RunningHost.Args(world.Database, world.DataDir));
        await using var scope = app.Services.CreateAsyncScope();
        var place = await scope.ServiceProvider.GetRequiredService<IHoldingPlaceStore>().FindByNameAsync(FlexImport.HoldingPlaceName, CancellationToken.None);
        Assert.Equal((HoldingPlaceKind.Broker, IntegrationKind.Broker), (place!.Kind, place.Integration));
        var books = await scope.ServiceProvider.GetRequiredService<PortfolioBooksReader>().AsOfAsync(place.Id, new LocalDate(2026, 10, 15), CancellationToken.None);
        Assert.Equal(Money.Usd(983_654.099691m), books.CashOf(place.Id, Currency.Usd));
        Assert.Equal(Money.Ils(309.94m), books.CashOf(place.Id, Currency.Ils));
        var vt = Assert.Single(books.Positions);
        Assert.Equal((103m, 16_243.900309m), (vt.Quantity.Value, (vt.Cost + vt.Fees).Amount));

        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(46, await db.Transactions.CountAsync());
        var audit = await db.AuditEvents.Where(e => e.Kind == ImportStatementCommand.AuditKind).OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(2, audit.Count);
        using var payload = JsonDocument.Parse(audit[0].Payload);
        var said = payload.RootElement;
        Assert.Equal(Path.GetFileName(kept), said.GetProperty("kept").GetString());
        Assert.False(said.TryGetProperty("file", out _)); // the owner's name of the file can hold the account number
        Assert.Equal(FlexAccount.Of("U0000000").Key, said.GetProperty("accountKey").GetString());
        Assert.Equal(
            (sha, "U…000", "2026-10-07", "2026-10-15", 46, 46, 0),
            (said.GetProperty("sha256").GetString(), said.GetProperty("account").GetString(), said.GetProperty("from").GetString(),
                said.GetProperty("to").GetString(), said.GetProperty("entries").GetInt32(), said.GetProperty("added").GetInt32(),
                said.GetProperty("notRecorded").GetInt32()));
        Assert.DoesNotContain("U0000000", audit[0].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStatementThatStartsWhereTheLedgerDoesNotEndIsRefused()
    {
        // Imported up to the 8th; the next download covers only the 15th: the 14th was never read.
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Variant(world, "early.xml", Only("20261007", "20261008")))).Code);

        var (code, output) = await ImportAsync(world, Variant(world, "late.xml", Only("20261015")));

        Assert.Equal(1, code);
        Assert.Contains(
            "! At the evening of 2026-10-14 IBKR's cash is 984231.9997 USD and the ledger's 1000000 USD: the ledger was not brought up to the day before this statement: a statement between the last import and this one is missing, or the history before this statement was never imported - download a period that starts where the ledger ends.",
            output);
        Assert.Contains("! At the end of 2026-10-15 IBKR holds 103 of VT and the ledger with this statement 3: shares moved without cash (a split, a transfer) or a trade is missing.", output);
        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(1, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task AFirstStatementWithoutTheHistoryBeforeItIsRefused()
    {
        var world = await NewAsync();

        var (code, output) = await ImportAsync(world, Variant(world, "late.xml", Only("20261014", "20261015")));

        Assert.Equal(1, code);
        Assert.Contains(output, l => l.StartsWith("! At the evening of 2026-10-13 IBKR's cash is 1000000 USD and the ledger's 0 USD", StringComparison.Ordinal));
        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(0, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task SharesThatMovedWithoutCashStopTheStatement()
    {
        // A split IBKR applied in a section the query does not show: the cash agrees, the shares do not.
        var world = await NewAsync();
        var split = Variant(world, "split.xml", d => d.Descendants("OpenPosition").Last().SetAttributeValue("position", "206"));

        var (code, output) = await ImportAsync(world, split);

        Assert.Equal(1, code);
        Assert.Equal(
            "! At the end of 2026-10-15 IBKR holds 206 of VT and the ledger with this statement 103: shares moved without cash (a split, a transfer) or a trade is missing.",
            output[1]);
    }

    /// <summary>IBKR restates the commission of the first execution: −1.000015 becomes −1.00002.</summary>
    private static void Restated(XDocument document)
    {
        var trade = document.Descendants("Trade").First(t => t.Attribute("transactionID")!.Value == "11006946599");
        trade.SetAttributeValue("ibCommission", "-1.00002");
        trade.SetAttributeValue("netCash", "-789.35002");
        foreach (var (day, attributes) in new[] { ("20261014", new[] { "endingCash" }), ("20261015", new[] { "startingCash", "endingCash" }) })
        {
            var row = document.Descendants("FlexStatement").Single(s => s.Attribute("fromDate")!.Value == day)
                .Element("CashReport")!.Elements("CashReportCurrency").Single(r => r.Attribute("currency")!.Value == "USD");
            foreach (var name in attributes)
            {
                var value = decimal.Parse(row.Attribute(name)!.Value, System.Globalization.CultureInfo.InvariantCulture) - 0.000005m;
                row.SetAttributeValue(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    [Fact]
    public async Task ALineIbkrRestatedIsRecordedAgainOnceTheOwnerReversedTheOldOne()
    {
        // The skill's way for a corrected line: reverse the recording, import again - it comes as the next revision.
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        var restated = Variant(world, "restated.xml", Restated);

        var before = await ImportAsync(world, restated);

        Assert.Equal(1, before.Code);
        Assert.Contains("! At the end of 2026-10-15 IBKR's cash is 983654.099686 USD and the ledger's 983654.099691 USD: something that moved cash is not in the ledger or not in this statement.", before.Output);
        Assert.Contains(before.Output, l => l.StartsWith("! The ledger would leave out Flex 11006946599/commission: the ledger holds this line differently", StringComparison.Ordinal) && l.Contains("cash -1.000015 USD in the ledger and -1.00002 USD in the source", StringComparison.Ordinal));

        Assert.Contains(before.Output, l => l.StartsWith("If IBKR restated the line, reverse the old entry (the owner's step): dotnet run --project src/ITrade.Host -- reverse-entry", StringComparison.Ordinal));
        string feeId;
        await using (var db = DbSetup.Create(world.Database))
        {
            feeId = (await db.Transactions.SingleAsync(t => t.ExternalId == "11006946599/commission")).Id.Value.ToString();
        }

        // The owner's step, as the owner types it.
        await using (var app = ITradeHost.Build(RunningHost.Args(world.Database, world.DataDir)))
        {
            using var said = new StringWriter();
            Assert.Equal(0, await ReverseEntryCommand.RunAsync(app, feeId, "IBKR restated the commission", said));
            Assert.StartsWith($"Reversed {feeId}: Fee of 2026-10-14, now 1.000015 USD", said.ToString(), StringComparison.Ordinal);
        }

        var after = await ImportAsync(world, restated);

        Assert.Equal(0, after.Code);
        Assert.Equal("Statement of U…000, 2026-10-07 to 2026-10-15 (4 day(s)): 46 entries; 1 recorded, 45 already in the ledger, 0 left out.", after.Output[0]);
        Assert.StartsWith("The ledger agrees with IBKR", after.Output[1], StringComparison.Ordinal);
        await using var check = DbSetup.Create(world.Database);
        Assert.Equal(1, await check.Transactions.CountAsync(t => t.ExternalId == "11006946599/commission" && t.ExternalRevision == 1));
        Assert.Equal(1, await check.AuditEvents.CountAsync(e => e.Kind == "ledger.entry-reversed"));
    }

    [Fact]
    public async Task AnEntryIsReversedOnceAndALegOfAConversionNotAlone()
    {
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        string fee, leg;
        await using (var db = DbSetup.Create(world.Database))
        {
            fee = (await db.Transactions.SingleAsync(t => t.ExternalId == "11006946599/commission")).Id.Value.ToString();
            leg = (await db.Transactions.SingleAsync(t => t.ExternalId == "11013041293/ILS")).Id.Value.ToString();
        }

        async Task<(int Code, string Said)> ReverseAsync(string id, string reason)
        {
            await using var app = ITradeHost.Build(RunningHost.Args(world.Database, world.DataDir));
            using var said = new StringWriter();
            return (await ReverseEntryCommand.RunAsync(app, id, reason, said), said.ToString());
        }

        Assert.Equal(0, (await ReverseAsync(fee, "a test")).Code);
        var twice = await ReverseAsync(fee, "a test");
        var oneLeg = await ReverseAsync(leg, "a test");
        var none = await ReverseAsync(Guid.NewGuid().ToString(), "a test");
        var noReason = await ReverseAsync(fee, " ");

        Assert.Equal((1, 1, 1, 2), (twice.Code, oneLeg.Code, none.Code, noReason.Code));
        Assert.StartsWith("Nothing was reversed: ", twice.Said, StringComparison.Ordinal);
        Assert.Contains("is one leg of a currency conversion", oneLeg.Said, StringComparison.Ordinal);
        await using var check = DbSetup.Create(world.Database);
        Assert.Equal(47, await check.Transactions.CountAsync());
    }

    [Fact]
    public async Task WithoutRowsPerCurrencyTheCashOfSeveralCurrenciesCannotBeHeldAgainstIbkr()
    {
        // IBKR left the rows per currency out of the last day, and the account holds dollars and shekels.
        var world = await NewAsync();
        var summaryOnly = Variant(world, "summary.xml", d => d.Descendants("FlexStatement").Last()
            .Element("CashReport")!.Elements("CashReportCurrency").Where(r => r.Attribute("currency")!.Value != "BASE_SUMMARY").Remove());

        var (code, output) = await ImportAsync(world, summaryOnly);

        Assert.Equal(1, code);
        Assert.Contains(
            "! IBKR's Cash Report of the end of 2026-10-15 has no row per currency, and the ledger holds USD, ILS: the cash cannot be held against it. Download the statement again (IBKR sometimes leaves those rows out) and import that one.",
            output);
    }

    [Fact]
    public async Task AnOlderStatementAfterANewerOneAddsNothing()
    {
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);

        var (code, output) = await ImportAsync(world, Variant(world, "early.xml", Only("20261007", "20261008")));

        Assert.Equal(0, code);
        Assert.Equal(
            "Statement of U…000, 2026-10-07 to 2026-10-08 (2 day(s)): 1 entries; 0 recorded, 1 already in the ledger, 0 left out.",
            output[0]);
    }

    [Fact]
    public async Task APaperAccountsStatementIsNeverRecorded()
    {
        var world = await NewAsync();
        var paper = Variant(world, "paper.xml", d => Account(d, "DU0000000"));

        var (code, output) = await ImportAsync(world, paper);

        Assert.Equal(1, code);
        Assert.Equal("Nothing was recorded from the statement of D…000, 2026-10-07 to 2026-10-15:", output[0]);
        Assert.StartsWith("! D…000 is a paper (demo) account.", output[1], StringComparison.Ordinal);
        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(0, await db.Transactions.CountAsync());
        Assert.Equal(0, await db.HoldingPlaces.CountAsync());
        Assert.False(Directory.Exists(ImportStatementCommand.ArchiveDir(world.DataDir)));
    }

    [Fact]
    public async Task AnotherAccountsStatementIsRefusedAfterTheFirst()
    {
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        var other = Variant(world, "other.xml", d => Account(d, "F0000000"));

        var (code, output) = await ImportAsync(world, other);

        Assert.Equal(1, code);
        Assert.Equal("! The statements imported so far are of U…000; this one is of F…000.", output[1]);
        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(46, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task AStatementTheImportCannotReadWholeRecordsNothing()
    {
        var world = await NewAsync();
        var cancelled = Variant(world, "cancelled.xml", d => d.Descendants("Trade").Last().SetAttributeValue("transactionType", "TradeCancel"));

        var (code, output) = await ImportAsync(world, cancelled);

        Assert.Equal(1, code);
        Assert.Contains(output, l => l.StartsWith("! 2026-10-15: trade 11013041293 (USD.ILS) is a 'TradeCancel'", StringComparison.Ordinal));
        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(0, await db.Transactions.CountAsync());
    }

    private static readonly string FirstDeposit = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flex-first-deposit.xml");

    /// <summary>
    /// The real account's first statement as a query broken out by day would give one of its days: the same rows,
    /// the period (and IBKR's Cash Report of it) of that day alone.
    /// </summary>
    private static string DayOfFirstDeposit(World world, string day)
    {
        var document = XDocument.Load(FirstDeposit);
        foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name.LocalName is "fromDate" or "toDate"))
        {
            attribute.Value = day;
        }

        var path = Path.Combine(world.DataDir, $"day-{day}.xml");
        document.Save(path);
        return path;
    }

    [Fact]
    public async Task TheFirstDepositOfTheRealAccountIsRecordedOnTheDayIbkrBookedIt()
    {
        // 100 ILS dated and settling 13 October, booked by IBKR on the 14th; one period of 30 days.
        var world = await NewAsync();

        var first = await ImportAsync(world, FirstDeposit);
        var again = await ImportAsync(world, FirstDeposit);

        Assert.Equal(0, first.Code);
        Assert.Equal(
            "Statement of U…000, 2026-09-17 to 2026-10-16 (1 period(s), not broken out by day): 1 entries; 1 recorded, 0 already in the ledger, 0 left out.",
            first.Output[0]);
        Assert.Equal(
            "The ledger agrees with IBKR: the cash at the start and the end of the statement, and the shares at its end; 1 period(s) of several days were checked only as a whole, not day by day (turn on Breakout by Day in the Flex query).",
            first.Output[1]);
        Assert.Equal((0, "Statement of U…000, 2026-09-17 to 2026-10-16 (1 period(s), not broken out by day): 1 entries; 0 recorded, 1 already in the ledger, 0 left out."), (again.Code, again.Output[0]));
        await using var db = DbSetup.Create(world.Database);
        var deposit = await db.Transactions.SingleAsync();
        Assert.Equal(
            (TransactionKind.Deposit, new LocalDate(2026, 10, 14), new LocalDate(2026, 10, 14), 100m, Currency.Ils, (string?)"43408377031", (string?)"dated 2026-10-13 by IBKR, booked 2026-10-14"),
            (deposit.Kind, deposit.TradeDate, deposit.SettlementDate, deposit.CashAmount, deposit.CashCurrency, deposit.ExternalId, deposit.Note));
    }

    [Fact]
    public async Task TheDayOfTheBookingBrokenOutLaterAgreesWithTheLedgerAndAddsNothing()
    {
        // The reason for taking the day of the booking: a statement of the 14th alone starts from IBKR's cash of the
        // evening of the 13th, nothing - the ledger has to hold nothing then, and the deposit on the 14th.
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, FirstDeposit)).Code);

        var (code, output) = await ImportAsync(world, DayOfFirstDeposit(world, "20261014"));

        Assert.Equal(0, code);
        Assert.Equal("Statement of U…000, 2026-10-14 to 2026-10-14 (1 day(s)): 1 entries; 0 recorded, 1 already in the ledger, 0 left out.", output[0]);
        Assert.Equal("The ledger agrees with IBKR: the cash at the start and the end of the statement, and the shares at its end; the cash of every day too.", output[1]);
    }

    [Fact]
    public async Task ADepositListedOnTheDayItIsDatedButBookedLaterIsRefused()
    {
        var world = await NewAsync();

        var (code, output) = await ImportAsync(world, DayOfFirstDeposit(world, "20261013"));

        Assert.Equal(1, code);
        Assert.Contains("! 2026-10-13: cash transaction 43408377031 (Deposits/Withdrawals) is dated 2026-10-13, booked on 2026-10-14 and settles on 2026-10-13; a row whose dates differ has not been seen yet and is not guessed.", output);
        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(0, await db.Transactions.CountAsync());
    }

    private static async Task<(int Code, string[] Output)> ReconcileAsync(World world, string? file = null)
    {
        await using var app = ITradeHost.Build(
            RunningHost.Args(world.Database, world.DataDir), services => services.AddSingleton<IClock>(new FakeClock(Now)));
        using var output = new StringWriter();
        var code = await ReconcileCommand.RunAsync(app, file, output);
        return (code, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task TheLedgerIsReconciledWithTheStatementImportedLastFigureByFigure()
    {
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);

        var (code, output) = await ReconcileAsync(world);

        Assert.Equal(0, code);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Fixture)));
        Assert.Equal(
            [
                $"The ledger against IBKR's statement of U…000, 2026-10-07 to 2026-10-15 (4 day(s)), 20261015-{sha[..12]}.xml; what the ledger holds after 2026-10-15 is not compared:",
                // 7 October has only IBKR's total in the base currency: shown as that, not as the dollars alone.
                "  the evening of 2026-10-06, cash of all currencies told in USD (IBKR gave no row per currency): ledger 0, IBKR 0",
                "  the end of 2026-10-15, cash ILS: ledger 309.94, IBKR 309.94",
                "  the end of 2026-10-15, cash USD: ledger 983654.099691, IBKR 983654.099691",
                "  the end of 2026-10-15, shares of VT: ledger 103, IBKR 103",
                "The ledger agrees with IBKR's statement; 1 figure(s) are IBKR's total of all currencies, which cannot tell the currencies apart - a statement with rows per currency checks them fully.",
                "The ledger's splits agree with the split days of the stored prices.",
            ],
            output);
    }

    [Fact]
    public async Task ADatabaseThisBuildHasNotMigratedIsNotTouched()
    {
        var world = new World(await pg.NewEmptyDatabaseAsync(), RunningHost.NewDataDir());

        var (code, output) = await ReconcileAsync(world);

        Assert.Equal(1, code);
        Assert.StartsWith("The database lacks ", output[0], StringComparison.Ordinal);
        await using var db = DbSetup.Create(world.Database);
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task WithoutAFileTheStatementThatReachesFurthestIsTaken()
    {
        // An older statement imported again afterwards must not hide the days after its own.
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        Assert.Equal(0, (await ImportAsync(world, Variant(world, "early.xml", Only("20261007", "20261008")))).Code);

        var (code, output) = await ReconcileAsync(world);

        Assert.Equal(0, code);
        Assert.StartsWith("The ledger against IBKR's statement of U…000, 2026-10-07 to 2026-10-15 (4 day(s)), 20261015-", output[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatTheLedgerHoldsOnTheStatementsDaysAndTheStatementDoesNotIsADifference()
    {
        // Typed in by hand: 500 in on 8 October and out on 14 October - both ends still agree.
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        await using (var app = ITradeHost.Build(RunningHost.Args(world.Database, world.DataDir)))
        {
            await using var scope = app.Services.CreateAsyncScope();
            var place = (await scope.ServiceProvider.GetRequiredService<IHoldingPlaceStore>().FindByNameAsync(FlexImport.HoldingPlaceName, CancellationToken.None))!.Id;
            Transaction Manual(TransactionKind kind, int day, decimal cash) =>
                new(TransactionId.New(), place, kind, new LocalDate(2026, 10, day), new LocalDate(2026, 10, day), Money.Usd(cash), TransactionSource.Manual, Now);
            var ledger = scope.ServiceProvider.GetRequiredService<TransactionLedger>();
            var undone = Manual(TransactionKind.Deposit, 15, 70m);
            await ledger.RecordAsync([Manual(TransactionKind.Deposit, 8, 500m), Manual(TransactionKind.Withdrawal, 14, -500m), undone], CancellationToken.None);
            await ledger.ReverseAsync(undone.Id, "typed twice", CancellationToken.None); // reversed: no longer in the books
        }

        var (code, output) = await ReconcileAsync(world);

        Assert.Equal(1, code);
        Assert.Equal(2, output.Count(l => l.StartsWith("! The ledger holds a ", StringComparison.Ordinal)));
        Assert.Contains("  the end of 2026-10-15, cash USD: ledger 983654.099691, IBKR 983654.099691", output);
        Assert.Contains(output, l => l.StartsWith("! The ledger holds a Deposit of 2026-10-08, 500 USD (Manual; entry ", StringComparison.Ordinal) && l.EndsWith("), that IBKR's statement does not state: if it is a mistake, it goes out with a reversal (the owner's step).", StringComparison.Ordinal));
        Assert.Contains(output, l => l.StartsWith("! The ledger holds a Withdrawal of 2026-10-14, -500 USD (Manual; entry ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASyntheticDifferenceIsFoundAndNothingIsChanged()
    {
        // IBKR's statement says 206 VT - a split in a section the query does not show - while the ledger holds 103.
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        var split = Variant(world, "split.xml", d => d.Descendants("OpenPosition").Last().SetAttributeValue("position", "206"));

        var (code, output) = await ReconcileAsync(world, split);

        Assert.Equal(1, code);
        Assert.Contains("! the end of 2026-10-15, shares of VT: ledger 103, IBKR 206", output);
        Assert.Contains("! At the end of 2026-10-15 IBKR holds 206 of VT and the ledger 103: shares moved without cash (a split, a transfer) or a trade is missing.", output);
        Assert.StartsWith("The ledger and IBKR's statement differ; nothing was changed.", output[^2], StringComparison.Ordinal);
        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(46, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task AnEntryReversedSinceTheImportIsADifference()
    {
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        string fee;
        await using (var db = DbSetup.Create(world.Database))
        {
            fee = (await db.Transactions.SingleAsync(t => t.ExternalId == "11006946599/commission")).Id.Value.ToString();
        }

        await using (var app = ITradeHost.Build(RunningHost.Args(world.Database, world.DataDir)))
        {
            using var said = new StringWriter();
            Assert.Equal(0, await ReverseEntryCommand.RunAsync(app, fee, "a test", said));
        }

        var (code, output) = await ReconcileAsync(world);

        Assert.Equal(1, code);
        Assert.Contains("! the end of 2026-10-15, cash USD: ledger 983655.099706, IBKR 983654.099691", output);
        Assert.Contains(output, l => l.StartsWith("! The ledger does not hold IBKR's line as stated: Flex 11006946599/commission", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASplitThePricesShowAndTheLedgerLacksIsFound()
    {
        // The prices already know a 2:1 split of VT on 16 October; the statement that brings it is not imported yet.
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        await using (var db = DbSetup.Create(world.Database))
        {
            var vt = await db.Instruments.SingleAsync(i => i.IbkrConId == 52197301);
            var day = new LocalDate(2026, 10, 16);
            await new ClosingPriceStore(db).AddAsync(
                new PricesToStore([], [], ClosingPrices.Calendar, [day], [new FundEvent(vt.Id, day, FundEventKind.Split, 2m, "yfinance")], []),
                Now, new PricesNote("test", PricesNote.Imported, "{}"), new ImportedExport(Now, new string('a', 64)), CancellationToken.None);
        }

        var (code, output) = await ReconcileAsync(world);

        Assert.Equal(1, code);
        Assert.Contains(output, l => l.StartsWith("! VT: the prices show a split of 2 for 1 on 2026-10-16, and the ledger holds 103 shares the evening before and no split that day", StringComparison.Ordinal));
        Assert.Contains("  the end of 2026-10-15, shares of VT: ledger 103, IBKR 103", output);
        // IBKR's statement is not to blame: the ledger and the prices differ.
        Assert.StartsWith("The ledger agrees with IBKR's statement", output[^2], StringComparison.Ordinal);
        Assert.Equal("The ledger and the stored prices differ about a split; nothing was changed. Until the ledger holds the split, the value of that fund is wrong.", output[^1]);
    }

    [Fact]
    public async Task WithoutAnImportOrWithAPaperStatementThereIsNothingToReconcile()
    {
        var world = await NewAsync();

        var none = await ReconcileAsync(world);
        var given = await ReconcileAsync(world, Fixture);
        Assert.Equal((1, "! No statement of IBKR has been imported: there are no books of IBKR to hold this one against. Import it first."), (given.Code, given.Output[1]));
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        var paper = await ReconcileAsync(world, Variant(world, "paper.xml", d => Account(d, "DU0000000")));

        Assert.Equal((1, "No statement of IBKR has been imported, so there is nothing to reconcile: import one first (import-statement <file>)."), (none.Code, none.Output[0]));
        Assert.Equal(1, paper.Code);
        Assert.Contains("! D…000 is a paper (demo) account; the ledger keeps the real account's books. Reconcile with a statement of the real account.", paper.Output);
        var other = await ReconcileAsync(world, Variant(world, "other.xml", d => Account(d, "F0000000")));
        Assert.Equal(1, other.Code);
        Assert.Contains("! The ledger keeps the books of U…000; this statement is of F…000.", other.Output);
    }

    [Fact]
    public async Task WhatTheStatementHoldsAndTheImportCannotReadIsADifference()
    {
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Fixture)).Code);
        var cancelled = Variant(world, "cancelled.xml", d => d.Descendants("Trade").Last().SetAttributeValue("transactionType", "TradeCancel"));

        var (code, output) = await ReconcileAsync(world, cancelled);

        Assert.Equal(1, code);
        Assert.Contains(output, l => l.StartsWith("! The statement holds what the import does not read, so the ledger cannot hold it: 2026-10-15: trade 11013041293 (USD.ILS) is a 'TradeCancel'", StringComparison.Ordinal));
        // The conversion IBKR now calls cancelled: the ledger's legs are named, without saying IBKR is silent on them.
        Assert.Contains(output, l => l.StartsWith("! The ledger holds a FxConversion of 2026-10-15, -100 USD (Flex 11013041293; entry ", StringComparison.Ordinal) && l.Contains("does not state in a form the import reads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinesOfTheStatementTheLedgerDoesNotHoldAreNamed()
    {
        // Only 7 and 8 October were imported; the whole statement says what the ledger lacks.
        var world = await NewAsync();
        Assert.Equal(0, (await ImportAsync(world, Variant(world, "early.xml", Only("20261007", "20261008")))).Code);

        var (code, output) = await ReconcileAsync(world, Fixture);

        Assert.Equal(1, code);
        Assert.Contains("! IBKR's line 11006946599 (Buy of 2026-10-14, -788.35 USD) is not in the ledger: the statement was not imported, or its recording was reversed - import the statement.", output);
        Assert.Equal(45, output.Count(l => l.StartsWith("! IBKR's line ", StringComparison.Ordinal)));
        Assert.Contains("! the end of 2026-10-15, shares of VT: ledger 0, IBKR 103", output);
        await using var db = DbSetup.Create(world.Database);
        Assert.Equal(1, await db.Transactions.CountAsync());
    }

    /// <summary>Answers SendRequest, then the statement: the Flex Web Service when all goes well.</summary>
    private sealed class Ibkr(byte[] statement) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            HttpContent content = Asked.Count == 1
                ? new StringContent("<FlexStatementResponse><Status>Success</Status><ReferenceCode>4242</ReferenceCode><Url>https://gdcdyn.interactivebrokers.com/GetStatement</Url></FlexStatementResponse>")
                : new ByteArrayContent(statement);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task TheStatementIsDownloadedIntoTheDataFolderAndCanThenBeImported()
    {
        var world = await NewAsync();
        var secret = Guid.NewGuid().ToString("N");
        var ibkr = new Ibkr(await File.ReadAllBytesAsync(Fixture));
        await using var app = ITradeHost.Build(
            [.. RunningHost.Args(world.Database, world.DataDir), $"--{FetchStatementCommand.AccessKey}={secret}", $"--{FetchStatementCommand.QueryKey}=123456"]);
        using var output = new StringWriter();

        var code = await FetchStatementCommand.RunAsync(app, output, ibkr, (_, _) => Task.CompletedTask);

        Assert.Equal(0, code);
        var path = Path.Combine(FetchStatementCommand.Folder(world.DataDir), "flex-20261016-084125.xml");
        Assert.Equal(
            [
                $"Downloaded the statement of 2026-10-07 to 2026-10-15 (4 day(s)) to {path}.",
                $"To record it: dotnet run --project src/ITrade.Host -- import-statement \"{path}\"",
            ],
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(await File.ReadAllBytesAsync(Fixture), await File.ReadAllBytesAsync(path));
        Assert.Equal(2, ibkr.Asked.Count);
        Assert.Equal(0, (await ImportAsync(world, path)).Code);
    }

    [Fact]
    public async Task WithoutTheSettingsNothingIsAskedOfIbkr()
    {
        var world = await NewAsync();
        var ibkr = new Ibkr([]);
        await using var app = ITradeHost.Build(RunningHost.Args(world.Database, world.DataDir));
        using var output = new StringWriter();

        Assert.Equal(2, await FetchStatementCommand.RunAsync(app, output, ibkr, (_, _) => Task.CompletedTask));

        Assert.StartsWith("Flex:Token and Flex:QueryId are not set.", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(ibkr.Asked);
    }

    [Fact]
    public async Task AFileThatIsNotAStatementIsNamed()
    {
        var world = await NewAsync();
        var path = Path.Combine(world.DataDir, "error.xml");
        await File.WriteAllTextAsync(path, "<FlexStatementResponse><Status>Fail</Status></FlexStatementResponse>");

        var (code, output) = await ImportAsync(world, path);

        Assert.Equal(1, code);
        Assert.StartsWith($"Cannot read the statement {path}: This is not a Flex statement", output[0], StringComparison.Ordinal);
    }
}
