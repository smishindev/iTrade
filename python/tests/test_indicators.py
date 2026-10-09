"""Indicators vs the exact reference vectors (spec §14), no look-ahead, dividend-neutral series."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pandas as pd
import pytest

from conftest import make_bars
from itrade.strategies.indicators import (
    atr_wilder,
    average_dollar_volume,
    dividend_multiplier,
    dividend_neutral,
    is_close,
    rsi_wilder,
    sma,
)

FIXTURES = Path(__file__).parent / "fixtures" / "indicators"
NAMES = ["rising", "falling", "gap"]


def load(name: str) -> pd.DataFrame:
    return pd.read_csv(FIXTURES / f"{name}.csv", dtype={"volume": "float64"})


def compute(df: pd.DataFrame) -> dict[str, pd.Series]:
    return {
        "sma3": sma(df["close"], 3),
        "sma5": sma(df["close"], 5),
        "rsi2": rsi_wilder(df["close"], 2),
        "atr14": atr_wilder(df["high"], df["low"], df["close"], 14),
        "adv5": average_dollar_volume(df["close"], df["volume"], 5),
    }


@pytest.mark.parametrize("name", NAMES)
def test_matches_reference_vectors_to_1e9(name):
    df = load(name)
    for column, got in compute(df).items():
        expected = df[column]
        tol = 1e-9 * max(1.0, float(np.nanmax(np.abs(expected))))  # relative for $ volume
        for i, (g, e) in enumerate(zip(got, expected, strict=True)):
            assert is_close(float(g), float(e), tol), f"{name} {column} row {i}: {g} != {e}"


@pytest.mark.parametrize("name", NAMES)
def test_undefined_values_are_nan_not_zero(name):
    got = compute(load(name))
    assert got["rsi2"].iloc[:2].isna().all() and not np.isnan(got["rsi2"].iloc[2])
    assert got["atr14"].iloc[:14].isna().all() and not np.isnan(got["atr14"].iloc[14])
    assert got["sma5"].iloc[:4].isna().all()


def test_prefix_invariance_no_look_ahead(nyse_sessions):
    bars = make_bars(nyse_sessions, seed=3)
    full = compute(bars)
    for k in (30, 100, 200):
        part = compute(bars.iloc[:k].copy())
        for name in full:
            pd.testing.assert_series_equal(full[name].iloc[:k], part[name], check_names=False)


def test_future_data_change_does_not_change_past(nyse_sessions):
    bars = make_bars(nyse_sessions, seed=4)
    mutated = bars.copy()
    mutated.loc[150:, ["open", "high", "low", "close"]] *= 3.0
    a, b = compute(bars), compute(mutated)
    for name in a:
        pd.testing.assert_series_equal(a[name].iloc[:150], b[name].iloc[:150], check_names=False)


def test_rsi_edge_cases():
    up = pd.Series([1.0, 2.0, 3.0, 4.0])
    flat = pd.Series([5.0, 5.0, 5.0, 5.0])
    assert rsi_wilder(up, 2).iloc[-1] == 100.0
    assert rsi_wilder(flat, 2).iloc[-1] == 50.0
    assert rsi_wilder(pd.Series([1.0, 2.0]), 2).isna().all()


def _dividend_bars() -> pd.DataFrame:
    # Close 100 until the ex-date (day 3), where the price drops exactly by the $2 dividend.
    close = [100.0, 100.0, 100.0, 98.0, 98.0, 98.0]
    div = [0.0, 0.0, 0.0, 2.0, 0.0, 0.0]
    return pd.DataFrame(
        {"open": close, "high": close, "low": close, "close": close, "dividends": div}
    )


def test_dividend_neutral_series_has_no_ex_date_drop():
    bars = _dividend_bars()
    star = dividend_neutral(bars)
    # C*_x / C*_{x-1} = (C_x + D_x) / C_{x-1} = 100 / 100 -> no drop on the ex-date
    assert star["close"].iloc[3] == pytest.approx(star["close"].iloc[2])
    assert star["m"].iloc[2] == 1.0 and star["m"].iloc[3] == pytest.approx(1 + 2 / 98)
    # the raw close does drop, so RSI on raw close would see a "pullback"
    assert rsi_wilder(bars["close"], 2).iloc[3] == 0.0
    assert rsi_wilder(star["close"], 2).iloc[3] == 50.0


def test_future_dividend_does_not_change_past_multiplier():
    bars = _dividend_bars()
    m_before = dividend_multiplier(bars["close"], bars["dividends"])
    later = bars.copy()
    later.loc[5, "dividends"] = 1.0  # a new dividend appears later
    m_after = dividend_multiplier(later["close"], later["dividends"])
    pd.testing.assert_series_equal(m_before.iloc[:5], m_after.iloc[:5])


def test_dividend_on_zero_close_is_rejected():
    with pytest.raises(ValueError):
        dividend_multiplier(pd.Series([1.0, 0.0]), pd.Series([0.0, 0.5]))
