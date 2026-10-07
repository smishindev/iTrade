"""Backtest metrics on hand-computed examples (spec §7)."""

from __future__ import annotations

from decimal import Decimal as D

import pandas as pd
import pytest

from itrade.backtest.metrics import (
    by_group,
    by_year,
    executable_share,
    max_drawdown,
    summarize,
    worst_losing_streak,
)


def trades_df(rs, exits=None):
    n = len(rs)
    exits = exits or [f"2010-01-{i + 4:02d}" for i in range(n)]
    return pd.DataFrame(
        {
            "ticker": ["A", "B", "A", "C", "B"][:n],
            "entry_date": pd.to_datetime(exits) - pd.Timedelta(days=2),
            "exit_date": pd.to_datetime(exits),
            "qty": [D(10)] * n,
            "entry_price": [D(20)] * n,
            "r": [D(str(r)) for r in rs],
            "risk": [D(10)] * n,
            "pnl": [D(str(10 * r)) for r in rs],
            "entry_costs": [D("0.4")] * n,
            "exit_costs": [D("0.6")] * n,
            "sessions_held": [3] * n,
        }
    )


FIVE = trades_df([1, -1, 0.5, -0.5, 2])


def test_streak_and_expectancy_by_hand():
    assert worst_losing_streak(FIVE) == 1
    assert worst_losing_streak(trades_df([1, -1, -0.5, -0.2, 2])) == 3


def test_max_drawdown_depth_and_length():
    dd, length = max_drawdown(pd.Series([100, 110, 99, 105, 120, 90, 95]))
    assert dd == pytest.approx(0.25) and length == 2  # peak 120 (index 4), not recovered by 6
    dd, length = max_drawdown(pd.Series([100, 80, 90, 101, 100]))
    assert dd == pytest.approx(0.20) and length == 3  # peak 100 -> recovered at index 3


def test_executable_share():
    skipped = pd.DataFrame(
        {"reason": ["max_positions"] * 4 + ["cost_too_high"] * 2 + ["size_zero"]}
    )
    assert executable_share(10, skipped) == pytest.approx(3 / 6)
    assert executable_share(4, pd.DataFrame({"reason": ["max_positions"] * 4})) != 0.5


def test_summary():
    equity = pd.DataFrame(
        {
            "date": pd.to_datetime(["2010-01-01", "2011-01-01"]),
            "equity": [D(1000), D(1010)],
            "invested": [D(0), D(500)],
        }
    )
    s = summarize(FIVE, pd.DataFrame({"reason": []}), equity, signals=5)
    assert s.trades == 5
    assert s.expectancy_r == pytest.approx(0.4)
    assert s.win_rate == pytest.approx(0.6)
    assert s.avg_win_r == pytest.approx((1 + 0.5 + 2) / 3)
    assert s.avg_loss_r == pytest.approx(-0.75)
    assert s.costs_r == pytest.approx(0.1)  # (0.4 + 0.6) / 10
    assert s.total_pnl == pytest.approx(20)
    assert s.cagr == pytest.approx(0.01, rel=1e-3)
    assert s.executable_share == 1.0 and s.flags == ()


def test_breakdowns():
    y = by_year(FIVE)
    assert list(y.index) == [2010] and y.loc[2010, "trades"] == 5
    g = by_group(FIVE, {"A": "eq", "B": "eq", "C": "bond"})
    assert g.loc["eq", "trades"] == 4 and g.loc["bond", "expectancy_r"] == pytest.approx(-0.5)
