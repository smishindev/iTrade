# iTrade — instructions for Claude Code

Personal portfolio platform for one owner (Israeli tax resident, not oleh, 10,000 ILS + contributions, own money).
**Plan v4 (2026-10-09):** money is held in a broad index; the platform does portfolio accounting, Israeli taxes,
contributions and rebalancing, and sends **owner-approved** purchases through IBKR. Three pre-registered trading
hypotheses were rejected in phase 1 (docs/spikes/hypothesis-H3.md); the research engine is frozen.

**Read first, once per session:** `PROJECT_STATUS.md` (what works, decisions, gaps, what is next), then
`docs/PLAN.md` (master plan, Russian), `docs/SCOPE.md`, `docs/GATES.md`. `ARCHITECTURE.md` is the system as built,
`QUICK_REFERENCE.md` the commands, recipes and gotchas, `PRE_FLIGHT_CHECKLIST.md` the steps around a change.
The owner communicates in Russian — answer in Russian.

## Target stack (decided — docs/PLAN.md §2)
React 19 + TypeScript (web/) · C# / .NET 10 + ASP.NET Core (src/ITrade.*) · PostgreSQL 18 (Docker) ·
Parquet snapshots in `D:\ITradeData` · official IBKR TWS API (C#) via IB Gateway.
Python (`python/src/itrade/`) = the frozen research archive of phase 1, reference oracle (`accounting/`), free data
ingest, statistics. **Python never trades.**

## Where things are now (phase 3 — accounting, plan v4; the phase itself is in `config/project.toml`)
- `src/ITrade.Domain` money, time, ids, instruments, holding places, transactions, books (lots, positions, cash) ·
  `src/ITrade.Application` use cases (daily jobs, instrument catalogue, transaction ledger, books, exchange rates,
  closing prices, value on a date, returns of a period) ·
  `src/ITrade.Infrastructure` EF Core / PostgreSQL, Bank of Israel rates · `src/ITrade.Broker.IBKR` (empty until phase 5) ·
  `src/ITrade.Host` API, sign-in, events hub, background jobs, serves the web interface · `tests/ITrade.*`
- `web/` React interface (MUI, React Router, TanStack Query, generated API client) · `web/e2e` browser tests
- `deploy/` PostgreSQL in Docker, backup and restore scripts · `tools/ci.py` everything at once · `tools/roadmap.py`
- `config/universe.toml` instruments · `config/costs.toml` fee/tax assumptions · `config/project.toml` phase (owner-only)
- `python/src/itrade/data/` ingest → raw snapshot → quality → curated · `python/src/itrade/costs/` cost model · `python/src/itrade/cli.py`
- Research archive: `python/src/itrade/strategies`, `python/src/itrade/backtest`, `docs/research-log*.md` (H1–H3, all **closed**),
  `docs/spikes/` · `docs/data-notes.md` reviewed data warnings · `docs/accountant-questions.md`
- v3 plan, scope, gates and roadmap (active trading): `docs/archive/`

## Commands
- Python project in `python/` (since P2.1.01): from there `uv sync` · `uv run pytest` · `uv run ruff check src tests tools`
  · `uv run ruff format src tests tools` · `uv run itrade ingest|quality|costs|sql` (see README);
  from the repo root the same with `uv run --directory python …`.
- Add dependencies with `uv add` (in `python/`), never `pip install`.
- Everything at once: `python tools/ci.py` (Python, .NET with integration tests, web, browser tests; needs Docker) ·
  `--fast` without Docker · `--only python|dotnet|web|e2e`. Run it before a phase gate.
- .NET (repo root): `dotnet build ITrade.slnx` · `dotnet test tests/ITrade.UnitTests` · `dotnet test tests/ITrade.ArchitectureTests`
  · `dotnet test tests/ITrade.IntegrationTests` (needs Docker) · `dotnet run --project src/ITrade.Host` (a running Host locks the
  Debug build — stop it first) · `dotnet run --project src/ITrade.Host -- set-password` ·
  `dotnet run --project src/ITrade.Host -- check` (builds the Host as a real start does and reports what is missing;
  changes nothing). Tests run the Host in-process as "Testing"; only `check` and a real start exercise the owner's launch.
  `dotnet run --project src/ITrade.Host -- import-instruments config/instruments.csv` loads the instrument catalogue
  (repeatable); the catalogue itself is generated: `uv run python tools/make_instrument_catalogue.py` in `python/`.
  `dotnet run --project src/ITrade.Host -- update-fx-rates` brings the Bank of Israel USD/ILS rates up to today (what the
  daily job `fx-rates` does; needs the internet; `--accept-changes <code> "<why>"` is the owner's step when the bank
  changed a stored rate — never run it for the owner) · `-- import-prices` loads the closing prices the Python
  pipeline exported (`itrade ingest --universe etf_pullback_v1` — the catalogue's funds — or `itrade export-closes`;
  `--accept-changes <code>` again is the owner's) · `-- retry-job <job> <yyyy-MM-dd>` releases a daily job that `check`
  shows as blocked.
- Database (PowerShell, repo root): `.\deploy\backup.ps1` (dump to `<DataDir>\backups`, read-only) ·
  `.\deploy\restore.ps1 -File <dump> -Verify` (restores into a temporary database and removes it). Restore never
  overwrites an existing database; switching the Host to a restored one is the owner's step.
- Web (in `web/`, Node 24): `npm ci` · `npm run dev` · `npm run check` (= `gen:api:check`, `format:check`, `lint`, `test`,
  `build`) · `npm run gen:api` after an API change (see web/README.md) · `npm run e2e` (Playwright against a real Host with a
  throw-away database; needs Docker). Interface texts are English; components come from MUI.

## Working rules
- **Stay inside the current phase** (`config/project.toml`, names in docs/GATES.md). The owner moves the project to
  the next phase after `/gate-check` passes; a gate criterion without a task is raised with the owner, not skipped.
- **Research is frozen.** No new strategy/hypothesis, variant or backtest unless the owner first approves a new scope
  (SCOPE) — then the full phase-1 protocol (pre-registration, calibrated control, quant-reviewer, one final run).
  2021-01-04…2026-09-30 is still unseen; the closed ledgers lock it for H1–H3.
- Only `ExecutionService` sends orders; on unknown order state reconcile, never resend.
- The ledger (`transactions`) is append-only and written only through `TransactionLedger`; a mistake is reversed,
  never edited. Positions, lots and balances are derived from it.
- Exchange rates (`fx_rates`) are append-only too and come only through `FxRates`: a rate the bank changed waits
  for the owner's explicit acceptance and is then a new revision. A converted amount keeps its `AppliedFxRate`
  (carried forward? provisional?).
- Closing prices (`closing_prices`, append-only) are for valuation only and come only through `ClosingPrices`:
  the price as traded, never the vendor's split-adjusted close, and only of a finished session. Whether the
  exchange was open on a day is read from `market_sessions` (the exchange calendar), not guessed from the prices.
  A value keeps its `PriceOnDate`; a missing or unavailable price is shown as such, never replaced by an older one.
- The value of the portfolio comes only through `PortfolioValuation`: a position without a usable price is not
  valued, and a sum that leaves something out is not called the total (`PortfolioTotal.Total` is null then).
- Returns come only through `PortfolioPerformance` / `PeriodReturns`: time-weighted and money-weighted are two
  figures, each named; a contribution is not a gain; no return is computed over a value that is not complete.
  The Python oracle (`python/src/itrade/accounting/returns.py`) writes the fixture the C# tests are held to.
- The index is compared as a portfolio (`IndexPortfolio`): the owner's own contributions, on their days, into the
  index fund at that day's close, its dividends put back after withholding. Never compare with a fund's price
  alone. `fund_events` (dividends, splits) are the market's facts about a fund, not what the owner received.
- Tax and accounting rules do not wait for the accountant (owner, 2026-10-12): choose, record as an assumption with
  source and date, label the result unverified.
- Money is `decimal`/`numeric`, never floating point, in C# and SQL. Times in UTC; schedules in America/New_York.
- Strategy, signal or backtest work → load the `research-integrity` skill first; afterwards `/review-research`.
  Pre-register variants (max 20); the final period 2021–2026 runs once.
- Never present a backtest number without: variants tried, period, costs included, sample size, control comparison.
  Never call a result "profit" before final-period + live evidence.
- Each task: goal, allowed change area, invariants, required tests, expected result. Strategy, risk,
  broker adapter and DB schema changes go in separate commits.
- A task is not done until the documents it made stale are updated in the same commit: `PROJECT_STATUS.md`
  (something works, was verified or decided, a gap opened or closed — and its "Last updated" date),
  `ARCHITECTURE.md` (a table, endpoint, command, job, outgoing call or guard), `QUICK_REFERENCE.md` (a command,
  a recipe, a gotcha that cost time), `README.md` (setup, everyday commands). A command written there was run
  as written.
- Tests must not need the network (mark exceptions `@pytest.mark.network`). The Host calls out only to the Bank of
  Israel's public rates service; test Hosts run with `ITrade:UpdateFxRates=false` and bring a stand-in source.
- Data files are never edited by hand; fix code and re-ingest.
- Fee and tax numbers are assumptions: when changing them, add source + date in a comment.
- Do not rewrite text files with Windows PowerShell `Get-Content`/`Set-Content` (it corrupts UTF-8); use Edit or Python.

## Guardrails (enforced by `.claude/hooks/` and `.githooks/pre-commit`)
- PreToolUse: blocks edits under the data folder (`D:\ITradeData` per `config/paths.toml`; old `data/` too), edits to `config/project.toml`, secret files, hard-coded credentials,
  **real IBKR account numbers** (use `DU0000000`), broker code in Python (always), IBKR API code in C# outside
  `src/ITrade.Broker.IBKR/` and `spikes/ibkr-feasibility/`, live ports 4001/7496 (in broker code and in settings)
  before phase 6 — the pilot phase of plan v4; the owner opens them by setting `phase = 6` (decided 2026-10-12);
  blocks deleting raw data, reading `.env`, `pip install`; since phase 3 (the database holds accounting records) also
  writing SQL to the working database from the shell (SELECT is fine; changes go through the Host), removing Docker
  volumes, `dotnet ef database drop|update`, and edits to a migration that is on `origin/main` (add a new one).
- PostToolUse: ruff format/fix + look-ahead flags on Python; `dotnet format whitespace` on C#; prettier + eslint on `web/`.
- Stop: ruff + pytest when python/config/tools changed; `dotnet build` for changed spikes; solution build + unit and
  architecture tests when src/tests changed; prettier check, eslint, tsc and vitest when `web/` changed. A failure sends you back.
- pre-commit: the same checks for what the commit touches, plus the integration tests when it touches
  `src/ITrade.Infrastructure/` or `tests/ITrade.IntegrationTests/` (Docker running), and no change to a pushed migration.
- Status line shows phase, roadmap progress and the next task; SessionStart injects the same.
- If a hook blocks you, do not work around it — explain to the user what it protects.
- Spike/broker run logs go to `spikes/**/out/` (git-ignored); only anonymised fixtures are committed.
  Local account numbers and ports live in `appsettings.Local.json` (git-ignored) or user-secrets.

## Slash commands, agents, skills
Work goes task by task through `docs/roadmap/` (rules: `docs/roadmap/README.md`, helper `python tools/roadmap.py`):
`/next-task` · `/task <ID>` · `/roadmap-status`. Commit messages start with the task ID.
`/gate-check [n]` (gate-keeper) · `/ingest [tickers]` (data-auditor) · `/cost <usd> <price> [ticker]` ·
`/research-log <text>` · `/review-research` (quant-reviewer).
Agent `execution-reviewer`: after any change to code that proposes, approves, sends or reconciles orders, before it is committed.
Agent `accounting-reviewer`: after any change to code that records or computes the owner's money (ledger, statement
imports, lots, positions, valuation, returns, reconciliation, tax amounts), before it is committed.
Skills: `research-integrity` (strategy/backtest), `data-pipeline`, `cost-model`, `ibkr-api` (anything touching IBKR),
`dotnet-conventions` (any C# in `src/` or `tests/`), `portfolio-accounting` (ledger, imports, lots, positions, valuation,
reconciliation, any balance told to the owner), `israeli-tax` (the Tax module, `[tax]` in `config/costs.toml`,
any tax figure told to the owner).
