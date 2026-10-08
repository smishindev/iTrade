"""Equal-weight universe reference (ETF_TREND_V2 §5)."""

from __future__ import annotations

import pandas as pd
import pytest

from itrade.backtest.benchmark import equal_weight


def frame(closes, dividends=None):
    idx = pd.to_datetime(["2023-12-28", "2023-12-29", "2024-01-02", "2024-01-03"])
    return pd.DataFrame(
        {"close": closes, "dividends": dividends or [0.0] * 4, "splits": 0.0, "s": 1.0}, index=idx
    )


def test_yearly_rebalance_by_hand():
    a = frame([10.0, 20.0, 20.0, 20.0])  # doubles in 2023
    b = frame([10.0, 10.0, 11.0, 11.0], dividends=[0, 0, 0, 0.0])  # +10% on 2 Jan 2024
    eq = equal_weight({"A": a, "B": b}, "2023-12-28", "2024-01-03", 100.0)
    assert list(eq["equity"].round(10)) == [100.0, 150.0, 157.5, 157.5]  # 75 + 82.5 after reset


def test_dividends_count_and_missing_instruments_wait_for_the_next_year():
    a = frame([10.0, 10.0, 10.0, 10.0], dividends=[0, 1.0, 0, 0])  # 10% total return
    b = frame([float("nan"), float("nan"), 10.0, 12.0])  # lists in 2024
    eq = equal_weight({"A": a, "B": b}, "2023-12-28", "2024-01-03", 100.0)
    assert eq["equity"].iloc[1] == pytest.approx(110.0)  # A alone in 2023
    # 2024: B has no return on its first day (no previous close) -> not in the 2024 basket
    assert eq["equity"].iloc[-1] == pytest.approx(110.0)
