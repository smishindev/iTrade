# iTrade — Architecture

What the system is made of today, how its parts talk, and where the sharp edges are. This is the
**as-built** picture; what is planned is in [docs/PLAN.md](docs/PLAN.md) (Russian), and what is done
and what is next in [PROJECT_STATUS.md](PROJECT_STATUS.md). For commands and recipes see
[QUICK_REFERENCE.md](QUICK_REFERENCE.md); for setup, [README.md](README.md).

The binding rules are [CLAUDE.md](CLAUDE.md), the skills in `.claude/skills/` and the plan. Where this
document and they disagree, they win and this file has a bug.

**Two things explain most of the shape.** The platform keeps one person's own money, so nothing may
move it or misstate it silently: orders need the owner's approval, the books are append-only, and a
wrong number is refused rather than smoothed over. And it is built and maintained mostly by an AI
agent in short sessions, so the rules are enforced by tools — hooks, architecture tests, database
triggers, reviewer agents — not left to memory.

---

## Stack

| Concern | Choice | Version |
|---|---|---|
| Runtime | .NET (`global.json`, SDK 10.0.302) | 10 |
| Web | ASP.NET Core minimal APIs, SignalR, OpenAPI | 10.0.12 |
| ORM | EF Core + Npgsql, snake_case names | 10.0.12 / 10.0.3 |
| Time | NodaTime (no `DateTime`, no `DateOnly`) | 3.3.5 |
| Database | PostgreSQL in Docker | 18 |
| Logs | Serilog, compact JSON, secrets masked | 10.0.0 |
| Interface | React + TypeScript + Vite | 19.2 / 6.0 / 8.3 |
| | MUI, React Router, TanStack Query | 9.5 / 8.4 / 5.104 |
| | openapi-typescript + openapi-fetch (generated client), React Hook Form + Zod | 7.13 / 0.17 |
| Tests | xUnit, CsCheck (properties), ArchUnitNET, Testcontainers; Vitest, Playwright | |
| Python (`python/`) | uv, ruff, pytest, DuckDB over Parquet — data ingest, cost model, frozen research | |
| Broker (phase 5) | official IBKR TWS API (C#) through IB Gateway | not built yet |

`InvariantGlobalization` is on for the whole solution: the Host never formats with the system's
culture.

---

## Addresses and settings

| | |
|---|---|
| Host (interface + API) | `https://127.0.0.1:7120` — loopback only, HTTPS only; Kestrel endpoints from settings are refused |
| Interface in development | `http://127.0.0.1:5173` (Vite), proxying the API to a running Host |
| Database | PostgreSQL 18, container `itrade-postgres`, `127.0.0.1:55432`, database `itrade`, named volume `itrade-pg` |
| Data folder | `D:\ITradeData` (`config/paths.toml`, `ITRADE_DATA_DIR`): `raw/`, `curated/`, `logs/`, `backups/` — outside the repository |
| Browser-test Host | port 7199 with a throw-away PostgreSQL (`tests/ITrade.E2EHost`) |
| IB Gateway (phase 5) | paper 4002; live 4001 is blocked by hooks until the owner sets `phase = 6` |

Settings come from `appsettings.json`, `appsettings.Paper.json`, user-secrets (never in the
"Testing" environment), environment variables and the command line. Section `ITrade`: `Mode`
(only `Paper` is accepted before the pilot), `Port`, `DataDir`, `HeartbeatSeconds`,
`JobsTickSeconds`, `UpdateFxRates`.

**Secrets live in `dotnet user-secrets`** of `src/ITrade.Host`: `ConnectionStrings:ITrade` today;
`Flex:Token` and `Flex:QueryId` when the Flex import arrives. The database password is also in
`deploy/.env` (git-ignored; Claude neither reads nor writes it). Nothing secret is in the repository,
and hooks reject credential literals and real IBKR account numbers (placeholders: `DU0000000`).

The project phase is `config/project.toml`. Only the owner edits it; hooks key off it.

---

## Solution layout

```
src/ITrade.Domain          pure types and rules: Money, Price, Quantity, FxRate, TradeDate, Instrument,
                           HoldingPlace, Transaction, PortfolioBooks (lots, positions, cash)
src/ITrade.Application     use cases and the interfaces of what they need: daily jobs and their scheduler,
                           InstrumentCatalogue, TransactionLedger, PortfolioBooksReader, FxRates
src/ITrade.Infrastructure  EF Core / PostgreSQL (context, rows, stores, migrations), instance lock,
                           Bank of Israel rates client, instrument catalogue file, weekday calendar
src/ITrade.Broker.IBKR     the only place IBKR API code may live - empty until the Flex import (phase 3)
                           and the adapter (phase 5)
src/ITrade.Host            the executable: HTTP API, sign-in, events hub, background services, commands,
                           and the built interface as static files
tests/ITrade.UnitTests            Domain, Application, parsers - no I/O
tests/ITrade.ArchitectureTests    the layer rules below
tests/ITrade.IntegrationTests     real PostgreSQL (Testcontainers) and the real Host on a free port
tests/ITrade.ReplayTests          recorded broker messages (phase 5)
tests/ITrade.BrokerContractTests  against a paper account, by hand (phase 5)
tests/ITrade.E2EHost              the Host that Playwright drives
web/                              the interface; web/e2e - browser tests
python/                           data pipeline, cost model, frozen research archive
```

| Project | May depend on | Must not |
|---|---|---|
| Domain | nothing | read a clock, the network, a database, files; use `DateOnly`/`TimeOnly` |
| Application | Domain | reference Infrastructure, Broker or Host |
| Infrastructure | Application, Domain | |
| Broker.IBKR | Application, Domain | be bypassed: no `IBApi` anywhere else |
| Host | all | |

`tests/ITrade.ArchitectureTests` checks these in every build, each proven once with a planted
violation. Only `ExecutionService` may use `IOrderSender` (both arrive in phase 5).

---

## The Host

**Start** (`ITradeHost.StartAsync`): take the instance lock (a PostgreSQL advisory lock — a second
Host on the same database refuses to start) → apply pending migrations → listen. A refusal of our own
(a missing setting, an unsafe one, another Host) is printed as one plain sentence, not a stack trace.

**Request pipeline:** request log → exception handler (every error leaves as RFC 9457 problem
details; no exception text reaches the browser) → status pages → interface files, CSP and security
headers → same-origin check for hubs → sign-in and antiforgery → endpoints.

**Sign-in.** One owner account, password of 12+ characters set with the `set-password` command.
Cookie session: HttpOnly, Secure, SameSite=Strict, 12 hours from sign-in with no sliding renewal.
Every changing request needs the antiforgery header `X-XSRF-TOKEN`. Five failures lock sign-in for
15 minutes. A new password or a sign-out ends every session, open event connections included.

**HTTP surface today** — small on purpose; screens arrive in phase 6:

| Address | |
|---|---|
| `GET /health` | anonymous: status, mode, database check |
| `GET /api/auth/session`, `POST /api/auth/login`, `POST /api/auth/logout` | session and antiforgery token |
| `/hubs/events` (SignalR) | heartbeat every 15 s; closes when the session ends |
| `GET /openapi/v1.json` | behind sign-in; the web client is generated from its snapshot `web/openapi/v1.json` |
| everything else that is not a file | the interface (`index.html`) |

**Commands** of the same executable (`dotnet run --project src/ITrade.Host -- <command>`):
`check [--offline]`, `set-password`, `import-instruments <file>`, `update-fx-rates`,
`import-prices [file]`, `retry-job <job> <date>`. A command builds the Host but does not start it.

**Tests run the Host in-process as "Testing".** That hid two broken real launches in phase 2, so
`check` exists, CI runs it, and anything that depends on the difference is run for real before it is
called done.

---

## Data

| Table | Holds | Notes |
|---|---|---|
| `settings` | key → JSON | the owner's password hash lives here |
| `audit_events` | what happened, who did it | **append-only** (triggers) |
| `job_runs` | one row per daily job and trade date | status, attempts, last error |
| `instruments`, `instrument_former_tickers` | what can be held; renames by date | 29 ETFs loaded from `config/instruments.csv` |
| `holding_places` | where money is held | in use: IBKR only |
| `transactions` | the accounting ledger | **append-only**; CHECKs and a trigger repeat the domain rules |
| `fx_rates` | official exchange rates, with revisions | **append-only**; 5332 rates since 2005 |
| `closing_prices` | closes as traded, with revisions | **append-only**; 185,273 prices of 29 funds since 1993; only for a day that is a session |
| `market_sessions` | the days an exchange calendar was open | **append-only**; 8,482 sessions of XNYS since 1993, from the exports of prices |
| `price_imports` | which export of prices was imported, and when | **append-only**; an export older than the latest is refused |
| `fund_events` | a fund's dividends (per share, as paid) and splits, with revisions | **append-only**; 2,538 events of 28 funds since 1993; only for a day that is a session; from the same export as the prices |

Conventions: snake_case; money, quantities and prices `numeric(18,6)`, rates `numeric(12,6)`, never
float; instants `timestamptz` (UTC), dates `date`; typed ids `uuid` (v7). One configuration point
(`DbSetup`), typed ids and domain types mapped by converters in `ITradeDbContext`.

**Append-only means the database refuses** UPDATE, DELETE and TRUNCATE on those tables, also for
the owner role. A ledger mistake is reversed by a new entry; a rate the bank corrected is a new
revision; nothing is edited. Because a wrong row is permanent, the rules of the domain types are
repeated as CHECK constraints and triggers, each tested with a planted violation.

**Migrations** (`src/ITrade.Infrastructure/Persistence/Migrations`, eleven so far) are applied by the
Host at start. One that is on `origin/main` is frozen — a schema change is a new migration, in its own
commit. `dotnet ef database update|drop` is not used.

**The working database** is the owner's. Claude reads it with `SELECT` at most; changes go through
the Host. Backups are `deploy/backup.ps1` (manual until phase 7), verified by restoring into a
temporary database.

---

## Accounting

```
statement / manual entry ──► TransactionLedger.RecordAsync ──► transactions (append-only)
                                      ▲ ReverseAsync                 │
                                                                     ▼
                         PortfolioBooks.From(ledger, date)  ──► positions with lots, cash by currency,
                         (pure function, nothing stored)        every sale matched to purchases
                                                                     │
Bank of Israel ──► FxRates.UpdateAsync ──► fx_rates ──► FxRates.OnAsync(date) ──► AppliedFxRate
                                                         (carried forward? provisional?)

curated bars ──► itrade export-closes ──► prices + sessions + manifest ──► import-prices
(Python)         (split adjustment undone,     (SHA-256 of both tables)          │
                  finished sessions only)                                        ▼
                 ClosingPrices.OnAsync(instrument, date) ──► PriceOnDate   closing_prices,
                 (of the session / carried / missing / unavailable)        market_sessions

PortfolioValuation.AsOfAsync(date) = books of the date + a PriceOnDate per instrument + the AppliedFxRate
                 ──► PortfolioValue: by holding place and in all, in dollars and in shekels
                     (nothing stored; what could not be valued or converted is named, not guessed)

PortfolioPerformance.OverAsync(first, last) = the ledger's deposits and withdrawals + the value around each
                 ──► PeriodReturns in dollars and in shekels: time-weighted, money-weighted, gain
                 ──► the same flows put into the index fund (IndexPortfolio: closes + fund_events) ──► its returns
```

- **Ledger.** One entry is one signed effect on shares and/or cash at one holding place. A line of a
  source is known by (place, source, external id, revision) and recorded once however often a
  statement is imported; a line that now says something else is reported, not applied.
- **Books.** Derived every time, so they cannot drift from the ledger. Lots are matched FIFO — an
  assumption, a parameter, named in the result. What the ledger cannot mean is refused.
- **Rates.** A stored rate is never overwritten. A change by the bank, or a new rate that moves
  more than 8% from the one before, waits for the owner's acceptance by code.
- **Prices.** For valuation only. The vendor adjusts its closes back in time for splits; the books
  hold the shares really held on a date, so the platform stores the price as traded that day — and
  only of a session that was over when the data was fetched. Whether the exchange was open on a day
  comes from the exchange calendar the export carries, not from the prices: a closed day carries the
  last close, a session without a price is a gap that is said, never filled with an older price. Many
  or large changes of stored prices, and a new close far from the one before it, wait for the
  owner's acceptance by code.
- **Value.** Worked out every time from the books, the prices and the rate of the date; for looking at,
  never for tax. A position without a usable price - none, or one from before a split - has no value,
  and a sum that leaves something out is not called the total. Amounts are added per currency and
  converted once, without rounding. What is provisional is said in words.
- **Returns.** Time-weighted (what a unit of money made that was there all the time) and money-weighted
  (the rate that explains what the owner's own contributions became), per currency. The portfolio is
  valued only around the owner's flows. A contribution counts from the start of its day, a withdrawal
  at the end of its day. Per-year figures from a year on. The same definitions are worked out another
  way in Python (`python/src/itrade/accounting/returns.py`), and its fixture holds the C# code.
- **Index.** Compared as a portfolio, not as a price: the owner's contributions and withdrawals, on
  their days, buy and sell the index fund (`ITrade:IndexTicker`) at that day's close; its dividends,
  after withholding, buy more; a split changes the shares. Measured by the same returns. No comparison
  is given rather than a wrong one - a missing close, a fund outside the catalogue, a withdrawal the
  index portfolio cannot follow are said.

The rules in full are the `portfolio-accounting` skill. Tax figures (phase 4) follow `israeli-tax`:
every number there is an unverified assumption until a specialist confirms it.

---

## Daily jobs

`DailyJobScheduler` runs each `IDailyJob` once per exchange session (New York dates), including the
sessions missed while the Host was off, in order. A job that fails is retried hourly; after five
real faults the date is blocked and holds back later ones until the owner releases it
(`retry-job`). A job that merely could not reach something outside throws
`TemporaryFailureException` and is retried for as long as it takes. `check` shows what is stuck.

One job so far: `fx-rates`, due 11:00 New York. The session calendar is `WeekdayCalendar` — a
stand-in without holidays, not for money decisions.

**Outgoing calls.** Exactly one: the Bank of Israel's public rates service, read without a key,
sending nothing about the owner. Test Hosts run with `ITrade:UpdateFxRates=false`.

---

## Interface (`web/`)

React app served by the Host from `wwwroot` (built by `npm run build`). Shell with a fixed header,
a mode badge (PAPER / LIVE / NOT CONNECTED — from the server, never a default in the page), six
menu entries that are placeholders until phase 6, sign-in page, theme toggle. API calls go through
the generated client with antiforgery and error middleware; a 401 forgets cached owner data.
Interface texts are English; components come from MUI.

---

## Python and data

`python/src/itrade/`: `data/` (ingest → immutable raw snapshot → quality checks → curated Parquet
with a manifest and SHA-256), `costs/` (the reference cost model, IBKR Tiered), `strategies/` and
`backtest/` (the frozen archive of phase 1: three hypotheses, all rejected). **Python never trades**
and holds no broker code. Curated prices are for valuation only — never for tax, never for an order.
The platform reads them through one export: `data/closes.py` writes the closes as traded, the
exchange's sessions they cover and a manifest that vouches for both tables, and the Host imports
that. Nothing runs the pipeline by itself yet.

---

## What enforces the rules

| Layer | Enforces |
|---|---|
| `.claude/hooks/guard_edits.py`, `guard_shell.py` | no edits to data, the phase file, secrets, pushed migrations; no credentials or real account numbers; no broker code in Python or outside `Broker.IBKR`; no live ports before phase 6; no writes to the working database from the shell; no removal of Docker volumes |
| `post_edit.py`, `stop_gate.py` | formatting on every edit; build, lint and fast tests before a turn can end |
| `.githooks/pre-commit` | the same per commit, plus integration tests when the database layer changed |
| `tools/ci.py` | all 16 steps, including "the Host starts as the owner runs it" and browser tests |
| Architecture tests | the layer table above |
| Database triggers and CHECKs | append-only tables, the ledger's and the rates' own rules |
| Reviewer agents | `accounting-reviewer` (money recorded or computed), `execution-reviewer` (orders), `gate-keeper` (phase gates), `quant-reviewer`, `data-auditor` — read-only, run before a commit |

---

## Not built yet, and known gaps

Priority and dates are in [PROJECT_STATUS.md](PROJECT_STATUS.md); listed here so nobody rediscovers
them.

- **No statement import, no valuation, no screens with data, no broker adapter, no tax module** —
  phases 3 (rest), 4, 5, 6.
- **The Host connects as the database owner**, who could disable the triggers. A least-privilege
  role is planned for the operations phase.
- **The Host does not notice an applied migration it does not know** (an older build against a newer
  database): EF Core skips such rows silently.
- **Order within a trade date is the order of recording.** Enough for one source and monthly
  purchases; a second source or intraday trades need an execution time on the entry.
- **Books "as of a date" do not remember what was known then**: a later reversal, a late fee or a
  late rate changes a past date's figure. Tax reports will have to keep what they were computed with.
- **Corporate actions**: only splits are worked into lots. Cash in lieu is counted and flagged
  (`NotInTheLots`); spin-offs, mergers and transfers cannot be written down yet.
- **Backups are manual** until phase 7.
