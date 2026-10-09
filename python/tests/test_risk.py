"""Position sizing and risk limits (spec §5.2–5.3)."""

from __future__ import annotations

from decimal import Decimal as D

import numpy as np
import pytest

from itrade.backtest.risk import (
    Exposure,
    PortfolioState,
    RiskPolicy,
    cost_model_round_trip,
    floor_to,
    size_entry,
)
from itrade.config import load_strategy
from itrade.costs import CostConfig

PARAMS = load_strategy("etf_pullback_v1")
COSTS = CostConfig.load()
WHOLE = RiskPolicy.from_config(PARAMS, COSTS.min_per_order_usd, COSTS.slippage_bps)
FRACTIONAL = RiskPolicy.from_config(
    {**PARAMS, "risk": {**PARAMS["risk"], "share_granularity": "fractional"}},
    COSTS.min_per_order_usd,
    COSTS.slippage_bps,
)
E = D("3280")


def flat_rt(value: str = "0.70"):
    return lambda q, limit, stop: D(value)


def state(equity=E, cash=E, exposures=()):
    return PortfolioState(D(equity), D(cash), list(exposures))


def exposure(group="us_equity", qty="1", price="10", stop="9", value="10", ticker="ZZZ"):
    return Exposure(ticker, group, D(qty), D(price), D(stop), D(value))


def test_policy_from_config():
    assert WHOLE.granularity == D(1) and FRACTIONAL.granularity == D("0.0001")
    assert (WHOLE.risk_per_trade, WHOLE.max_open_positions) == (D("0.0025"), 3)
    assert WHOLE.max_order_notional_usd == D("700") and WHOLE.max_cost_in_r == D("0.15")
    assert WHOLE.min_per_order_usd == D("0.35")


def test_floor_to():
    assert floor_to(D("3.99"), D(1)) == D(3)
    assert floor_to(D("0.36789"), D("0.0001")) == D("0.3678")
    assert floor_to(D("-1"), D(1)) == D(0)


def test_vwo_example_from_plan_with_real_costs():
    """PLAN §6: E = $3,280, 1R budget = $8.20; VWO limit 61.00, stop 59.00 -> 3 whole shares."""
    rt = cost_model_round_trip(COSTS, half_spread_bps=2.0)
    res = size_entry(D("61.00"), D("59.00"), "emerging", D("2.0"), state(), WHOLE, rt)
    assert res.taken and res.qty == D(3)
    assert res.planned_risk == D("6.00")
    assert res.planned_risk + res.round_trip_cost <= D("8.20")
    assert res.reservation == D(3) * D("61.00") + res.round_trip_cost / 2


def test_spy_whole_shares_is_size_zero_fractional_fits():
    rt = cost_model_round_trip(COSTS, half_spread_bps=0.5)
    whole = size_entry(D("778.00"), D("758.00"), "us_equity", D("0.5"), state(), WHOLE, rt)
    assert not whole.taken and whole.reason == "size_zero"

    frac = size_entry(D("778.00"), D("758.00"), "us_equity", D("0.5"), state(), FRACTIONAL, rt)
    assert frac.taken and D("0.3") < frac.qty < D("0.37")
    budget = D("8.20")
    assert frac.qty * 20 + rt(frac.qty, D("778.00"), D("758.00")) <= budget
    bigger = frac.qty + FRACTIONAL.granularity
    assert bigger * 20 + rt(bigger, D("778.00"), D("758.00")) > budget  # largest that fits


@pytest.mark.parametrize(
    ("kwargs", "reason"),
    [
        (dict(limit="61", stop="61"), "invalid_levels"),
        (dict(exposures=[exposure()] * 3), "max_positions"),
        (dict(equity="1000", limit="300", stop="299"), "max_position"),
        (dict(equity="1000000", limit="800", stop="790"), "max_order_notional"),
        (dict(exposures=[exposure(qty="24", price="10", stop="9")]), "max_total_risk"),
        (dict(exposures=[exposure(group="x", value=str(0.6 * 3280 - 10))]), "max_invested"),
        (dict(exposures=[exposure(value=str(0.3 * 3280 - 10))], group="us_equity"), "max_group"),
        (dict(cash="50"), "settled_cash"),
        (dict(rt="2.00"), "cost_too_high"),
    ],
)
def test_each_limit_reports_its_reason(kwargs, reason):
    limit, stop = D(kwargs.get("limit", "61")), D(kwargs.get("stop", "59"))
    st = state(
        kwargs.get("equity", E),
        kwargs.get("cash", kwargs.get("equity", E)),
        kwargs.get("exposures", ()),
    )
    res = size_entry(
        limit,
        stop,
        kwargs.get("group", "emerging"),
        D("2"),
        st,
        WHOLE,
        flat_rt(kwargs.get("rt", "0.70")),
    )
    assert (res.taken, res.reason, res.qty) == (False, reason, D(0))


def test_pending_entries_count_against_limits():
    # two exposures already use 0.0075*E - 1 of risk: a 2-dollar-risk trade no longer fits
    used = (D("0.0075") * E - 1) / 2
    ex = [exposure(qty=str(used), price="2", stop="1", ticker=t) for t in ("A", "B")]
    res = size_entry(D("61"), D("59"), "emerging", D("2"), state(exposures=ex), WHOLE, flat_rt())
    assert res.reason == "max_total_risk"


def test_random_states_never_break_the_rules():
    rng = np.random.default_rng(20261007)
    for _ in range(1500):
        equity = D(str(round(rng.uniform(500, 20000), 2)))
        limit = D(str(round(rng.uniform(5, 900), 2)))
        stop = (limit * D(str(round(rng.uniform(0.85, 0.995), 4)))).quantize(D("0.01"))
        cash = (equity * D(str(round(rng.uniform(0, 1), 3)))).quantize(D("0.01"))
        n_ex = int(rng.integers(0, 3))
        ex = [
            exposure(
                group=str(rng.choice(["a", "b"])),
                qty=str(round(rng.uniform(0, 5), 2)),
                price="50",
                stop="48",
                value=str(round(rng.uniform(0, float(equity) / 4), 2)),
                ticker=f"E{i}",
            )
            for i in range(n_ex)
        ]
        st = state(equity, cash, ex)
        policy = WHOLE if rng.random() < 0.5 else FRACTIONAL
        rt = cost_model_round_trip(COSTS, half_spread_bps=2.0)
        res = size_entry(limit, stop, "a", D("2"), st, policy, rt)
        if not res.taken:
            assert res.qty == 0
            continue
        q = res.qty
        assert q > 0 and (q / policy.granularity) == (q / policy.granularity).to_integral_value()
        assert q * (limit - stop) + res.round_trip_cost <= policy.risk_per_trade * equity
        assert q * limit <= policy.max_position_fraction * equity
        assert q * limit <= policy.max_order_notional_usd
        assert res.reservation <= cash
        assert res.round_trip_cost <= policy.max_cost_in_r * res.planned_risk
