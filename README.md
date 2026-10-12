# iTrade

A personal platform for one owner's long-term index portfolio. Own capital only, no clients.

**What it is (plan v4, 9 October 2026).** The money sits in a broad index and is held for years. The platform
keeps the books: positions and tax lots across the places the money is held, Israeli taxes, regular
contributions and rebalancing — and it sends purchases to Interactive Brokers only after the owner approved
each one.

**What it is not.** A trading system. Phase 1 tested three pre-registered rule-based strategies against
simply holding the index, after every cost; all three were rejected by their own rules
([docs/spikes/hypothesis-H3.md](docs/spikes/hypothesis-H3.md)). That research is kept as a frozen archive.

- Plan: [docs/PLAN.md](docs/PLAN.md) · scope: [docs/SCOPE.md](docs/SCOPE.md) · phase gates: [docs/GATES.md](docs/GATES.md)
- Tasks: [docs/roadmap/](docs/roadmap/README.md) (`python tools/roadmap.py status`)
- Instructions for Claude Code: [CLAUDE.md](CLAUDE.md)

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
| `cd web; npm run dev` | interface with hot reload on `http://127.0.0.1:5173`, next to a running Host |
| `.\deploy\backup.ps1` | back up the database to `<data folder>\backups` |
| `.\deploy\restore.ps1 -File <dump> -Verify` | check a backup by restoring it into a temporary database |
| `python tools/roadmap.py status` | progress by phase |

More: [web/README.md](web/README.md) (interface, API client, browser tests) and [python/README.md](python/README.md).

## Layout

```
src/ITrade.Domain          money, time, ids, instruments, holding places - pure types and rules
src/ITrade.Application     use cases (daily jobs; accounting, taxes, plan in later phases)
src/ITrade.Infrastructure  PostgreSQL through EF Core, migrations
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
- Money is `decimal` everywhere; the audit journal in the database is append-only.

Hooks in `.claude/hooks/` and `.githooks/pre-commit` (enable once with `git config core.hooksPath .githooks`),
architecture tests and database triggers check these on every change.
