---
description: Skeptical review of strategy/backtest changes for look-ahead, survivorship, missing costs and overfitting
argument-hint: "[path or git range — default: uncommitted changes]"
---

Research is frozen (plan v4): the code under `python/src/itrade/strategies` and `python/src/itrade/backtest` is an
archive. This command is for the rare change there (a bug fix in the archive, or work under a new scope the
owner approved in `docs/SCOPE.md`).

Use the `quant-reviewer` agent on: $ARGUMENTS
(if empty: every changed file under `python/src/itrade/` and `python/tests/` in `git status` / `git diff`).

Relay its findings, most severe first, and its verdict. Then offer to fix the findings —
do not fix them before the user answers.
