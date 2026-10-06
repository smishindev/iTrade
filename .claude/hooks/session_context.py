"""SessionStart: tell Claude the current phase and how fresh the curated data is."""

from __future__ import annotations

import json

from _common import context, current_phase, project_dir, read_input

PHASES = {
    0: "scope",
    1: "data",
    2: "backtester + costs",
    3: "research",
    4: "paper trading",
    5: "small live",
    6: "scale",
}


def main() -> None:
    root = project_dir(read_input())
    phase = current_phase(root)
    lines = [
        f"iTrade project phase: {phase} ({PHASES.get(phase, '?')}). "
        "Exit criteria: docs/GATES.md. Scope and stop rules: docs/SCOPE.md.",
    ]
    if phase < 4:
        lines.append("Broker/order-placement code is blocked by hooks until phase 4.")

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
