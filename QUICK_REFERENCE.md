# iTrade — Quick Reference

The cookbook. For *why* things are shaped this way see [ARCHITECTURE.md](ARCHITECTURE.md); for the
process around a change see [PRE_FLIGHT_CHECKLIST.md](PRE_FLIGHT_CHECKLIST.md); for first-time setup,
[README.md](README.md).

---

## Commands

All from the repository root unless stated. The shell is PowerShell; Git Bash works for the same
commands.

```powershell
# everything at once - the same command before every "done"
python tools/ci.py                 # 16 steps: Python, .NET with integration tests, web, the Host's start check, browser tests (needs Docker)
python tools/ci.py --fast          # without Docker
python tools/ci.py --only dotnet   # or python | web | e2e

# .NET
dotnet build ITrade.slnx
dotnet test tests/ITrade.UnitTests
dotnet test tests/ITrade.ArchitectureTests
dotnet test tests/ITrade.IntegrationTests      # real PostgreSQL in Docker, about half a minute
dotnet format ITrade.slnx

# the application and its commands (a running Host locks the Debug build - stop it before building)
dotnet run --project src/ITrade.Host
dotnet run --project src/ITrade.Host -- check                # can it start? changes nothing
dotnet run --project src/ITrade.Host -- set-password
dotnet run --project src/ITrade.Host -- import-instruments config/instruments.csv
dotnet run --project src/ITrade.Host -- update-fx-rates
dotnet run --project src/ITrade.Host -- import-prices        # after: uv run --directory python itrade ingest --universe etf_pullback_v1
dotnet run --project src/ITrade.Host -- fetch-statement      # IBKR's Flex statement into <data>/flex (token in user-secrets)
dotnet run --project src/ITrade.Host -- import-statement "D:\ITradeData\flex\flex-20261016-101638.xml"   # record it; again adds nothing
dotnet run --project src/ITrade.Host -- reverse-entry <entry id> "<why>"   # the owner's step: cancel an entry IBKR restated, then import again
dotnet run --project src/ITrade.Host -- retry-job fx-rates 2026-10-12

# interface (in web/, Node 24)
npm ci
npm run dev        # http://127.0.0.1:5173 next to a running Host
npm run check      # API client is current, format, lint, tests, build
npm run gen:api    # after an API change
npm run e2e        # Playwright against a real Host with a throw-away database

# Python (in python/, or from the root with: uv run --directory python ...)
uv sync
uv run pytest
uv run ruff check src tests tools
uv run itrade ingest | quality | export-closes | costs | sql

# database
docker compose -f deploy/docker-compose.yml up -d
.\deploy\backup.ps1
.\deploy\restore.ps1 -File <dump> -Verify

# roadmap
python tools/roadmap.py status
python tools/roadmap.py next --all
python tools/roadmap.py show P3.3.02
```

## Is it alive?

```powershell
dotnet run --project src/ITrade.Host -- check
```

It builds the Host exactly as a real start does and prints one line per thing: environment, mode,
address, data folder, interface, database and pending migrations, owner password, daily jobs that
are stuck, how old the exchange rates and the closing prices are. `OK` or `PROBLEMS: n` at the end; a line starting with
`!` is a problem and says what to run.

With the Host running: `curl.exe -k https://127.0.0.1:7120/health` → status, mode, database check.

## Reading the working database

Read only. Changes go through the Host; a hook blocks writing SQL from the shell.

```powershell
docker exec itrade-postgres psql -U itrade -d itrade -c "select name, trade_date, status, attempts, left(error, 120) from job_runs order by trade_date desc limit 10"
docker exec itrade-postgres psql -U itrade -d itrade -c "select count(*), max(rate_date) from fx_rates"
docker exec itrade-postgres psql -U itrade -d itrade -c "select count(*), count(distinct instrument_id), max(session) from closing_prices"
docker exec itrade-postgres psql -U itrade -d itrade -c "select calendar, count(*), min(session), max(session) from market_sessions group by 1"
docker exec itrade-postgres psql -U itrade -d itrade -c "select kind, count(*), min(day), max(day) from fund_events group by 1"
docker exec itrade-postgres psql -U itrade -d itrade -c "select occurred_at, actor, kind from audit_events order by occurred_at desc limit 10"
docker exec itrade-postgres psql -U itrade -d itrade -c 'select migration_id from \"__EFMigrationsHistory\" order by 1'
```

