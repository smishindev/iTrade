from __future__ import annotations

import pytest

from itrade.costs import annual_drag_pct, capital_gains_tax, estimate_trade_cost, round_trip_bps
from itrade.costs.model import BUY, SELL, commission_usd, fx_usd


def test_minimum_commission_applies_to_small_orders(cost_cfg):
    # 4 shares * $0.0035 = $0.014 < $0.35 minimum
    c = commission_usd(cost_cfg, 4, 650.0)
    assert c == pytest.approx(0.35 + 4 * cost_cfg.passthrough_per_share_usd)


def test_commission_capped_at_pct_of_value(cost_cfg):
    # 1000 penny shares at $0.01: per-share fee $3.50 but cap is 1% of $10 = $0.10
    c = commission_usd(cost_cfg, 1000, 0.01)
    assert c == pytest.approx(0.10 + 1000 * cost_cfg.passthrough_per_share_usd)


def test_regulatory_fees_only_on_sells(cost_cfg):
    buy = estimate_trade_cost(cost_cfg, 10, 500.0, BUY)
    sell = estimate_trade_cost(cost_cfg, 10, 500.0, SELL)
    assert buy.regulatory == 0.0
    assert sell.regulatory > 0.0


def test_spread_and_slippage_scale_with_notional(cost_cfg):
    tc = estimate_trade_cost(cost_cfg, 10, 100.0, BUY, half_spread_bps=5.0)
    assert tc.spread == pytest.approx(1000 * 5 / 1e4)
    assert tc.slippage == pytest.approx(1000 * cost_cfg.slippage_bps / 1e4)


def test_fx_minimum_dominates_small_conversions(cost_cfg):
    assert fx_usd(cost_cfg, 2700) == pytest.approx(cost_cfg.fx_min_per_conversion_usd)
    assert fx_usd(cost_cfg, 10_000_000) == pytest.approx(10_000_000 * 0.2 / 1e4)


def test_total_and_bps_consistent(cost_cfg):
    tc = estimate_trade_cost(cost_cfg, 5, 400.0, SELL, convert_fx=True)
    parts = tc.commission + tc.regulatory + tc.spread + tc.slippage + tc.fx
    assert tc.total == pytest.approx(parts)
    assert tc.bps == pytest.approx(parts / 2000 * 1e4)


def test_zero_shares_costs_nothing(cost_cfg):
    assert estimate_trade_cost(cost_cfg, 0, 100.0, BUY).total == 0.0


@pytest.mark.parametrize("bad", [{"side": "hold"}, {"price": 0.0}])
def test_invalid_inputs_raise(cost_cfg, bad):
    kwargs = {"shares": 1, "price": 100.0, "side": BUY} | bad
    with pytest.raises(ValueError):
        estimate_trade_cost(cost_cfg, **kwargs)


def test_round_trip_small_account_is_minimum_dominated(cost_cfg):
    small = round_trip_bps(cost_cfg, 500, 650.0, half_spread_bps=0.5)
    large = round_trip_bps(cost_cfg, 50_000, 650.0, half_spread_bps=0.5)
    assert small > large


def test_annual_drag():
    assert annual_drag_pct(10.0, 12) == pytest.approx(1.2)


def test_capital_gains_tax_with_carryforward():
    tax = capital_gains_tax({2024: -1000.0, 2025: 1500.0, 2026: 400.0}, 0.25)
    assert tax == {2024: 0.0, 2025: pytest.approx(125.0), 2026: pytest.approx(100.0)}


def test_capital_gains_tax_without_carryforward():
    tax = capital_gains_tax({2024: -1000.0, 2025: 1500.0}, 0.25, carryforward=False)
    assert tax[2025] == pytest.approx(375.0)


def test_loss_carries_across_multiple_years():
    tax = capital_gains_tax({2024: -1000.0, 2025: 300.0, 2026: 1000.0}, 0.25)
    assert tax == {2024: 0.0, 2025: 0.0, 2026: pytest.approx(75.0)}
