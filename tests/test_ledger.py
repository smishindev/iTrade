"""Research ledger and the once-only final period (P1.A.24)."""

from __future__ import annotations

import pytest

from itrade.backtest.ledger import (
    FinalAlreadyRun,
    LedgerRow,
    append,
    check_final_allowed,
    final_runs,
)
from itrade.cli import build_parser, main

TEMPLATE = """# Research log

## Pre-registration
| a | b |
|---|---|
| 1 | not a ledger row because it is above the ledger |

## Ledger

| # | Date | Variant | Period | Trades | Exp. R (90% CI) | Win % | Max DD | Control pct | Notes |
|---|------|---------|--------|--------|-----------------|-------|--------|-------------|-------|
"""


def row(period="development", variant="base"):
    return LedgerRow("2026-10-07", variant, period, 10, "0.1 (0 … 0.2)", "50%", "3%", "—", "x")


def test_append_numbers_rows_and_ignores_tables_above(tmp_path):
    p = tmp_path / "log.md"
    p.write_text(TEMPLATE, encoding="utf-8")
    assert append(p, row()) == 1
    assert append(p, row("validation", "rsi5")) == 2
    lines = p.read_text(encoding="utf-8").splitlines()
    assert lines[-1].startswith("| 2 | 2026-10-07 | rsi5 | validation |")
    assert final_runs(p) == []
    check_final_allowed(p)  # no final yet


def test_second_final_is_refused_whatever_the_variant(tmp_path):
    p = tmp_path / "log.md"
    p.write_text(TEMPLATE, encoding="utf-8")
    append(p, row("final", "base"))
    with pytest.raises(FinalAlreadyRun, match="row #1"):
        check_final_allowed(p)


def test_cli_final_needs_explicit_flag(capsys):
    assert main(["backtest", "--period", "final"]) == 2
    assert "runs ONCE" in capsys.readouterr().out
    args = build_parser().parse_args(
        ["backtest", "--period", "final", "--i-understand-this-runs-once"]
    )
    assert args.i_understand_this_runs_once is True
    with pytest.raises(SystemExit):
        build_parser().parse_args(["backtest", "--period", "train"])


def test_latest_run_id_per_variant_supersedes_earlier_rows(tmp_path):
    from itrade.backtest.ledger import latest_run_ids

    p = tmp_path / "log.md"
    p.write_text(TEMPLATE, encoding="utf-8")
    for variant, run_id in [("base", "a1"), ("no_stop (diag)", "b1"), ("no_stop (diag)", "b2")]:
        append(
            p, LedgerRow("d", variant, "validation", 1, "x", "x", "x", "x", f"run `{run_id}`, x")
        )
    append(p, LedgerRow("d", "base", "development", 1, "x", "x", "x", "x", "run `dev`, x"))
    assert latest_run_ids(p, "validation") == {"base": "a1", "no_stop": "b2"}
