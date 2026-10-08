"""H2 ETF_TREND_V2 parameters (P1.H2.02): pinned version and the registered variants."""

from __future__ import annotations

from itrade.backtest.variants import apply_variant
from itrade.config import load_strategy, strategy_version

PARAMS = load_strategy("etf_trend_v2")


def test_version_is_pinned():
    # Pinned on purpose: changing parameters or universe must be a conscious new version.
    assert strategy_version("etf_trend_v2") == (
        "be529ab58672c201e4b1a6d6e2ffca0299458fae2a94615cdb9d9e090feddd7b"
    )


def test_owner_decisions_2026_10_08():
    assert PARAMS["rotation"]["weight"] == 0.20
    assert PARAMS["breakout"]["risk_per_trade"] == 0.005
    assert PARAMS["risk"]["max_position_fraction"] == 0.20
    assert PARAMS["risk"]["max_cost_in_r"] == 0.10
    assert PARAMS["criteria"]["max_drawdown"] == 0.20
    assert PARAMS["periods"]["final"] == {"start": "2021-01-04", "end": "2026-09-30"}


def test_variants_within_budget_and_valid():
    variants = PARAMS["variants"]
    assert len(variants) == 18 <= 20
    assert {v["rules"] for v in variants} == {"rotation", "breakout"}
    assert [v["name"] for v in variants if v.get("diagnostic")] == ["rot_costs_x2", "brk_costs_x2"]
    for v in variants:
        assert v["name"].startswith({"rotation": "rot_", "breakout": "brk_"}[v["rules"]])
        apply_variant(PARAMS, v["name"])  # every override hits a real key