Every table but the migration history has a lower-case name and needs no quotes. That one does, and
Windows PowerShell drops plain double quotes on the way to `psql` — hence the `\"`.

---

## Recipes

### A schema change
1. Change the row class and its mapping in `ITradeDbContext` (constraints with names that say the
   rule: `ck_<table>_<what_must_hold>`).
2. `dotnet ef migrations add <Name> --project src/ITrade.Infrastructure --startup-project src/ITrade.Infrastructure --output-dir Persistence/Migrations`
3. Triggers and functions go into the migration by hand (`migrationBuilder.Sql`), with their `DROP`
   in `Down`.
4. An integration test that plants a violation of each new constraint and sees it refused.
5. Its own commit: `<task id> (schema): ...`, before the commit with the logic.

A migration that is on `origin/main` is frozen — add another. Before regenerating one that is not
pushed, `check` must still list it as pending on the working database.

### A rule of the ledger, the books or the rates
1. Load the `portfolio-accounting` skill; the rule goes into the domain type (`Transaction`,
   `PortfolioBooks`) or the use case (`TransactionLedger`, `FxRates`), not into a store or an endpoint.
2. If a row could now be wrong for good, repeat the rule in the database (CHECK or trigger) and add
   its planted violation to `LedgerRulesTests` or the rates' tests.
3. Hand-worked example in the test, to the last digit; a property (CsCheck) if the rule must hold
   for every input.
4. Break the rule on purpose once and see a test fail.
5. `accounting-reviewer` before the commit.

### A daily job
1. `IDailyJob` in `ITrade.Application` — idempotent: the same date may run again after a crash.
2. Throw when the work cannot be done. Something outside that is merely away →
   `TemporaryFailureException` (retried hourly, never blocked); anything else counts towards the
   five attempts.
3. Register it in the Host behind a setting that test Hosts turn off if it reaches outside.
4. Test with the real scheduler and a `FakeClock`; see `tests/ITrade.IntegrationTests/Fx`.

### A call to something outside
Name it in the task first: it is the second outgoing call of a platform that has one. Then:
`AddHttpClient` with a timeout and a size limit, strict parsing (columns by name, invariant culture,
plausible ranges, refuse anything unexpected), a switch like `ITrade:UpdateFxRates` that
`RunningHost.Args` and `tests/ITrade.E2EHost` turn off, the service's real answer saved under
`tests/ITrade.UnitTests/Fixtures/` byte for byte with its SHA-256 asserted.

### An API endpoint
1. Minimal API in `src/ITrade.Host/<Feature>/`, wired from `ITradeHost.Build`; `.WithName`,
   `.Produces`, `.ProducesProblem`. Everything needs sign-in unless marked `.AllowAnonymous()`;
   every changing request needs the antiforgery header.
2. Errors are `Results.Problem(statusCode:, title:)`; no exception text leaves the server.
3. Refresh the snapshot: run the API integration test with `ITRADE_UPDATE_OPENAPI=1`, then
   `npm run gen:api` in `web/`. `npm run check` fails while the client is stale.

### A command of the Host
A static class next to its feature (`Name`, `RunAsync(app, ..., output)`), dispatched in
`Program.cs` before the normal start. It builds the Host and does not start it; it migrates first if
it writes. Then run it exactly as the owner would, on the real database, before telling the owner it
works.

### A screen
Page in `web/src/pages`, route in `routes.tsx`, menu entry in `shell/navigation.ts`; data through
the generated client and TanStack Query; MUI components; English texts. A number shown to the owner
carries its date, currency and source — and for a converted amount, whether the rate was carried
forward or provisional.

### An instrument
Add it to the generator's list, `uv run python tools/make_instrument_catalogue.py` in `python/`,
then `import-instruments config/instruments.csv`. A listing that contradicts what is stored is
reported and left alone; a ticker change is a rename with its date.

