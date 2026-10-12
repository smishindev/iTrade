"""PreToolUse gate for Bash/PowerShell.

Blocks shell commands that would delete or rewrite immutable raw data, touch the phase file,
read secrets, install packages outside uv (which would drift from uv.lock), or — since phase 3, when the
database began to hold the owner's accounting records — write to the working database behind the Host's
back, remove its Docker volume, or roll its schema back.
"""

from __future__ import annotations

import re

from _common import deny, read_input

DESTRUCTIVE = r"(\brm\b|\bdel\b|\brmdir\b|Remove-Item|\bmv\b|Move-Item|Set-Content|Out-File|Clear-Content|>\s*\S)"
RAW_DATA = r"(data|ITradeData)[/\\]+raw"

# A SQL client pointed at a database from the shell. Tests never do this: they get a throw-away
# database from Testcontainers inside the test process.
SQL_CLIENT = r"\b(psql|dropdb)\b|itrade-postgres"
WRITING_SQL = (
    r"\bdropdb\b|\b(drop|truncate|alter|create|grant|revoke)\s+\w|\bdelete\s+from\b|\binsert\s+into\b"
    r"|\bupdate\s+\S+\s+set\b|session_replication_role"
)
DATABASE_VOLUME = (
    r"docker(\s+|-)compose\b.*\bdown\b.*(\s-v\b|--volumes)"
    r"|docker\s+volume\s+(rm|remove|prune)\b"
    r"|docker\s+system\s+prune\b.*--volumes"
)
EF_DATABASE = r"dotnet(\.exe)?\s+ef\s+database\s+(drop|update)\b"


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
    if re.search(SQL_CLIENT, cmd, re.I) and re.search(WRITING_SQL, cmd, re.I):
        deny(
            "The working database holds the owner's accounting records. Read it with SELECT; change it only "
            "through the Host (migrations at start, the ledger, the import commands). A correction of the "
            "ledger is a reversal, never SQL. Tests use their own throw-away database."
        )
    if re.search(DATABASE_VOLUME, cmd, re.I):
        deny(
            "This would remove Docker volumes - the working database lives in one (itrade-pg). "
            "Only the owner does that, after `deploy\\backup.ps1`."
        )
    if re.search(EF_DATABASE, cmd, re.I):
        deny(
            "The Host applies migrations itself at start. Dropping the database or moving it to another "
            "migration by hand (a roll-back drops tables with their data) is the owner's step."
        )


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # fail closed: a broken guard must block, not allow
        deny(f"guard_shell hook error ({type(exc).__name__}: {exc}) - blocked to stay safe; fix the hook.")
