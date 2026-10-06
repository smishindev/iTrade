# iTrade — instructions for Claude Code

Personal active-trading platform for one owner (Israeli tax resident, not oleh, 10,000 ILS, own money).
It finds ideas with a formalised strategy, shows reasons/risk/costs, executes **owner-approved** trades
through IBKR, and measures results after costs and Israeli tax.

**Read first, once per session:** `docs/PLAN.md` (master plan, Russian), `docs/SCOPE.md`, `docs/GATES.md`.
The owner communicates in Russian — answer in Russian.

## Target stack (decided — docs/PLAN.md §2)
React 19 + TypeScript (web/) · C# / .NET 10 + ASP.NET Core (src/ITrade.*) · PostgreSQL 18 (Docker) ·
Parquet snapshots in `D:\ITradeData` · official IBKR TWS API (C#) via IB Gateway.
Python (current `src/itrade/`, moving to `python/` in phase 2) = research spike A, reference oracle,
free data ingest, statistics. **Python never trades.**

## Where things are now (phase 1)
- `config/universe.toml` instruments · `config/costs.toml` fee/tax assumptions · `config/project.toml` phase (owner-only)
- `src/itrade/data/` ingest → raw snapshot → quality → curated · `src/itrade/costs/` cost model · `src/itrade/cli.py`
- `docs/research-log.md` every variant tried · `docs/data-notes.md` reviewed data warnings

## Commands
- `uv sync` · `uv run pytest` · `uv run ruff check src tests` · `uv run ruff format src tests`
- `uv run itrade ingest|quality|costs|sql` (see README)
- Add dependencies with `uv add`, never `pip install`. (.NET commands are added in phase 2.)

## Working rules
- **Stay inside the current phase** (`config/project.toml`, names in docs/GATES.md). Phase 1 = spikes A
  and B only; do not start the .NET platform before G1 is passed.
- One strategy core: the C# core is the source of truth; the Python oracle must agree with it.
- Only `ExecutionService` sends orders; on unknown order state reconcile, never resend.
- Money is `decimal`/`numeric`, never floating point, in C# and SQL. Times in UTC; schedules in America/New_York.
- Strategy, signal or backtest work → load the `research-integrity` skill first; afterwards `/review-research`.
  Pre-register variants (max 20); the final period 2021–2026 runs once.
- Never present a backtest number without: variants tried, period, costs included, sample size, control comparison.
  Never call a result "profit" before final-period + live evidence.
- Each task: goal, allowed change area, invariants, required tests, expected result. Strategy, risk,
  broker adapter and DB schema changes go in separate commits.
- Tests must not need the network (mark exceptions `@pytest.mark.network`).
- Data files are never edited by hand; fix code and re-ingest.
- Fee and tax numbers are assumptions: when changing them, add source + date in a comment.
- Do not rewrite text files with Windows PowerShell `Get-Content`/`Set-Content` (it corrupts UTF-8); use Edit or Python.

## Guardrails (enforced by `.claude/hooks/`)
- PreToolUse: blocks edits under `data/`, edits to `config/project.toml`, secret files, hard-coded credentials,
  broker code in Python (always), IBKR API code in C# outside `src/ITrade.Broker.IBKR/` and
  `spikes/ibkr-feasibility/`, live ports 4001/7496 before phase 8; blocks deleting raw data, reading `.env`, `pip install`.
- PostToolUse: ruff format/fix on edited Python; flags look-ahead patterns in strategy code.
- Stop: runs ruff + pytest when src/tests/config changed; a failure sends you back to fix it.
- If a hook blocks you, do not work around it — explain to the user what it protects.

## Slash commands, agents, skills
`/gate-check [n]` (gate-keeper) · `/ingest [tickers]` (data-auditor) · `/cost <usd> <price> [ticker]` ·
`/research-log <text>` · `/review-research` (quant-reviewer).
Skills: `research-integrity`, `data-pipeline`, `cost-model`.
