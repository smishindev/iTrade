"""PreToolUse gate for Bash/PowerShell.

Blocks shell commands that would delete or rewrite immutable raw data, touch the phase file,
read secrets, or install packages outside uv (which would drift from uv.lock).
"""

from __future__ import annotations

import re

from _common import deny, read_input

DESTRUCTIVE = r"(\brm\b|\bdel\b|\brmdir\b|Remove-Item|\bmv\b|Move-Item|Set-Content|Out-File|Clear-Content|>\s*\S)"
RAW_DATA = r"(data|ITradeData)[/\\]+raw"


def main() -> None:
    payload = read_input()
    cmd = (payload.get("tool_input", {}) or {}).get("command", "") or ""

    if re.search(RAW_DATA, cmd, re.I) and re.search(DESTRUCTIVE, cmd, re.I):
        deny("Raw snapshots (data/raw, D:/ITradeData/raw) are immutable. Do not delete, move or overwrite them.")
    if re.search(r"ITradeData", cmd, re.I) and re.search(r"\b(rm|del|rmdir|mv)\b|Remove-Item|Move-Item", cmd, re.I):
        deny("D:/ITradeData holds the project data. Do not delete or move it; the owner does that by hand.")
    if re.search(r"config[/\\]+project\.toml", cmd, re.I) and re.search(DESTRUCTIVE + r"|sed\s+-i", cmd, re.I):
        deny("Only the owner changes config/project.toml (the project phase).")
    if re.search(r"(^|[\s/\\])\.env(\.|\b)", cmd) and re.search(r"\b(cat|type|Get-Content|less|more|head|tail)\b", cmd, re.I):
        deny("Do not read secret files.")
    if re.search(r"(^|[;&|]\s*)(python -m )?pip3? install\b", cmd, re.I):
        deny("Use `uv add <package>` (or `uv add --dev`) so uv.lock stays the source of truth.")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # fail closed: a broken guard must block, not allow
        deny(f"guard_shell hook error ({type(exc).__name__}: {exc}) - blocked to stay safe; fix the hook.")
