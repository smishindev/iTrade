---
name: dotnet-conventions
description: How C# code is written in the iTrade solution (src/ITrade.*, tests/ITrade.*) — money and quantities, time and trade dates, project layers, database, HTTP API, logging, secrets and tests. Load before writing or reviewing C# in this repository.
---

# .NET conventions of iTrade

The compiler, analyzers (warnings are errors), architecture tests and hooks enforce most of this;
the rest is here so new code looks like the code around it. `docs/PLAN.md` §3.1 and §4 win over this summary.

## Money and quantities
- `decimal` in C#, `numeric` in SQL — never `double` or `float`, also not in DTOs, JSON, logs or tests.
  The one exception is a *rate* that needs a power or a root (a return per year, a money-weighted return):
  computed in `double`, rounded to ten places and handed on as `decimal` (`PeriodReturns.RateScale`). The
  amounts it is computed from and applied to stay `decimal`.
- Use the domain types (`ITrade.Domain`): `Money(Amount, Currency)`, `Price`, `Quantity`, `FxRate`.
  Amounts of different currencies do not add up — convert with an `FxRate` (it carries date and source).
- Amounts stay exact; rounding happens only where asked and names its rule: `Money.RoundToMinor(rule)`,
  `FloorToMinor()`, `Price.FloorToTick` / `CeilToTick`, `Quantity.FloorTo`, `Quantity.Affordable(...)`.
  Round the way that is safe for the owner (a buy never rounds up past the money available).
- Database precision: every `decimal` is `numeric(18,6)` by convention (`ITradeDbContext.ConfigureConventions`);
  a table that needs another one says so on the column — amounts `numeric(18,4)`, FX rates `numeric(12,6)`.
- `FxRate.Convert` is exact in the direction of the quote and divides the other way: round where the amount
  is shown or booked, never in between.
- Fee and tax numbers are assumptions: a change carries a source and a date in a comment.

## Time
- NodaTime only. Instants are `Instant` (stored as `timestamptz`, UTC); calendar dates are `LocalDate`
  (stored as `date`). No `DateTime`, `DateTimeOffset` or `DateOnly`.
- The clock is `NodaTime.IClock`, injected: `SystemClock` in the Host, `FakeClock` in tests. Domain code
  never reads a clock (architecture test).
- A trading day is `TradeDate` — the New York calendar date, not a UTC date: `TradeDate.Of(instant)`.
  Zones: `Zones.NewYork`, `Zones.Israel`. Schedules are written in New York time.
- Sessions and settlement come from `IExchangeCalendar`. `WeekdayCalendar` is a stand-in without
  holidays — not for money decisions.

## Layers (checked by `tests/ITrade.ArchitectureTests`)
| Project | Holds | May depend on |
|---|---|---|
| `ITrade.Domain` | types and rules, pure functions | nothing; no clock, network, database, files |
| `ITrade.Application` | use cases, interfaces of stores and gateways | Domain |
| `ITrade.Infrastructure` | EF Core / PostgreSQL, imports, files | Application, Domain |
| `ITrade.Broker.IBKR` | the only `IBApi` code, the only `IBrokerGateway` | Application, Domain |
| `ITrade.Host` | HTTP API, sign-in, background services, web files | all |

Only `ExecutionService` may use `IOrderSender`. On an unknown order state: reconcile, never resend.
Broker code is reviewed by the `execution-reviewer` agent; load the `ibkr-api` skill first.

## Database
- `ITradeDbContext`, snake_case names, one configuration in `DbSetup`. Typed ids and domain types
  map through converters registered in `ConfigureConventions`.
- Schema changes: a new migration (`dotnet ef migrations add <Name> --project src/ITrade.Infrastructure
  --startup-project src/ITrade.Infrastructure --output-dir Persistence/Migrations`) in its own commit.
  Applied migrations are never edited: a migration on `origin/main` is frozen (edit hook and pre-commit), and
  before regenerating one that is not pushed, `dotnet run --project src/ITrade.Host -- check` must still list it
  as pending. The Host applies migrations at start; `dotnet ef database update|drop` is not used.
- The working database is read with SELECT at most; it is changed only through the Host. Tests take a
  throw-away database (`NewEmptyDatabaseAsync`).
- `audit_events` is append-only (database triggers). Write to it; never update or delete.
- `transactions` — the accounting ledger — is append-only too (triggers reject UPDATE, DELETE, TRUNCATE). Write
  through `TransactionLedger`: `RecordAsync` (a repeated import adds nothing; an entry is known by holding place,
  source, external id and revision; what it leaves out it reports) and `ReverseAsync` (a mistake is cancelled by a reversal, then the right entry is
  recorded). Amounts are signed effects; the order within a trade date is the order of recording (`sequence`).
  The accounting rules are in the `portfolio-accounting` skill.
