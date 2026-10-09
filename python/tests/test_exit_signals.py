"""ETF_PULLBACK_V1 exit signals (spec §4.3)."""

from __future__ import annotations

import numpy as np
import pandas as pd
import pytest

from itrade.config import load_strategy
from itrade.strategies.etf_pullback_v1 import SignalParams, exit_signal, holding_sessions

P = SignalParams.from_strategy(load_strategy("etf_pullback_v1"))
SESSIONS = pd.DatetimeIndex(pd.bdate_range("2024-03-04", periods=30))
ENTRY = SESSIONS[0]


def frame(close_star: float = 100.0, sma_exit: float = 101.0, missing: list[int] = ()):
    """Prepared frame: close* below SMA(exit) every day (no exit by price) unless overridden."""
    idx = SESSIONS.delete(list(missing)) if missing else SESSIONS
    return pd.DataFrame({"close_star": close_star, "sma_exit": sma_exit}, index=idx)


def test_max_hold_from_config():
    assert P.max_hold_sessions == 10


def test_holding_sessions_counts_entry_as_one_and_skips_weekends():
    assert holding_sessions(SESSIONS, ENTRY, ENTRY) == 1
    assert holding_sessions(SESSIONS, ENTRY, SESSIONS[9]) == 10
    friday, monday = pd.Timestamp("2024-03-08"), pd.Timestamp("2024-03-11")
    assert holding_sessions(SESSIONS, friday, monday) == 2


def test_no_exit_while_below_sma_and_under_max_hold():
    f = frame()
    for i in range(9):  # sessions 1..9
        assert exit_signal(SESSIONS[i], "X", ENTRY, f, SESSIONS, P) is None


def test_max_hold_exactly_on_the_tenth_session():
    sig = exit_signal(SESSIONS[9], "X", ENTRY, frame(), SESSIONS, P)
    assert sig is not None and sig.reason == "max_hold" and sig.holding_sessions == 10


def test_exit_sma_takes_precedence_over_max_hold():
    sig = exit_signal(SESSIONS[9], "X", ENTRY, frame(close_star=102.0), SESSIONS, P)
    assert sig.reason == "exit_sma"


def test_exit_sma_on_entry_day_is_decided_after_close():
    sig = exit_signal(ENTRY, "X", ENTRY, frame(close_star=101.5), SESSIONS, P)
    assert sig == sig.__class__(ENTRY, "X", "exit_sma", 1)  # executes at the next open, not today


def test_close_equal_to_sma_is_not_an_exit():
    assert exit_signal(SESSIONS[3], "X", ENTRY, frame(close_star=101.0), SESSIONS, P) is None


def test_undefined_sma_falls_back_to_max_hold_only():
    f = frame(sma_exit=np.nan, close_star=500.0)
    assert exit_signal(SESSIONS[2], "X", ENTRY, f, SESSIONS, P) is None
    assert exit_signal(SESSIONS[9], "X", ENTRY, f, SESSIONS, P).reason == "max_hold"


def test_missing_bar_still_counts_sessions_for_max_hold():
    f = frame(missing=[9])  # no bar on the 10th session
    sig = exit_signal(SESSIONS[9], "X", ENTRY, f, SESSIONS, P)
    assert sig.reason == "max_hold"


def test_check_before_entry_is_an_error():
    with pytest.raises(ValueError):
        exit_signal(SESSIONS[0], "X", SESSIONS[1], frame(), SESSIONS, P)
