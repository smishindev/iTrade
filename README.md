# iTrade

A personal platform for one owner's long-term index portfolio. Own capital only, no clients.

**What it is (plan v4, 9 October 2026).** The money sits in a broad index and is held for years. The platform
keeps the books: positions and tax lots across the places the money is held, Israeli taxes, regular
contributions and rebalancing — and it sends purchases to Interactive Brokers only after the owner approved
each one.

**What it is not.** A trading system. Phase 1 tested three pre-registered rule-based strategies against
simply holding the index, after every cost; all three were rejected by their own rules
([docs/spikes/hypothesis-H3.md](docs/spikes/hypothesis-H3.md)). That research is kept as a frozen archive.

## Documentation

| Document | Read it for |
|---|---|
| [PROJECT_STATUS.md](PROJECT_STATUS.md) | What works and what verified it, the decisions and their reasons, known gaps, what is next, what waits for the owner. **Start here when coming back to the project** |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Stack, addresses and settings, layers, the Host, the tables, how the books are kept, what enforces the rules |
| [QUICK_REFERENCE.md](QUICK_REFERENCE.md) | Commands, "is it alive", recipes (a migration, a job, an endpoint, a command), gotchas |
| [PRE_FLIGHT_CHECKLIST.md](PRE_FLIGHT_CHECKLIST.md) | The process before, during and after a change; when to stop and ask |
| [docs/PLAN.md](docs/PLAN.md) · [docs/SCOPE.md](docs/SCOPE.md) · [docs/GATES.md](docs/GATES.md) | The plan, what is in and out of scope, the criteria of each phase gate (Russian) |
| [docs/roadmap/](docs/roadmap/README.md) | Every task with its dated note; `python tools/roadmap.py status` |
| [CLAUDE.md](CLAUDE.md), `.claude/skills/` | The binding rules for Claude Code and the conventions of each area |

The plan, [CLAUDE.md](CLAUDE.md) and the skills are the standard. The four documents at the top describe the
system those rules govern; where they disagree, the rules win and the document is wrong.

## What you need

.NET SDK 10 · Node 24 · Python with [uv](https://docs.astral.sh/uv/) · Docker Desktop · PowerShell.

## First start

```powershell
# 1. Database: PostgreSQL 18 in Docker, reachable from this machine only.
#    Create deploy\.env from deploy\env.example with your own password, then:
docker compose -f deploy/docker-compose.yml up -d

# 2. Tell the Host how to reach it (stored in your user profile, not in the repository):
dotnet user-secrets set "ConnectionStrings:ITrade" "Host=127.0.0.1;Port=55432;Database=itrade;Username=itrade;Password=<yours>" --project src/ITrade.Host

# 3. Set the owner's password (typed hidden, 12 characters or more):
dotnet run --project src/ITrade.Host -- set-password

# 4. Build the interface and run:
cd web; npm ci; npm run build; cd ..
dotnet run --project src/ITrade.Host
```

Open `https://127.0.0.1:7120`. The Host listens on this machine only, applies database migrations at start and
runs in PAPER mode. To get rid of the browser's certificate warning: `dotnet dev-certs https --trust`.

## Everyday commands

| Command | What it does |
|---|---|
| `python tools/ci.py` | build and test everything (needs Docker); `--fast` without Docker |
| `dotnet run --project src/ITrade.Host` | the application (interface + API); stop it before building |
| `dotnet run --project src/ITrade.Host -- check` | is everything in place to start? (interface, database, password) — changes nothing |
| `dotnet run --project src/ITrade.Host -- import-instruments config/instruments.csv` | load the instrument catalogue; running it again changes nothing |
| `dotnet run --project src/ITrade.Host -- update-fx-rates` | bring the Bank of Israel USD/ILS rates up to today (the daily job does the same; needs the internet). If the bank changed a rate that is already stored, it says so and stores nothing; after checking the bank's page, run the line it prints: `... update-fx-rates --accept-changes <code> "<what you checked>"` |
| `uv run --directory python itrade ingest --universe etf_pullback_v1`, then `dotnet run --project src/ITrade.Host -- import-prices` | refresh the market data and load the closing prices (as traded, finished sessions only) the platform values the portfolio with; running it again adds nothing. Run the ingest after the New York close (23:00 in Israel) to get the day's close |
| `dotnet run --project src/ITrade.Host -- retry-job <job> <yyyy-MM-dd>` | let a daily job that `check` shows as blocked be tried again |
| `cd web; npm run dev` | interface with hot reload on `http://127.0.0.1:5173`, next to a running Host |
| `.\deploy\backup.ps1` | back up the database to `<data folder>\backups` |
| `.\deploy\restore.ps1 -File <dump> -Verify` | check a backup by restoring it into a temporary database |
| `python tools/roadmap.py status` | progress by phase |

More: [web/README.md](web/README.md) (interface, API client, browser tests) and [python/README.md](python/README.md).

## Layout

```
src/ITrade.Domain          money, time, instruments, transactions, lots and positions - pure types and rules
src/ITrade.Application     use cases: daily jobs, instrument catalogue, ledger, books, exchange rates
src/ITrade.Infrastructure  PostgreSQL through EF Core, migrations, the Bank of Israel rates client
src/ITrade.Broker.IBKR     the only code that may talk to the broker (empty until phase 5)
src/ITrade.Host            HTTP API, sign-in, events, background jobs; serves the interface
tests/                     unit, architecture, integration (Docker) and the Host for browser tests
web/                       React interface (TypeScript, MUI), browser tests in web/e2e
python/                    data ingest, quality checks, cost model, frozen research archive
deploy/                    PostgreSQL in Docker, backup and restore
config/                    instruments, fee and tax assumptions, data folder, project phase (owner-only)
tools/                     ci.py (everything at once), roadmap.py
docs/                      plan, scope, gates, roadmap, research reports, questions for the accountant
```

Data (market snapshots, logs, backups) lives outside the repository, in the folder named in
`config/paths.toml`.

## Safety rules that are enforced, not just written down

- Only one service may send orders, and only after the owner's approval; an order in an unknown state is
  reconciled, never resent. Python never talks to a broker.
- Live broker ports are blocked until the pilot phase; no real account number or credential may enter the repository.
- Money is `decimal` everywhere; the audit journal, the accounting ledger and the exchange rates are append-only
  in the database itself.

Hooks in `.claude/hooks/` and `.githooks/pre-commit` (enable once with `git config core.hooksPath .githooks`),
architecture tests and database triggers check these on every change.