---

## Gotchas hit in this codebase

| Symptom | Cause |
|---|---|
| Build fails with a locked `ITrade.Host.exe` | The Host is running. Stop it, then build |
| `dotnet run -- import-instruments config/instruments.csv` cannot find the file | `dotnet run --project` starts in the project folder; the command looks in parent folders now. A new command taking a path needs the same |
| Tests are green and the real Host does not start | Tests run the Host as "Testing". Run `check`, or the real thing |
| A second Host refuses to start | By design: one Host per database (instance lock) |
| `iTrade cannot start: ConnectionStrings:ITrade is not set` | User-secrets hold it; see README, *First start* |
| A second reversal fails with 42501 instead of 23505 | A one-to-one EF relationship "moves" the dependent by updating the old row. Between ledger rows: many-to-one plus a unique index |
| Entries of one day come back in a different order | Ids made in one millisecond are not ordered; the ledger's order is `sequence` |
| `db.Database.GetConnectionString()` has no password | Npgsql drops it after opening. Pass the connection string explicitly |
| `ON DELETE RESTRICT` gives 23001, not 23503 | PostgreSQL's own code for a restrict violation |
| A fund's price before a split looks halved in the vendor's data | The vendor adjusts `close` back in time. The platform uses the price as traded (`itrade export-closes`); never value past holdings with the curated `close` |
| `import-prices` asks for an acceptance code | More than 20 stored prices changed, one changed by more than 2%, or a new close is more than 30% from the one before. An export gone wrong more often than a real price. Find out why before the owner accepts the code it prints |
| `import-prices` refuses: the export was written before the one imported last | An older export would put corrected prices back. `itrade export-closes` again, then import |
| `import-prices` refuses: fetched before that session ended | The data was fetched during New York trading hours (the evening in Israel) by something other than the current exporter, or on a half day before 16:00 New York. `itrade ingest` again after the close |
| A fund's last bar is not in the export | It was fetched while the session was open; that bar is not a close. The next ingest after the close (23:30 in Israel) brings it |
| A fund is "behind" after an ingest | Its newest bar failed the quality check, so nothing of that fetch was taken - not even the good days before it. Seen 12 October 2026 with EWC: the vendor's bar had its open a cent below the day's low, during the session and still after the close. Ingest again the next day; the vendor usually repairs such a bar |
| `import-prices` says the export left out thousands of stored prices | The plain `itrade ingest` was run: it fetches the nine research tickers from 2005 and shortens the history of the ones the catalogue shares (SPY, IEF, TLT, GLD, VT). Nothing is lost - stored prices stay. The catalogue's funds are `--universe etf_pullback_v1` |
| A price is "missing" for a day although the fund trades | The vendor has no bar for that session (the export's manifest lists them under `missing_sessions`), or the fund is behind - `check` names it |
| `Cannot read the closing prices: ... names no table of fund events: it was written by an older exporter` | The export in the data folder is from before dividends and splits were exported. `uv run --directory python itrade export-closes`, then import |
| The portfolio "beats the index" by about 2% a year | It is being compared with the fund's price. The comparison is `PerformanceReport.Index` - the same contributions into the fund, with its dividends |
| `Cannot read the closing prices: ... not the one its manifest vouches for` | A table (prices or sessions) was changed after it was exported, or the export was caught half-written. `itrade export-closes` again |
| The integration tests fail with `53300: too many clients` | Every test database keeps pooled connections for a while. The test server runs with `max_connections=500` (`PostgresFixture`) |
| `test_fixture_the_platform_reads_is_what_the_oracle_says` fails after a change of the returns oracle | The fixture the C# tests read is written by the oracle: in `python/`, `ITRADE_UPDATE_FIXTURES=1 uv run pytest tests/test_returns_oracle.py`, then run the .NET unit tests - they must agree with the new file |
| A rate or an amount prints as `3.055000` | That is the column's scale. Format with `0.######` where it is shown |
| The job "succeeds" but the rate of today is missing | The bank publishes in the afternoon, Israel time. The figure is marked provisional until it arrives |
| `update-fx-rates` says the bank now states another rate | Nothing was stored. After checking the bank's page, run the line it prints (`--accept-changes <code> "<why>"`) — the owner's step |
| A daily job is `blocked` in `check` | Five real faults. Deal with the cause it names, then `retry-job <job> <date>` |
| A Flex statement downloaded after midnight in Israel lacks the day's trades | IBKR's "last 30 days" ends with yesterday **in New York**. Download after 07:00 Israel time (midnight in New York) to get the previous New York day |
| `import-statement`: "is a paper (demo) account" | By design: a paper account's statement never enters the owner's books (the ledger keeps every entry). The token made under the paper login downloads the paper account; the real account needs its own Flex query and token |
| `import-statement`: "the entries move ... and IBKR's Cash Report ..." | Something of that day moved cash that the import does not read, or the query lost a section. Nothing was recorded. Look at the day in the file before changing any code |
| `import-statement`: "At the evening of ... IBKR's cash is ... and the ledger's ..." | The statement starts later than the ledger ends (a gap between downloads: "last 30 days" slides), or it is the first one and the account had history before it. Download a period that starts where the ledger ends (Flex query → Run with a custom date range) |
| `import-statement`: "The ledger would leave out Flex <id>: the ledger holds this line differently" | IBKR restated a line already recorded (a commission finalised, another settle date). The owner checks the statement, then `reverse-entry <entry id> "<why>"` with the id it names, and imports again: the line comes in as its next revision |
| `import-statement`: "IBKR holds ... of VT and the ledger with this statement ..." | Shares moved without cash (a split, a transfer) or a trade is not in the ledger. Nothing was recorded; look at the statement's CorporateActions and Transfers |
| `import-statement` / `fetch-statement`: "(1 period(s), not broken out by day)", "checked only as a whole" | The Flex query has no Breakout by Day: IBKR sends the 30 days as one period, and no single day's cash can be held against IBKR's. Turn on Breakout by Day in the query (Delivery Configuration). Nothing is wrong with what was recorded |
| `import-statement`: a deposit "is dated ..., booked on ...; a row whose dates differ" | IBKR booked it later than its date. A deposit booked 1–4 days later inside the period is recorded on the day of the booking; anything else (a withdrawal, a longer gap, a row listed in the period of its dated day) is refused until such a row has been seen |
| `import-statement`: "has no row per currency ... Download the statement again" | IBKR left the per-currency rows out of the Cash Report of the first or last day (it varies from download to download) while the account holds more than one currency |
| `import-statement` refuses a 'TradeCancel', a corporate action, a kind of cash transaction | Never seen on a real statement, so not guessed. The first real one is how the import learns it: keep the file, write a test from it (anonymised), then extend `FlexLedgerEntries` |
| Flex times look 7 hours off | Flex writes New York time without a zone (`dateTime="20261015;154021"` was 22:40 in Israel) |
| IBKR's `twr` in `ChangeInNAV` is 100 times the platform's | It is in percent; the platform's returns are fractions |
| MTD/YTD fields of an old day's Flex statement show later trades | They are of the day of the download, not of the statement's day. The import does not read them |
| A commit is refused over a migration | It is on `origin/main`. Add a new migration |
| A hook blocks an edit or a command | It protects data, secrets, the phase file or the working database. Do not work around it; say what it protects |
| A check on an ISIN or a test secret is refused by a hook | It looks like an account number or a credential. Use `DU0000000`, an obviously fake ISIN, or generate the secret at run time |
| `git push` fails for Claude | No credentials in that environment. The owner pushes (VS Code "Sync Changes") |
| Text files show broken Russian | Written with Windows PowerShell `Set-Content`. Use the Edit tool or Python |
| A Python patch script in a Bash heredoc breaks on quotes or `\n` | Write the script to a file and run it |
| `LF will be replaced by CRLF` on every commit | Expected on this machine (`* text=auto`). Shell hooks and recorded fixtures are pinned in `.gitattributes` |
| Browser shows a certificate warning | The development certificate is not trusted: `dotnet dev-certs https --trust` |
| `uv` warns about an old virtual-environment path in VS Code | Select the interpreter `python\.venv\Scripts\python.exe` |
