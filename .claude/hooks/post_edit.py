"""PostToolUse for Write/Edit/MultiEdit.

Python: ruff format + ruff check --fix; report remaining lint errors and look-ahead smells.
C#: `dotnet format whitespace` on the edited file (needs a .csproj above it).
All feedback is non-blocking context for Claude.
"""

from __future__ import annotations

import re
import shutil
import subprocess
from pathlib import Path

from _common import context, project_dir, read_input, rel_path

LOOKAHEAD_SMELLS = [
    (re.compile(r"\.shift\(\s*-\s*\d"), "negative shift (uses future rows)"),
    (re.compile(r"\.bfill\(|fillna\([^)]*method\s*=\s*[\"']bfill"), "backward fill (leaks future values)"),
    (re.compile(r"rolling\([^)]*center\s*=\s*True"), "centered rolling window (uses future rows)"),
    (re.compile(r"\.iloc\[\s*-1\s*\]"), "iloc[-1] in signal code (is this the last *known* bar?)"),
]
SIGNAL_DIRS = (
    "python/src/itrade/strategies/", "python/src/itrade/backtest/", "python/src/itrade/signals/"
)
PYTHON_DIR = "python"  # the Python project (pyproject.toml, .venv) since P2.1.01


def ruff_cmd(root: Path) -> list[str]:
    venv = root / PYTHON_DIR / ".venv"
    for exe in (venv / "Scripts" / "ruff.exe", venv / "bin" / "ruff"):
        if exe.exists():
            return [str(exe)]
    if shutil.which("ruff"):
        return ["ruff"]
    return ["uv", "run", "--quiet", "ruff"]


def nearest_project(path: Path, root: Path) -> Path | None:
    for folder in path.parents:
        found = sorted(folder.glob("*.csproj"))
        if found:
            return found[0]
        if folder == root:
            return None
    return None


def format_csharp(file_path: str, root: Path) -> None:
    path = Path(file_path).resolve()
    project = nearest_project(path, root.resolve())
    if project is None or not shutil.which("dotnet"):
        return
    result = subprocess.run(
        ["dotnet", "format", "whitespace", str(project), "--include", str(path)],
        cwd=root,
        capture_output=True,
        text=True,
        timeout=120,
    )
    if result.returncode != 0:
        context("PostToolUse", "dotnet format failed:\n" + (result.stdout + result.stderr)[-2000:])


def main() -> None:
    payload = read_input()
    tool_input = payload.get("tool_input", {}) or {}
    file_path = tool_input.get("file_path") or ""
    root = project_dir(payload)
    rel = rel_path(file_path, root) if file_path else None
    if rel and rel.endswith(".cs"):
        format_csharp(file_path, root)
        return
    if not rel or not rel.endswith(".py") or rel.startswith(".claude/"):
        return

    ruff = ruff_cmd(root)
    # ruff reads python/pyproject.toml when run from the Python project folder
    run = {"cwd": root / PYTHON_DIR, "capture_output": True, "text": True, "timeout": 60}
    subprocess.run([*ruff, "format", "--quiet", file_path], **run)
    subprocess.run([*ruff, "check", "--fix", "--quiet", file_path], **run)
    lint = subprocess.run([*ruff, "check", "--output-format", "concise", file_path], **run)

    notes = []
    if lint.returncode != 0 and lint.stdout.strip():
        notes.append("ruff found issues it could not auto-fix:\n" + lint.stdout.strip())

    if rel.startswith(SIGNAL_DIRS):
        source = Path(file_path).read_text(encoding="utf-8", errors="replace")
        for n, line in enumerate(source.splitlines(), 1):
            for pattern, why in LOOKAHEAD_SMELLS:
                if pattern.search(line) and "lookahead-ok" not in line:
                    notes.append(f"{rel}:{n}: possible look-ahead bias — {why}. "
                                 "Fix it, or add `# lookahead-ok: <reason>` if it is intentional.")

    if notes:
        context("PostToolUse", "\n".join(notes))


if __name__ == "__main__":
    main()
