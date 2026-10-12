---
description: Refresh market data, validate it, and explain any warnings
argument-hint: "[TICKER ...]"
allowed-tools: Bash(uv run itrade:*), Bash(uv run --directory python itrade:*), PowerShell(uv run itrade:*), PowerShell(uv run --directory python itrade:*)
---

1. Run `uv run --directory python itrade ingest $ARGUMENTS -v`.
2. Summarise per ticker: promoted or not, date range, errors, warnings.
3. Compare warnings with `docs/data-notes.md`. If there are warnings not explained there,
   use the `data-auditor` agent on just those tickers and show its proposed data-notes rows.
   Ask before adding them to `docs/data-notes.md`.
4. If any ticker was not promoted, say clearly that curated data for it is unchanged and why.
5. `ingest` ends by exporting the closing prices as traded. The platform's funds are `--universe etf_pullback_v1`:
   if the prices are for the platform and that was not the ingest just run, run it too. Load them with
   `dotnet run --project src/ITrade.Host -- import-prices` and relay its summary: new prices and sessions, the
   latest finished session, any revised price, any fund that is behind. A fund fetched before the close has its
   last bar left out — say so; the next ingest brings it. If the import asks for an acceptance code (many or large
   changes of stored prices, a new close far from the one before), do not accept for the owner — show the
   message and find out why.
