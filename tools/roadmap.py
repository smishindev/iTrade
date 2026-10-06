"""Roadmap helper: progress per phase and the next available task.

Usage:
    python tools/roadmap.py status
    python tools/roadmap.py next [--all]
    python tools/roadmap.py show P1.A.05

Reads docs/roadmap/P<n>-*.md. A task is a heading line:
    ### [ ] P1.A.05 · 🤖 Title · 2 ч · после: P1.A.04, P1.A.08
Status: [ ] open, [~] in progress, [x] done, [-] cancelled. Stdlib only.
"""

from __future__ import annotations

import re
import sys
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROADMAP = ROOT / "docs" / "roadmap"
HEADER = re.compile(r"^### \[(?P<status>[ x~\-])\] (?P<id>P\d+\.[A-Z0-9]+\.\d+) · (?P<rest>.+)$")
TASK_ID = re.compile(r"P\d+\.[A-Z0-9]+\.\d+")
HOURS = re.compile(r"^(?P<lo>\d+(?:,\d+)?)(?:–(?P<hi>\d+(?:,\d+)?))? ч\b")


@dataclass
class Task:
    id: str
    status: str
    who: str
    title: str
    hours: float | None
    deps: list[str]
    phase: int
    file: Path
    line: int
    body: list[str] = field(default_factory=list)

    @property
    def open(self) -> bool:
        return self.status in (" ", "~")


def _num(s: str) -> float:
    return float(s.replace(",", "."))


def parse_file(path: Path) -> list[Task]:
    phase = int(path.name[1 : path.name.index("-")])
    tasks: list[Task] = []
    for n, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        m = HEADER.match(line)
        if not m:
            if tasks and not line.startswith("## "):
                tasks[-1].body.append(line)
            continue
        parts = [p.strip() for p in m["rest"].split(" · ")]
        who, _, title = parts[0].partition(" ")
        hours, deps = None, []
        for part in parts[1:]:
            if h := HOURS.match(part):
                lo = _num(h["lo"])
                hours = (lo + _num(h["hi"])) / 2 if h["hi"] else lo
            elif part.startswith("после:"):
                deps = TASK_ID.findall(part)
        tasks.append(Task(m["id"], m["status"], who, title, hours, deps, phase, path, n))
    return tasks


def load() -> list[Task]:
    tasks: list[Task] = []
    for path in sorted(ROADMAP.glob("P*-*.md"), key=lambda p: int(p.name[1 : p.name.index("-")])):
        tasks += parse_file(path)
    return tasks


def current_phase() -> int:
    with (ROOT / "config" / "project.toml").open("rb") as f:
        return int(tomllib.load(f).get("phase", 0))


def cmd_status(tasks: list[Task]) -> None:
    phase = current_phase()
    print(f"Current phase: {phase}\n")
    print(f"{'phase':<6}{'done':>6}{'total':>7}{'hours left':>12}{'hours total':>13}")
    for p in sorted({t.phase for t in tasks}):
        ts = [t for t in tasks if t.phase == p and t.status != "-"]
        done = sum(t.status == "x" for t in ts)
        total_h = sum(t.hours or 0 for t in ts)
        left_h = sum(t.hours or 0 for t in ts if t.open)
        mark = " <" if p == phase else ""
        print(f"P{p:<5}{done:>6}{len(ts):>7}{left_h:>12.1f}{total_h:>13.1f}{mark}")


def available(tasks: list[Task]) -> list[Task]:
    by_id = {t.id: t for t in tasks}
    phase = current_phase()
    return [
        t
        for t in tasks
        if t.open
        and t.phase <= phase
        and all(by_id[d].status in ("x", "-") for d in t.deps if d in by_id)
    ]


def fmt(t: Task) -> str:
    h = f"{t.hours:g} ч" if t.hours is not None else "—"
    return f"{t.id:<10} {t.who} {t.title}  [{h}]  ({t.file.name}:{t.line})"


def cmd_next(tasks: list[Task], show_all: bool) -> None:
    ready = available(tasks)
    if not ready:
        print("No available tasks in the current phase (all done or blocked by dependencies).")
        return
    mine = [t for t in ready if t.who in ("🤖", "🤝")]
    owner = [t for t in ready if t.who == "👤"]
    if show_all:
        for t in ready:
            print(fmt(t))
        return
    if mine:
        print("Next for Claude:", fmt(mine[0]))
    if owner:
        print("Waiting on owner:")
        for t in owner:
            print("  " + fmt(t))


def cmd_show(tasks: list[Task], task_id: str) -> None:
    for t in tasks:
        if t.id == task_id:
            print(f"### [{t.status}] {t.id} · {t.who} {t.title}")
            print("\n".join(t.body).strip())
            return
    print(f"Task {task_id} not found", file=sys.stderr)
    sys.exit(1)


def main(argv: list[str]) -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    tasks = load()
    cmd = argv[0] if argv else "status"
    if cmd == "status":
        cmd_status(tasks)
    elif cmd == "next":
        cmd_next(tasks, "--all" in argv)
    elif cmd == "show" and len(argv) > 1:
        cmd_show(tasks, argv[1])
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main(sys.argv[1:])
