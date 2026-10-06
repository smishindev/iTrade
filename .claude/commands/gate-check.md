---
description: Check a phase gate (default = current phase) against docs/GATES.md with evidence
argument-hint: "[phase number]"
---

Use the `gate-keeper` agent to check gate **$ARGUMENTS** (if empty, the current phase from
`config/project.toml`). Relay its table and gate result to the user unchanged in substance.
Then, if any row is FAIL and the fix is small and within the current phase, offer to do it.
Never edit `config/project.toml` — the owner advances the phase.
