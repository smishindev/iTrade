# Data notes

Explanations for quality-check warnings that were reviewed and accepted. Gate 1 requires
every warning from `uv run itrade quality -v` to have an entry here.

| Ticker | Check | Dates | Reviewed | Explanation / action |
|--------|-------|-------|----------|----------------------|
| VWO | split_event | 2008-06-18 | ⏳ | 2:1 split. Verify adj_close has no jump across that date. |
| VWO | return_outlier | 2008-10-13 | ⏳ | Global rally day in the 2008 crisis; likely genuine. Confirm against a second source. |
| ILS=X | ohlc_inconsistent | 211 days | ⏳ | Yahoo FX bars are quote snapshots; only `close` is used for conversion. |
| ILS=X | gap | to 2008-08-26 | ⏳ | Vendor gap. Forward-fill ≤ 5 days for FX conversion only. |
