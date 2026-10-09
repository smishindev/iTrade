"""Research ledger and the once-only final period (P1.A.24)."""

from __future__ import annotations

import shutil

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


@pytest.fixture
def fake_project(tmp_path, monkeypatch):
    import itrade.backtest.report as report
    import itrade.backtest.runner as runner
    import itrade.config as config

    shutil.copytree(config.project_root() / "config", tmp_path / "config")
    (tmp_path / "docs").mkdir()
    (tmp_path / "docs" / "research-log.md").write_text(TEMPLATE, encoding="utf-8")
    monkeypatch.setattr(config, "project_root", lambda: tmp_path)
    monkeypatch.setattr(report, "git_commit", lambda: "abc1234")

    def crash(*args, **kwargs):
        raise RuntimeError("simulated crash during the final run")

    monkeypatch.setattr(runner, "run_variant", crash)
    return tmp_path / "docs" / "research-log.md", report


FINAL = ["backtest", "--period", "final", "--i-understand-this-runs-once"]


def test_final_needs_its_control_and_a_clean_tree(fake_project, capsys):
    ledger, report = fake_project
    assert main(FINAL) == 3
    assert "--control 1000" in capsys.readouterr().out
    report.git_commit = lambda: "abc1234+dirty"
    assert main([*FINAL, "--control", "1000"]) == 3
    assert "uncommitted" in capsys.readouterr().out
    assert final_runs(ledger) == []


def test_final_is_locked_before_it_runs(fake_project, capsys):
    ledger, _ = fake_project
    with pytest.raises(RuntimeError, match="simulated crash"):
        main([*FINAL, "--control", "1000"])
    assert len(final_runs(ledger)) == 1  # the lock row survived the crash
    assert main([*FINAL, "--control", "1000"]) == 3
    assert "REFUSED" in capsys.readouterr().out


def test_no_run_without_a_ledger(fake_project, capsys):
    ledger, _ = fake_project
    ledger.unlink()
    assert main(["backtest", "--period", "development"]) == 3
    assert "pre-register" in capsys.readouterr().out


def test_trend_final_runs_its_group_costs_x2_under_the_same_lock(fake_project, monkeypatch):
    import itrade.cli as cli

    _, _ = fake_project
    h2 = cli.ledger_path("etf_trend_v2")
    h2.write_text(TEMPLATE, encoding="utf-8")
    calls = []
    monkeypatch.setattr(cli, "_run_and_log", lambda s, v, per, c, w, led: calls.append((v, per, c)))
    args = ["backtest", "--strategy", "etf_trend_v2", "--variant", "brk_trend", *FINAL[1:]]
    assert main([*args, "--control", "1000"]) == 0
    assert calls == [("brk_trend", "final", 1000), ("brk_costs_x2", "final", 0)]
    assert len(final_runs(h2)) == 1  # one lock row for the hypothesis


def test_a_closed_hypothesis_refuses_its_final(tmp_path):
    from itrade.backtest.ledger import close_hypothesis

    p = tmp_path / "log.md"
    p.write_text(TEMPLATE, encoding="utf-8")
    append(p, row("validation", "base"))
    check_final_allowed(p)
    close_hypothesis(p, "2026-10-09", "rejected at discovery by the pre-registered rule")
    with pytest.raises(FinalAlreadyRun, match="closed"):
        check_final_allowed(p)
    with pytest.raises(FinalAlreadyRun):
        close_hypothesis(p, "2026-10-09", "twice")
