"""ETF_PULLBACK_V1 entry signals (spec §4.1–4.2)."""

from __future__ import annotations

from decimal import Decimal

import numpy as np
import pandas as pd
import pytest

from itrade.backtest.corporate_actions import split_scale
from itrade.config import load_strategy
from itrade.strategies.etf_pullback_v1 import (
    SignalParams,
    entry_levels,
    entry_signals,
    floor_tick,
    prepare_instrument,
)

P = SignalParams.from_strategy(load_strategy("etf_pullback_v1"))
T = pd.Timestamp("2024-06-03")


def row(**overrides) -> pd.DataFrame:
    """A prepared row at T that passes every entry condition unless overridden."""
    base = {
        "close": 100.0,
        "close_star": 100.0,
        "m": 1.0,
        "s": 1.0,
        "sma_trend": 90.0,
        "sma_exit": 101.0,
        "rsi": 5.0,
        "atr_star": 2.0,
        "adv": 1e8,
        "member": True,
        "ex_next": False,
    }
    base.update(overrides)
    return pd.DataFrame([base], index=pd.DatetimeIndex([T], name="date"))


def test_params_from_config():
    assert (P.trend_sma, P.rsi_period, P.atr_period, P.exit_sma) == (200, 2, 14, 5)
    assert (P.rsi_max, P.limit_atr, P.stop_atr) == (10.0, 0.5, 2.0)
    assert P.tick_size == Decimal("0.01") and P.price_series == "dividend_neutral"


def test_floor_tick_ignores_binary_noise():
    assert floor_tick(100.99999999999999, Decimal("0.01")) == Decimal("101.00")
    assert floor_tick(101.0049, Decimal("0.01")) == Decimal("101.00")
    assert floor_tick(95.992, Decimal("0.01")) == Decimal("95.99")


def test_levels_by_hand():
    # close 100, ATR 2.004: limit = floor(100 + 1.002) = 101.00, stop = floor(100 - 4.008) = 95.99
    assert entry_levels(100.0, 2.004, 1.0, 1.0, P) == (Decimal("101.00"), Decimal("95.99"))
    # ATR on the dividend-neutral scale (M = 2) is divided back: same levels
    assert entry_levels(100.0, 4.008, 2.0, 1.0, P) == (Decimal("101.00"), Decimal("95.99"))
    # a later 2:1 split (S = 2): traded price is twice the adjusted one
    assert entry_levels(100.0, 2.004, 1.0, 2.0, P) == (Decimal("202.00"), Decimal("191.98"))


def test_signal_when_all_conditions_hold():
    cands, skipped = entry_signals(T, {"AAA": row()}, set(), P)
    assert [c.ticker for c in cands] == ["AAA"] and skipped == []
    c = cands[0]
    assert (c.limit, c.stop, c.close_raw) == (Decimal("101.00"), Decimal("96.00"), Decimal("100"))


@pytest.mark.parametrize(
    "override",
    [
        {"member": False},
        {"close_star": 89.0},  # below the trend SMA
        {"rsi": 10.01},  # just above the threshold
        {"sma_trend": np.nan},  # trend not defined yet
        {"rsi": np.nan},
    ],
)
def test_no_signal_when_a_condition_fails(override):
    assert entry_signals(T, {"AAA": row(**override)}, set(), P) == ([], [])


def test_rsi_exactly_at_threshold_is_a_signal():
    cands, _ = entry_signals(T, {"AAA": row(rsi=10.0)}, set(), P)
    assert len(cands) == 1


@pytest.mark.parametrize(
    ("override", "held", "reason"),
    [
        ({}, {"AAA"}, "already_held"),
        ({"ex_next": True}, set(), "ex_dividend_next"),
        ({"atr_star": 60.0}, set(), "invalid_levels"),  # stop below zero
        ({"atr_star": np.nan}, set(), "invalid_levels"),
    ],
)
def test_skipped_with_reason(override, held, reason):
    cands, skipped = entry_signals(T, {"AAA": row(**override)}, held, P)
    assert cands == [] and [s.reason for s in skipped] == [reason]


def test_order_rsi_then_volume_then_ticker():
    prepared = {
        "CCC": row(rsi=3.0, adv=1e8),
        "BBB": row(rsi=3.0, adv=2e8),
        "AAA": row(rsi=7.0, adv=9e9),
        "ABA": row(rsi=3.0, adv=1e8),
    }
    cands, _ = entry_signals(T, prepared, set(), P)
    assert [c.ticker for c in cands] == ["BBB", "ABA", "CCC", "AAA"]


def test_date_without_bar_is_ignored():
    assert entry_signals(pd.Timestamp("2024-06-04"), {"AAA": row()}, set(), P) == ([], [])


# --- prepare_instrument --------------------------------------------------------------------------


def _bars(n=260, seed=1):
    rng = np.random.default_rng(seed)
    dates = pd.bdate_range("2023-01-02", periods=n)
    close = 50 * np.exp(np.cumsum(rng.normal(0.0005, 0.01, n)))
    return pd.DataFrame(
        {
            "date": dates,
            "open": close,
            "high": close * 1.01,
            "low": close * 0.99,
            "close": close,
            "volume": 5e6,
            "dividends": 0.0,
            "splits": 0.0,
        }
    )


def test_prepare_columns_ex_next_and_split_scale():
    bars = _bars()
    bars.loc[100, "dividends"] = 0.5
    bars.loc[200, "splits"] = 2.0
    sessions = pd.DatetimeIndex(bars["date"])
    member = pd.Series(True, index=sessions)
    prep = prepare_instrument(bars, member, sessions, P)
    d = sessions
    assert prep.loc[d[99], "ex_next"] and not prep.loc[d[100], "ex_next"]
    assert prep.loc[d[199], "s"] == 2.0 and prep.loc[d[200], "s"] == 1.0
    assert prep["sma_trend"].iloc[:199].isna().all() and not np.isnan(prep["sma_trend"].iloc[199])
    assert prep.loc[d[100], "m"] > prep.loc[d[99], "m"] == 1.0


def test_prepare_is_prefix_invariant():
    bars = _bars(300, seed=7)
    sessions = pd.DatetimeIndex(bars["date"])
    member = pd.Series(True, index=sessions)
    full = prepare_instrument(bars, member, sessions, P)
    part = prepare_instrument(bars.iloc[:250], member, sessions, P)
    causal = ["close_star", "m", "sma_trend", "sma_exit", "rsi", "atr_star", "adv", "member"]
    pd.testing.assert_frame_equal(full.iloc[:250][causal], part[causal])


def test_split_scale():
    bars = _bars(5)
    bars.loc[2, "splits"] = 2.0
    bars.loc[4, "splits"] = 3.0
    assert list(split_scale(bars)) == [6.0, 6.0, 3.0, 3.0, 1.0]
