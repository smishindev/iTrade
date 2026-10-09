"""Stop hook: do not let Claude finish a turn with failing tests, lint or build.

Python (ruff + pytest, in python/) runs when python/, config/ or tools/ changed.
C# (dotnet build) runs for each spike under spikes/ that changed, if the SDK is installed.
On failure exits 2 so Claude sees the output and keeps working. Skips when it already
blocked once this turn (stop_hook_active) to avoid loops.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

from _common import project_dir, read_input

PYTHON_WATCHED = ("python/", "config/", "tools/")


def tail(text: str, n: int = 40) -> str:
    return "\n".join(text.strip().splitlines()[-n:])


def changed_paths(root: Path) -> list[str]:
    status = subprocess.run(
        ["git", "status", "--porcelain", "--untracked-files=all"],
        cwd=root,
        capture_output=True,
        text=True,
        timeout=30,
    )
    return [line[3:].strip('"') for line in status.stdout.splitlines()]


def python_checks(root: Path) -> list[str]:
    run = {"cwd": root / "python", "capture_output": True, "text": True}
    failures = []
    lint = subprocess.run(
        ["uv", "run", "--quiet", "ruff", "check", "src", "tests", "tools"], timeout=120, **run
    )
    if lint.returncode != 0:
        failures.append("ruff check failed:\n" + tail(lint.stdout + lint.stderr))
    tests = subprocess.run(
        ["uv", "run", "--quiet", "pytest", "-x", "-q", "-m", "not network"], timeout=600, **run
    )
    if tests.returncode not in (0, 5):  # 5 = no tests collected
        failures.append("pytest failed:\n" + tail(tests.stdout + tests.stderr))
    return failures


def dotnet_checks(root: Path, changed: list[str]) -> list[str]:
    if not shutil.which("dotnet"):
        return []
    spikes = {p.split("/")[1] for p in changed if p.startswith("spikes/") and p.count("/") >= 2}
    failures = []
    for spike in sorted(spikes):
        folder = root / "spikes" / spike
        targets = sorted(folder.glob("*.sln*")) or sorted(folder.rglob("*.csproj"))[:1]
        for target in targets:
            build = subprocess.run(
                ["dotnet", "build", str(target), "--nologo", "-v", "q"],
                cwd=root,
                capture_output=True,
                text=True,
                timeout=600,
            )
            if build.returncode != 0:
                failures.append(f"dotnet build {target.name} failed:\n" + tail(build.stdout + build.stderr))
    return failures


def main() -> None:
    payload = read_input()
    if payload.get("stop_hook_active"):
        return
    root = project_dir(payload)
    changed = changed_paths(root)

    failures = []
    if any(path.startswith(PYTHON_WATCHED) for path in changed):
        failures += python_checks(root)
    failures += dotnet_checks(root, changed)

    if failures:
        print("Quality gate (stop hook) — fix before finishing:\n\n" + "\n\n".join(failures), file=sys.stderr)
        sys.exit(2)


if __name__ == "__main__":
    main()
