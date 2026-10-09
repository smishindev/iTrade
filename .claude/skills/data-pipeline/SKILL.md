---
name: data-pipeline
description: How iTrade market data is fetched, stored, validated and queried. Load when adding an instrument or data source, running or debugging `itrade ingest`/`itrade quality`, interpreting a data-quality warning, or reading bars for research.
---

# Data pipeline

```
source.fetch() -> data/raw/<source>/<TICKER>/<utc-stamp>.parquet   (immutable, kept even if rejected)
               -> validate_bars()                                   (python/src/itrade/data/quality.py)
               -> data/curated/bars/<TICKER>.parquet + manifest.json (only if no ERROR)
```

## Commands
- `uv run --directory python itrade ingest [TICKERS...] [-v]` — fetch, snapshot, validate, promote. Exit 1 if any ticker was not promoted.
- `uv run --directory python itrade quality [TICKERS...] -v` — re-validate curated files.
- `uv run --directory python itrade sql "<query>"` — DuckDB; view `bars(date, open, high, low, close, adj_close, volume, dividends, splits, ticker)`.
- In Python: `Store().read_bars("SPY")`.

## Rules
- Never edit or delete anything in `data/` (hooks block it). Fix the code, re-ingest.
- `close` from Yahoo is split-adjusted only; use `adj_close` for returns, `close` for share counts and order sizing.
- USD/ILS is `ILS=X` (ILS per 1 USD). Use its `close` only; its OHLC is noisy.
- Each curated file must trace back to a raw snapshot via `manifest.json` (`raw_snapshot`, `raw_sha256`).

## Adding an instrument
1. Add an `[[instruments]]` block to `config/universe.toml` with a realistic `half_spread_bps`.
2. `uv run --directory python itrade ingest <TICKER> -v`.
3. Every warning → a row in `docs/data-notes.md` with an explanation.

## Adding a data source
1. Implement the `DataSource` protocol in `python/src/itrade/data/sources.py` returning `BAR_COLUMNS`
   (use `normalize_bars` if the vendor is Yahoo-shaped), register it in `SOURCES`.
2. Unit-test it with a recorded/fake response — tests must not hit the network
   (mark unavoidable ones `@pytest.mark.network`).
3. A second source is most valuable for cross-checking: compare `adj_close` returns between
   sources and flag days that differ by > 0.5%.

## Interpreting warnings
| Check | Usually means | Action |
|---|---|---|
| return_outlier | Bad split/dividend adjustment, or a real crash/rally day | Compare with a second source or news; note in data-notes |
| split_event | Corporate action | Confirm `adj_close` has no jump that day |
| missing_sessions (few) | Vendor gap | Accept if < 1%; never forward-fill prices for trading signals |
| ohlc_inconsistent on FX | Quote snapshots | Expected; only `close` is used |
| stale | Vendor stopped updating / delisted | Investigate before using recent data |
