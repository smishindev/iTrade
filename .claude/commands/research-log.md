---
description: Record a tried strategy variant in the multiple-testing ledger (research is frozen - see below)
argument-hint: "<strategy/variant and results, free text>"
---

**Research is frozen (plan v4, 2026-10-09).** The ledgers of H1–H3 (`docs/research-log.md`,
`docs/research-log-H2.md`, `docs/research-log-H3.md`) end with a `closed` row and must not get new rows.

If the owner has approved a **new scope** in `docs/SCOPE.md` with a new hypothesis and its own ledger file,
append one row to that ledger from: $ARGUMENTS

- `#` = previous row number + 1. Date = today.
- Fill the columns you can from the text or from the most recent backtest output in this
  conversation; write `?` for anything unknown — never invent numbers.
- After adding it, state the count: "N of 20 variants used" (stop rule in docs/SCOPE.md).
  At 15+ warn the user; at 20 remind them the stop criterion is reached.

Otherwise do not write anything: tell the owner that research is frozen and what a new scope requires
(pre-registration, calibrated control, `quant-reviewer` before and after, one final run).