- A reference from a ledger row to another ledger row is mapped many-to-one with a unique index, not one-to-one:
  EF Core "moves" a one-to-one dependent by updating the old row.
- A store used by background work writes with single statements (`ExecuteSqlAsync`, `ExecuteUpdateAsync`)
  or its own scope, so one unit's unsaved changes cannot ride along with another's.

## HTTP API
- Minimal APIs in `ITrade.Host`, grouped by feature (`Auth/`, `Events/`, `Jobs/`, `Web/`), wired from
  `ITradeHost.Build`. Everything requires sign-in unless marked `.AllowAnonymous()`.
- Every changing request needs the antiforgery header; errors are problem details
  (`Results.Problem(statusCode:, title:)`); no exception text leaves the server.
- Name operations (`.WithName`) and declare answers (`.Produces`, `.ProducesProblem`). After an API
  change: refresh `web/openapi/v1.json` (integration test with `ITRADE_UPDATE_OPENAPI=1`), then
  `npm run gen:api` in `web/`.
- The Host listens on `127.0.0.1` only. PAPER is the only mode until the pilot gate.
- One Host per database: `InstanceLock` (a PostgreSQL advisory lock) makes a second one refuse to start.
- The session lasts 12 hours from sign-in, without sliding renewal; sign-out and a new password end every
  session, including open event connections.
- A daily job (`IDailyJob`) is idempotent and throws when it cannot do its work: the scheduler retries hourly, and
  after five failures the date is blocked and holds back the later ones. `check` shows a stuck date with its cause;
  the owner releases it with `retry-job <job> <date>`. Do not swallow a failure to keep a job green.
  The one exception is something outside that is merely away (no network, a service down): throw
  `TemporaryFailureException` — the date is retried hourly for as long as it takes and is never blocked. A fault in
  what was received is not temporary.
- Outgoing calls: only the Bank of Israel rates service so far (`AddHttpClient`, a timeout, a size limit, no data
  about the owner). A new outgoing call is named in the task and gets a switch like `ITrade:UpdateFxRates`, which
  the test Hosts turn off (`RunningHost.Args`, `tests/ITrade.E2EHost`).
- An answer from outside is parsed strictly — columns by name, invariant culture, plausible ranges — and anything
  unexpected is refused: a wrong number stored is worse than a failed job.
- A background service never lets an exception escape `ExecuteAsync` (that would stop the Host), and treats
  `OperationCanceledException` as a stop only when its own stopping token is cancelled — a timeout is a failure.
- `dotnet run --project src/ITrade.Host -- check` builds the Host as a normal start does and reports what is
  missing, without changing anything. The tests run the Host in-process as "Testing"; the owner runs the real
  executable in Development — anything that depends on that difference needs this check, not only a test.

## Logging and secrets
- Log through `[LoggerMessage]` partial methods (CA1848 is an error); Serilog writes compact JSON.
- Never log a password, token or connection string. The masking formatter is a safety net, not a licence.
- Secrets live in user-secrets or `appsettings.Local.json` (git-ignored). No credential literal in
  code or tests — generate test secrets at run time (`Guid.NewGuid().ToString("N")`); a hook rejects literals.
- No real IBKR account number anywhere: `DU0000000` / `U0000000`.

## Style
- File-scoped namespaces, `sealed` by default, primary constructors, `internal` unless another
  project needs the type. Private fields `_camelCase`; constants and `private static readonly`
  fields `PascalCase`.
- Culture: `CultureInfo.InvariantCulture` and `StringComparison.Ordinal` always (CA1305/CA1307 are errors).
- Comments say why, in English. No commented-out code.

## Tests
- `tests/ITrade.UnitTests` — Domain and Application, no I/O, `FakeClock`; property tests with CsCheck
  where a rule must hold for all inputs (rounding, sizing).
- `tests/ITrade.IntegrationTests` — real PostgreSQL 18 through Testcontainers and the real Host on a
  free port (`RunningHost`); a test that needs its own data takes `NewEmptyDatabaseAsync()`.
- `tests/ITrade.ReplayTests` — recorded broker messages, no network. `tests/ITrade.BrokerContractTests`
  — against a paper account, by hand.
- No test needs the network or the owner's database (the "Testing" environment keeps user-secrets out).
- A guard is not done until a deliberate violation has been seen to fail its test.

## Commands (repository root)
`dotnet build ITrade.slnx` · `dotnet test tests/ITrade.UnitTests` · `dotnet test tests/ITrade.ArchitectureTests`
· `dotnet test tests/ITrade.IntegrationTests` (Docker) · `dotnet format ITrade.slnx`. A running Host
locks the Debug build — stop it first.
