"""SessionStart: tell Claude the current phase and how fresh the curated data is."""

from __future__ import annotations

import json

from _common import context, current_phase, project_dir, read_input

PHASES = {
    0: "decisions",
    1: "validation spikes A (hypothesis, Python) + B (IBKR feasibility, C#)",
    2: "foundation (.NET, PostgreSQL, React shell)",
    3: "data in C#",
    4: "strategy core + backtest in C#",
    5: "UI",
    6: "paper execution",
    7: "hardening",
    8: "live pilot",
    9: "expansion",
}


def main() -> None:
    root = project_dir(read_input())
    phase = current_phase(root)
    lines = [
        f"iTrade project phase: {phase} ({PHASES.get(phase, '?')}). "
        "Master plan: docs/PLAN.md. Exit criteria: docs/GATES.md. Scope: docs/SCOPE.md.",
        "Python never trades; IBKR API code only in src/ITrade.Broker.IBKR and spikes/ibkr-feasibility.",
    ]
    if phase < 8:
        lines.append("Live IB Gateway ports are blocked until the live pilot (phase 8).")

    manifest = root / "data" / "curated" / "manifest.json"
    if manifest.exists():
        data = json.loads(manifest.read_text())
        lasts = sorted({v.get("last") for v in data.values() if v.get("last")})
        if lasts:
            lines.append(f"Curated data: {len(data)} series, last bar between {lasts[0]} and {lasts[-1]}.")
    else:
        lines.append("No curated data yet — run `uv run itrade ingest`.")
    context("SessionStart", "\n".join(lines))


if __name__ == "__main__":
    main()
