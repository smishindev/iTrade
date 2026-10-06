---
description: Refresh market data, validate it, and explain any warnings
argument-hint: "[TICKER ...]"
allowed-tools: Bash(uv run itrade:*), PowerShell(uv run itrade:*)
---

1. Run `uv run itrade ingest $ARGUMENTS -v`.
2. Summarise per ticker: promoted or not, date range, errors, warnings.
3. Compare warnings with `docs/data-notes.md`. If there are warnings not explained there,
   use the `data-auditor` agent on just those tickers and show its proposed data-notes rows.
   Ask before adding them to `docs/data-notes.md`.
4. If any ticker was not promoted, say clearly that curated data for it is unchanged and why.
