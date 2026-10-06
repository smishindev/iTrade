"""Claude Code status line for iTrade: phase, roadmap progress, next task. Stdlib only."""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SHORT = {
    0: "решения",
    1: "проверки A+B",
    2: "основа",
    3: "данные",
    4: "ядро",
    5: "UI",
    6: "paper",
    7: "укрепление",
    8: "LIVE-пилот",
    9: "расширение",
}


def main() -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.read()  # Claude Code sends session JSON; not needed here
    try:
        sys.path.insert(0, str(ROOT / "tools"))
        import roadmap

        tasks = roadmap.load()
        phase = roadmap.current_phase()
        in_phase = [t for t in tasks if t.phase == phase and t.status != "-"]
        done = sum(t.status == "x" for t in in_phase)
        ready = [t for t in roadmap.available(tasks) if t.who in ("🤖", "🤝")]
        nxt = f" │ next {ready[0].id}" if ready else ""
        print(f"iTrade │ P{phase} {SHORT.get(phase, '')} │ {done}/{len(in_phase)}{nxt}")
    except Exception:
        print("iTrade")


if __name__ == "__main__":
    main()
