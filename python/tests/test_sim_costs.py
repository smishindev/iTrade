"""Costs and dividends inside the simulator (spec §6.3–§6.4)."""

from __future__ import annotations

from decimal import Decimal as D

import pytest

from itrade.backtest.fills import dividend_cash, order_cost
from itrade.costs import CostConfig, estimate_trade_cost
from itrade.costs.model import BUY, SELL

CFG = CostConfig.load()


@pytest.mark.parametrize(
    ("qty", "price", "side"),
    [("3", "61.00", BUY), ("3", "59.00", SELL), ("0.3678", "778.00", BUY), ("250", "40.00", SELL)],
)
def test_order_cost_equals_cost_model(qty, price, side):
    expected = estimate_trade_cost(CFG, float(qty), float(price), side, half_spread_bps=2.0).total
    assert abs(float(order_cost(CFG, D(qty), D(price), side, 2.0)) - expected) < 1e-4


def test_minimum_commission_dominates_small_orders():
    cost = order_cost(CFG, D("3"), D("61.00"), BUY, 2.0)
    variable = D("3") * D("61.00") * D("4") / D("10000")  # half-spread 2 bp + slippage 2 bp
    assert cost == (D("0.35") + D("3") * D("0.0005") + variable).quantize(D("0.0001"))


def test_sells_pay_regulatory_fees():
    assert order_cost(CFG, D("3"), D("61"), SELL, 2.0) > order_cost(CFG, D("3"), D("61"), BUY, 2.0)


def test_costs_multiplier_for_the_diagnostic_variant():
    one = order_cost(CFG, D("3"), D("61"), BUY, 2.0)
    two = order_cost(CFG, D("3"), D("61"), BUY, 2.0, multiplier=2.0)
    assert abs(two - 2 * one) <= D("0.0001")


def test_dividend_cash_net_of_withholding():
    # 10 shares, $0.50 dividend, no later split, 25% US withholding -> $3.75
    assert dividend_cash(D("10"), 0.50, 1.0, D("0.25")) == D("3.7500")


def test_dividend_cash_undoes_split_adjustment():
    # vendor shows $0.25 after a later 2:1 split; 10 raw shares really received $0.50 each
    assert dividend_cash(D("10"), 0.25, 2.0, D("0.25")) == D("3.7500")
