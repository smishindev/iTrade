---
description: Price an order with the cost model and explain the yearly drag
argument-hint: "<notional USD> <price USD> [TICKER]"
allowed-tools: Bash(uv run itrade:*), PowerShell(uv run itrade:*)
---

Arguments: `$ARGUMENTS` = notional in USD, share price in USD, optional ticker.
If notional is missing, use 2700 (the whole account). If price is missing, look up the latest
close with `uv run itrade sql "select close from bars where ticker='<T>' order by date desc limit 1"`.

Run `uv run itrade costs --notional <N> --price <P> [--ticker <T>]` and explain in plain words:
- the cost of this one order and of a round trip,
- what the yearly drag becomes at the turnover the user is considering,
- whether FX conversion or tax matters more than commission here (see the `cost-model` skill).
