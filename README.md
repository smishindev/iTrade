# iTrade

Personal systematic-investing research platform. Own capital only, no clients.

The question this project answers: **can any rule-based strategy beat simply holding a
global index ETF, after every cost and Israeli tax, measured in shekels?** If not, the
answer is to hold the ETF — and that is a successful outcome.

- Scope and assumptions: [docs/SCOPE.md](docs/SCOPE.md)
- Phase gates and stop criteria: [docs/GATES.md](docs/GATES.md)

## Setup

The Python project (data, checks, cost model, research archive) lives in `python/`; `src/` is for the
C# solution (phase 2).

```sh
cd python
uv sync
uv run pytest
```

## Commands (from `python/`)

```sh
uv run itrade ingest                 # download the universe, snapshot, validate, promote
uv run itrade ingest SPY VT -v       # specific tickers, show warnings
uv run itrade quality -v             # re-check curated data
uv run itrade costs --notional 2700 --price 650 --ticker SPY
uv run itrade sql "select ticker, count(*), min(date), max(date) from bars group by 1"
```

## Layout

```
config/            universe.toml (instruments), costs.toml (fee/tax assumptions)
python/src/itrade/data/   sources -> raw snapshot -> quality checks -> curated parquet
python/src/itrade/costs/  commission, spread, slippage, FX, Israeli capital-gains tax
data/              generated, git-ignored (raw/ is immutable)
docs/              scope, gates
```
