"""Variant comparison and the pre-registered selection rule (P1.A.27, research-log H1)."""

from __future__ import annotations

import json

import pytest

from itrade.backtest.compare import (
    comparison_markdown,
    criteria_check,
    families,
    load_runs,
    select,
)
from itrade.config import load_strategy

PARAMS = load_strategy("etf_pullback_v1")
DIAGNOSTICS = {"costs_x2", "skip_one_day_per_week", "no_stop", "raw_close_indicators"}


def write_run(root, variant, expectancy, period="validation", version="v1", **summary):
    d = root / f"{variant}_{period}_{abs(hash((variant, expectancy))) % 10**8:08d}"
    d.mkdir()
    s = {
        "trades": 150, "expectancy_r": expectancy, "win_rate": 0.55, "costs_r": 0.1,
        "max_drawdown": 0.04, "executable_share": 0.8, "cagr": 0.01,
    } | summary  # fmt: skip
    meta = {
        "variant": variant, "diagnostic": variant in DIAGNOSTICS, "period": period,
        "strategy_version": version, "summary": s,
        "expectancy_ci": [expectancy - 0.05, expectancy + 0.05], "control_percentile": 60.0,
        "reference_cagr": 0.1, "run_id": d.name,
    }  # fmt: skip
    (d / "meta.json").write_text(json.dumps(meta), encoding="utf-8")


def runs_with(tmp_path, values):
    for variant, e in values.items():
        write_run(tmp_path, variant, e)
    return load_runs(tmp_path, "validation", "v1")


def test_families_follow_the_registered_overrides():
    fam = families(PARAMS)
    assert fam["entry.rsi_max"] == ["rsi5", "base", "rsi15"]
    assert fam["indicators.trend_sma"] == ["sma150", "base", "sma250"]
    assert fam["exit.max_hold_sessions"] == ["hold5", "base", "hold15"]
    assert all(not set(m) & DIAGNOSTICS for m in fam.values())


def test_base_wins_by_default(tmp_path):
    runs = runs_with(tmp_path, {"base": 0.10, "rsi5": 0.14, "rsi15": 0.05})  # +0.04 < margin
    assert select(runs, PARAMS).variant == "base"


def test_candidate_needs_margin_and_positive_family(tmp_path):
    runs = runs_with(tmp_path, {"base": 0.10, "rsi5": 0.20, "rsi15": 0.05})
    assert select(runs, PARAMS).variant == "rsi5"
    (tmp_path / "x").mkdir()
    runs = runs_with(tmp_path / "x", {"base": 0.10, "rsi5": 0.20, "rsi15": -0.01})
    assert select(runs, PARAMS).variant == "base"


def test_diagnostics_are_never_selected(tmp_path):
    runs = runs_with(tmp_path, {"base": 0.0, "costs_x2": 0.5, "no_stop": 0.9})
    assert select(runs, PARAMS).variant == "base"


def test_other_versions_and_periods_are_ignored_and_duplicates_refused(tmp_path):
    write_run(tmp_path, "base", 0.1)
    write_run(tmp_path, "rsi5", 0.3, version="old")
    write_run(tmp_path, "rsi15", 0.3, period="development")
    assert list(load_runs(tmp_path, "validation", "v1").index) == ["base"]
    write_run(tmp_path, "base", 0.2)
    with pytest.raises(ValueError, match="several runs"):
        load_runs(tmp_path, "validation", "v1")


def test_criteria_and_markdown(tmp_path):
    runs = runs_with(tmp_path, {"base": 0.12, "rsi5": 0.05, "rsi15": -0.02, "costs_x2": 0.03})
    checks = dict(criteria_check(runs, "base", PARAMS))
    assert checks["costs_x2 expectancy > 0"] is True
    assert checks["control percentile >= 95"] is False
    md = comparison_markdown(runs, PARAMS, "validation")
    assert "| base | candidate | 150 | +0.120" in md and "Selection rule → `base`" in md


def test_base_neighbours_are_all_candidates_and_fractional_waits_for_spike_b(tmp_path):
    from itrade.backtest.compare import neighbours

    assert len(neighbours("base", PARAMS)) == 9
    assert neighbours("rsi5", PARAMS) == ["base", "rsi15"]
    runs = runs_with(tmp_path, {"base": 0.10, "fractional": 0.30})
    assert select(runs, PARAMS).variant == "base"
    assert select(runs, PARAMS, fractional_confirmed=True).variant == "fractional"
