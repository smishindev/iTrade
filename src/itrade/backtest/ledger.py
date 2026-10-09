"""The multiple-testing ledger in docs/research-log.md (spec §8, research-integrity rule 8).

Every `itrade backtest` run appends one row. The final period runs **once per hypothesis**:
any existing `final` row refuses a second final run, whatever the variant.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

LEDGER_HEADER = "## Ledger"
ROW = re.compile(r"^\|\s*(\d+)\s*\|")


@dataclass(frozen=True)
class LedgerRow:
    date: str
    variant: str
    period: str
    trades: int
    expectancy: str
    win_rate: str
    max_dd: str
    control: str
    notes: str


def _rows(text: str) -> list[list[str]]:
    _, _, ledger = text.partition(LEDGER_HEADER)
    out = []
    for line in ledger.splitlines():
        if ROW.match(line):
            out.append([c.strip() for c in line.strip().strip("|").split("|")])
    return out


def final_runs(path: Path) -> list[list[str]]:
    """Ledger rows whose period column is `final`."""
    return [r for r in _rows(path.read_text(encoding="utf-8")) if len(r) > 3 and r[3] == "final"]


RUN_ID = re.compile(r"run `([^`]+)`")


def latest_run_ids(path: Path, period: str) -> dict[str, str]:
    """variant -> run id of its latest ledger row on `period` (a re-run after a fix supersedes)."""
    out: dict[str, str] = {}
    for r in _rows(path.read_text(encoding="utf-8")):
        if len(r) > 9 and r[3] == period and (m := RUN_ID.search(r[9])):
            out[r[2].removesuffix(" (diag)")] = m.group(1)
    return out


class FinalAlreadyRun(RuntimeError):
    pass


def closed_rows(path: Path) -> list[list[str]]:
    """Ledger rows whose period column is `closed` (the hypothesis was decided without a final)."""
    return [r for r in _rows(path.read_text(encoding="utf-8")) if len(r) > 3 and r[3] == "closed"]


def check_final_allowed(path: Path) -> None:
    closed = closed_rows(path)
    if closed:  # review P1.H3.06: an early rejection must also lock the unseen final period
        raise FinalAlreadyRun(
            f"this hypothesis is closed (ledger row #{closed[0][0]}: {closed[0][9]}); the final "
            f"period stays unseen — a new hypothesis needs a new registration"
        )
    done = final_runs(path)
    if done:
        raise FinalAlreadyRun(
            f"the final period was already run for this hypothesis (ledger row #{done[0][0]}, "
            f"variant {done[0][2]}); it runs once — a new hypothesis needs a new registration"
        )


def close_hypothesis(path: Path, date: str, reason: str) -> int:
    """Append the `closed` row that locks the final period of a decided hypothesis."""
    if closed_rows(path):
        raise FinalAlreadyRun("this hypothesis is already closed")
    row = LedgerRow(date, "—", "closed", 0, "—", "—", "—", "—", reason)
    return append(path, row)


def append(path: Path, row: LedgerRow) -> int:
    """Append a row to the ledger table; returns its number."""
    text = path.read_text(encoding="utf-8")
    if LEDGER_HEADER not in text:
        raise ValueError(f"{path} has no '{LEDGER_HEADER}' section")
    number = len(_rows(text)) + 1
    line = (
        f"| {number} | {row.date} | {row.variant} | {row.period} | {row.trades} | "
        f"{row.expectancy} | {row.win_rate} | {row.max_dd} | {row.control} | {row.notes} |"
    )
    path.write_text(text.rstrip("\n") + "\n" + line + "\n", encoding="utf-8")
    return number
