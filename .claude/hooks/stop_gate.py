"""Stop hook: do not let Claude finish a turn with failing tests or lint.

Runs only when src/, tests/ or config/ have uncommitted changes. On failure, exits 2 so
Claude sees the output and keeps working. Skips if it already blocked once this turn
(stop_hook_active) to avoid loops.
"""

from __future__ import annotations

import subprocess
import sys

from _common import project_dir, read_input

WATCHED = ("src/", "tests/", "config/", "pyproject.toml")


def tail(text: str, n: int = 40) -> str:
    return "\n".join(text.strip().splitlines()[-n:])


def main() -> None:
    payload = read_input()
    if payload.get("stop_hook_active"):
        return
    root = project_dir(payload)
    run = {"cwd": root, "capture_output": True, "text": True}

    status = subprocess.run(["git", "status", "--porcelain"], timeout=30, **run)
    changed = [line[3:] for line in status.stdout.splitlines()]
    if not any(path.startswith(WATCHED) for path in changed):
        return

    lint = subprocess.run(["uv", "run", "--quiet", "ruff", "check", "src", "tests"], timeout=120, **run)
    tests = subprocess.run(
        ["uv", "run", "--quiet", "pytest", "-x", "-q", "-m", "not network"], timeout=600, **run
    )
    failures = []
    if lint.returncode != 0:
        failures.append("ruff check failed:\n" + tail(lint.stdout + lint.stderr))
    if tests.returncode not in (0, 5):  # 5 = no tests collected
        failures.append("pytest failed:\n" + tail(tests.stdout + tests.stderr))
    if failures:
        print("Quality gate (stop hook) — fix before finishing:\n\n" + "\n\n".join(failures), file=sys.stderr)
        sys.exit(2)


if __name__ == "__main__":
    main()
