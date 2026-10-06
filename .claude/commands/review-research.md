---
description: Skeptical review of strategy/backtest changes for look-ahead, survivorship, missing costs and overfitting
argument-hint: "[path or git range — default: uncommitted changes]"
---

Use the `quant-reviewer` agent on: $ARGUMENTS
(if empty: every changed file under src/itrade/ and tests/ in `git status` / `git diff`).

Relay its findings, most severe first, and its verdict. Then offer to fix the findings —
do not fix them before the user answers.
