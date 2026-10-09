"""SessionStart: tell Claude the current phase and how fresh the curated data is."""

from __future__ import annotations

import json
import sys

from _common import context, current_phase, data_root, project_dir, read_input

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

    manifest = data_root(root) / "curated" / "manifest.json"
    if manifest.exists():
        data = json.loads(manifest.read_text())
        lasts = sorted({v.get("last") for v in data.values() if v.get("last")})
        if lasts:
            lines.append(f"Curated data: {len(data)} series, last bar between {lasts[0]} and {lasts[-1]}.")
    else:
        lines.append("No curated data yet — run `uv run itrade ingest`.")

    lines += roadmap_lines(root)
    context("SessionStart", "\n".join(lines))


def roadmap_lines(root) -> list[str]:
    """Progress of the current phase and the next available tasks (from tools/roadmap.py)."""
    try:
        sys.path.insert(0, str(root / "tools"))
        import roadmap

        tasks = roadmap.load()
        phase = roadmap.current_phase()
        in_phase = [t for t in tasks if t.phase == phase and t.status != "-"]
        done = sum(t.status == "x" for t in in_phase)
        ready = roadmap.available(tasks)
        out = [f"Roadmap: phase {phase} — {done}/{len(in_phase)} tasks done. Work task by task: /next-task."]
        claude = [t for t in ready if t.who in ("🤖", "🤝")][:1]
        owner = [t for t in ready if t.who == "👤"][:3]
        out += [f"Next for Claude: {t.id} {t.title}" for t in claude]
        if owner:
            out.append("Waiting on owner: " + "; ".join(f"{t.id} {t.title}" for t in owner))
        return out
    except Exception as exc:  # the roadmap must never break session start
        return [f"Roadmap unavailable: {exc}"]


if __name__ == "__main__":
    main()
