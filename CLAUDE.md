# iTrade — instructions for Claude Code

Personal systematic-investing research for one owner (Israeli resident, 10,000 ILS, own money).
Goal: find out whether any rule-based strategy beats buy-and-hold VT **in ILS, after costs
and tax**. "No, hold the index" is an acceptable outcome. Read `docs/SCOPE.md` once per session
if you have not.

## Where things are
- `config/universe.toml` instruments · `config/costs.toml` fee/tax assumptions · `config/project.toml` phase (owner-only)
- `src/itrade/data/` ingest → raw snapshot → quality → curated · `src/itrade/costs/` cost & tax model · `src/itrade/cli.py`
- `docs/GATES.md` phase exit criteria · `docs/research-log.md` every variant tried · `docs/data-notes.md` reviewed warnings

## Commands
- `uv sync` · `uv run pytest` · `uv run ruff check src tests` · `uv run ruff format src tests`
- `uv run itrade ingest|quality|costs|sql` (see README)
- Add dependencies with `uv add`, never `pip install`.

## Working rules
- **Stay inside the current phase** (`config/project.toml`). Do not build later-phase pieces
  "while we're here". Broker/order code is hook-blocked until phase 4.
- **Simplest thing that answers the question.** Python + pandas + DuckDB + Parquet + cron.
  No services, queues, cloud, Rust/Go, web UI unless SCOPE.md is updated first.
- Strategy, signal or backtest work → load the `research-integrity` skill first; afterwards run
  `/review-research`. Costs always on; results in ILS after tax, next to the benchmark.
- Never present a backtest number without: number of variants tried, period, costs included,
  benchmark difference. Never call a result "profit" before out-of-sample + live evidence.
- Tests must not need the network (mark exceptions `@pytest.mark.network`).
- Data files are never edited by hand; fix code and re-ingest.
- Fee and tax numbers are assumptions: when changing them, add source + date in a comment.

## Guardrails (enforced by `.claude/hooks/`)
- PreToolUse: blocks edits under `data/`, edits to `config/project.toml`, secret files,
  hard-coded credentials, broker code before phase 4; blocks deleting raw data, reading `.env`, `pip install`.
- PostToolUse: ruff format/fix on edited Python; flags look-ahead patterns in strategy code.
- Stop: runs ruff + pytest when src/tests/config changed; a failure sends you back to fix it.
- If a hook blocks you, do not work around it — explain to the user what it protects.

## Slash commands, agents, skills
`/gate-check [n]` (gate-keeper) · `/ingest [tickers]` (data-auditor) · `/cost <usd> <price> [ticker]` ·
`/research-log <text>` · `/review-research` (quant-reviewer).
Skills: `research-integrity`, `data-pipeline`, `cost-model`.
