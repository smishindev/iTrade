"""PostToolUse for Write/Edit/MultiEdit on Python files.

1. ruff format + ruff check --fix on the edited file.
2. Report remaining lint errors and look-ahead-bias smells back to Claude (non-blocking).
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
SIGNAL_DIRS = ("src/itrade/strategies/", "src/itrade/backtest/", "src/itrade/signals/")


def ruff_cmd(root: Path) -> list[str]:
    for exe in (root / ".venv" / "Scripts" / "ruff.exe", root / ".venv" / "bin" / "ruff"):
        if exe.exists():
            return [str(exe)]
    if shutil.which("ruff"):
        return ["ruff"]
    return ["uv", "run", "--quiet", "ruff"]


def main() -> None:
    payload = read_input()
    tool_input = payload.get("tool_input", {}) or {}
    file_path = tool_input.get("file_path") or ""
    root = project_dir(payload)
    rel = rel_path(file_path, root) if file_path else None
    if not rel or not rel.endswith(".py") or rel.startswith(".claude/"):
        return

    ruff = ruff_cmd(root)
    run = {"cwd": root, "capture_output": True, "text": True, "timeout": 60}
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
