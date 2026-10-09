# iTrade — instructions for Claude Code

Personal portfolio platform for one owner (Israeli tax resident, not oleh, 10,000 ILS + contributions, own money).
**Plan v4 (2026-10-09):** money is held in a broad index; the platform does portfolio accounting, Israeli taxes,
contributions and rebalancing, and sends **owner-approved** purchases through IBKR. Three pre-registered trading
hypotheses were rejected in phase 1 (docs/spikes/hypothesis-H3.md); the research engine is frozen.

**Read first, once per session:** `docs/PLAN.md` (master plan, Russian), `docs/SCOPE.md`, `docs/GATES.md`.
The owner communicates in Russian — answer in Russian.

## Target stack (decided — docs/PLAN.md §2)
React 19 + TypeScript (web/) · C# / .NET 10 + ASP.NET Core (src/ITrade.*) · PostgreSQL 18 (Docker) ·
Parquet snapshots in `D:\ITradeData` · official IBKR TWS API (C#) via IB Gateway.
Python (current `python/src/itrade/`, moving to `python/` in phase 2) = research spike A, reference oracle,
free data ingest, statistics. **Python never trades.**

## Where things are now (end of phase 1, plan v4)
- `config/universe.toml` instruments · `config/costs.toml` fee/tax assumptions · `config/project.toml` phase (owner-only)
- `python/src/itrade/data/` ingest → raw snapshot → quality → curated · `python/src/itrade/costs/` cost model · `python/src/itrade/cli.py`
- Research archive: `python/src/itrade/strategies`, `python/src/itrade/backtest`, `docs/research-log*.md` (H1–H3, all **closed**),
  `docs/spikes/` · `docs/data-notes.md` reviewed data warnings · `docs/accountant-questions.md`
- v3 plan, scope, gates and roadmap (active trading): `docs/archive/`

## Commands
- Python project in `python/` (since P2.1.01): from there `uv sync` · `uv run pytest` · `uv run ruff check src tests tools`
  · `uv run ruff format src tests tools` · `uv run itrade ingest|quality|costs|sql` (see README);
  from the repo root the same with `uv run --directory python …`.
- Add dependencies with `uv add` (in `python/`), never `pip install`. (.NET commands are added in phase 2.)

## Working rules
- **Stay inside the current phase** (`config/project.toml`, names in docs/GATES.md). Next: phase 2 (foundation) once
  the owner moves the phase after `/gate-check 1`.
- **Research is frozen.** No new strategy/hypothesis, variant or backtest unless the owner first approves a new scope
  (SCOPE) — then the full phase-1 protocol (pre-registration, calibrated control, quant-reviewer, one final run).
  2021-01-04…2026-09-30 is still unseen; the closed ledgers lock it for H1–H3.
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

## Guardrails (enforced by `.claude/hooks/` and `.githooks/pre-commit`)
- PreToolUse: blocks edits under the data folder (`D:\ITradeData` per `config/paths.toml`; old `data/` too), edits to `config/project.toml`, secret files, hard-coded credentials,
  **real IBKR account numbers** (use `DU0000000`), broker code in Python (always), IBKR API code in C# outside
  `src/ITrade.Broker.IBKR/` and `spikes/ibkr-feasibility/`, live ports 4001/7496 before phase 8 (v3 numbering;
  becomes phase 6 in task P2.6.03 with the owner's consent);
  blocks deleting raw data, reading `.env`, `pip install`.
- PostToolUse: ruff format/fix + look-ahead flags on Python; `dotnet format whitespace` on C#.
- Stop: ruff + pytest when src/tests/config/tools changed; `dotnet build` for changed spikes. A failure sends you back.
- Status line shows phase, roadmap progress and the next task; SessionStart injects the same.
- If a hook blocks you, do not work around it — explain to the user what it protects.
- Spike/broker run logs go to `spikes/**/out/` (git-ignored); only anonymised fixtures are committed.
  Local account numbers and ports live in `appsettings.Local.json` (git-ignored) or user-secrets.

## Slash commands, agents, skills
Work goes task by task through `docs/roadmap/` (rules: `docs/roadmap/README.md`, helper `python tools/roadmap.py`):
`/next-task` · `/task <ID>` · `/roadmap-status`. Commit messages start with the task ID.
`/gate-check [n]` (gate-keeper) · `/ingest [tickers]` (data-auditor) · `/cost <usd> <price> [ticker]` ·
`/research-log <text>` · `/review-research` (quant-reviewer).
Skills: `research-integrity` (strategy/backtest), `data-pipeline`, `cost-model`, `ibkr-api` (anything touching IBKR).
