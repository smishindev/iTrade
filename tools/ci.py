"""One command that builds and tests the whole repository (gate G2: "CI script").

    python tools/ci.py             all: Python, .NET with integration tests, web, browser tests
    python tools/ci.py --fast      without the steps that need Docker (integration, browser tests)
    python tools/ci.py --only web  one part: python | dotnet | web | e2e

Every step runs even if an earlier one failed; the summary at the end lists them all, and the exit
code is 1 when any failed. There is no CI server: run it before a phase gate and after larger
changes.
The pre-commit hook runs the fast subset for the parts a commit touches. Stdlib only.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


@dataclass(frozen=True)
class Step:
    part: str
    name: str
    command: tuple[str, ...]
    cwd: str = "."
    needs_docker: bool = False


STEPS = (
    Step(
        "python",
        "ruff format",
        ("uv", "run", "--quiet", "ruff", "format", "--check", "src", "tests", "tools"),
        "python",
    ),
    Step(
        "python",
        "ruff check",
        ("uv", "run", "--quiet", "ruff", "check", "src", "tests", "tools"),
        "python",
    ),
    Step(
        "python", "pytest", ("uv", "run", "--quiet", "pytest", "-q", "-m", "not network"), "python"
    ),
    Step("dotnet", "build", ("dotnet", "build", "ITrade.slnx", "--nologo", "-v", "q")),
    Step(
        "dotnet",
        "format and style",
        ("dotnet", "format", "ITrade.slnx", "--verify-no-changes", "--no-restore", "-v", "q"),
    ),
    Step(
        "dotnet",
        "unit tests",
        ("dotnet", "test", "tests/ITrade.UnitTests", "--no-build", "--nologo", "-v", "q"),
    ),
    Step(
        "dotnet",
        "architecture tests",
        ("dotnet", "test", "tests/ITrade.ArchitectureTests", "--no-build", "--nologo", "-v", "q"),
    ),
    Step(
        "dotnet",
        "integration tests",
        ("dotnet", "test", "tests/ITrade.IntegrationTests", "--no-build", "--nologo", "-v", "q"),
        needs_docker=True,
    ),
    # Recorded broker messages, no network (empty until phase 5). ITrade.BrokerContractTests is
    # left out on purpose: it needs a paper account and is run by hand.
    Step(
        "dotnet",
        "replay tests",
        ("dotnet", "test", "tests/ITrade.ReplayTests", "--no-build", "--nologo", "-v", "q"),
    ),
    Step("web", "API client is current", ("npm", "run", "--silent", "gen:api:check"), "web"),
    Step("web", "prettier", ("npm", "run", "--silent", "format:check"), "web"),
    Step("web", "eslint", ("npm", "run", "--silent", "lint"), "web"),
    Step("web", "unit tests", ("npm", "test", "--silent"), "web"),
    Step("web", "build", ("npm", "run", "--silent", "build"), "web"),
    Step("e2e", "browser tests", ("npm", "run", "--silent", "e2e"), "web", needs_docker=True),
)
PARTS = tuple(dict.fromkeys(step.part for step in STEPS))


def docker_is_up() -> bool:
    docker = shutil.which("docker")
    if not docker:
        return False
    return subprocess.run([docker, "info"], capture_output=True, timeout=60).returncode == 0


def run(step: Step) -> tuple[bool, float, str]:
    program = shutil.which(step.command[0])  # npm is npm.cmd on Windows
    if program is None:
        return False, 0.0, f"{step.command[0]} is not installed"
    started = time.monotonic()
    result = subprocess.run(
        [program, *step.command[1:]],
        cwd=ROOT / step.cwd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=1800,
    )
    tail = "\n".join((result.stdout + result.stderr).strip().splitlines()[-25:])
    return result.returncode == 0, time.monotonic() - started, tail


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--fast", action="store_true", help="skip the steps that need Docker")
    parser.add_argument("--only", choices=PARTS, help="run one part")
    args = parser.parse_args()

    steps = [s for s in STEPS if args.only in (None, s.part) and not (args.fast and s.needs_docker)]
    if any(s.needs_docker for s in steps) and not docker_is_up():
        print("Docker is not running: start Docker Desktop, or use --fast.", file=sys.stderr)
        return 2

    results = []
    for step in steps:
        label = f"{step.part}: {step.name}"
        print(f"... {label}", flush=True)
        ok, seconds, tail = run(step)
        results.append((label, ok, seconds))
        if not ok:
            print(tail, flush=True)

    print("\n" + "=" * 62)
    for label, ok, seconds in results:
        print(f"{'PASS' if ok else 'FAIL'}  {label:<42} {seconds:6.1f} s")
    skipped = [f"{s.part}: {s.name}" for s in STEPS if s not in steps]
    for label in skipped:
        print(f"skip  {label}")
    failed = sum(not ok for _, ok, _ in results)
    print("=" * 62)
    print(
        "ALL PASSED" if not failed else f"{failed} FAILED",
        f"({len(skipped)} skipped)" if skipped else "",
    )
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
