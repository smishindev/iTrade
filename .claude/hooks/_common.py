"""Shared helpers for iTrade Claude Code hooks. Stdlib only — hooks must start fast."""

from __future__ import annotations

import json
import os
import re
import sys
import tomllib
from pathlib import Path


def read_input() -> dict:
    try:
        return json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return {}


def project_dir(payload: dict | None = None) -> Path:
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env:
        return Path(env)
    if payload and payload.get("cwd"):
        return Path(payload["cwd"])
    return Path(__file__).resolve().parents[2]


def rel_path(file_path: str, root: Path) -> str | None:
    """Project-relative POSIX path, lower-cased (Windows paths are case-insensitive).
    None when the file is outside the project."""
    try:
        p = Path(file_path)
        if not p.is_absolute():
            p = root / p
        rel = os.path.relpath(p.resolve(), root.resolve())
    except ValueError:  # different drive on Windows
        return None
    rel = rel.replace("\\", "/")
    if rel.startswith(".."):
        return None
    return rel.lower()


def current_phase(root: Path) -> int:
    try:
        with (root / "config" / "project.toml").open("rb") as f:
            return int(tomllib.load(f).get("phase", 0))
    except (OSError, ValueError, tomllib.TOMLDecodeError):
        return 0


def new_content(tool_input: dict) -> str:
    """Text a Write/Edit/MultiEdit/NotebookEdit call is about to put into the file."""
    parts = [
        tool_input.get("content"),
        tool_input.get("new_string"),
        tool_input.get("new_source"),
    ]
    parts += [e.get("new_string") for e in tool_input.get("edits", []) or []]
    return "\n".join(p for p in parts if p)


def deny(reason: str) -> None:
    print(
        json.dumps(
            {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                }
            }
        )
    )
    sys.exit(0)


def context(event: str, text: str) -> None:
    print(json.dumps({"hookSpecificOutput": {"hookEventName": event, "additionalContext": text}}))
    sys.exit(0)


SECRET_ASSIGNMENT = re.compile(
    r"""(?ix)
    \b(api[_-]?key|api[_-]?secret|secret[_-]?key|password|passwd|access[_-]?token|auth[_-]?token)
    \s*[:=]\s*["'][^"'\s]{8,}["']
    """
)

# C# code that touches the official IBKR TWS API (namespace IBApi).
CSHARP_BROKER_CODE = re.compile(r"\busing\s+IBApi\b|\bIBApi\.|\bEClientSocket\b|\.placeOrder\s*\(")
CSHARP_BROKER_DIRS = ("src/itrade.broker.ibkr/", "spikes/ibkr-feasibility/")  # lower-cased
LIVE_PORT = re.compile(r"\b(4001|7496)\b")

# Python libraries/calls that can move real money. Python never trades (docs/PLAN.md §2.5).
BROKER_CODE = re.compile(
    r"""(?x)
    \b(import|from)\s+(ib_insync|ib_async|ibapi|alpaca|alpaca_trade_api|tinkoff|ccxt)\b
    | \bplace_?order\s*\(
    | \bplaceOrder\s*\(
    | \bsubmit_order\s*\(
    """
)
