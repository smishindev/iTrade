"""Random control for ETF_TOM_V3 (spec §4): for every month in which the strategy has a window,
one window of the same length at a random place earlier in that month — the entry at a uniformly
drawn session from the month's first session up to the last one whose exit open still falls on or
before the strategy's entry session (so the random window never overlaps the turn of the month).
Same ETFs, sizing, limit, stop and costs: only the timing is random.
"""

from __future__ import annotations

from collections.abc import Mapping

import numpy as np
import pandas as pd

from itrade.strategies.etf_tom_v3 import TomParams, window_flags


def control_windows(
    sessions: pd.DatetimeIndex, start, end, p: TomParams, rng: np.random.Generator
) -> tuple[pd.Series, pd.Series]:
    """(enter_next, exit_next) flags for one control run."""
    n = len(sessions)
    months = sessions.to_period("M")
    strat_enter, _ = window_flags(sessions, p)
    s, e = pd.Timestamp(start), pd.Timestamp(end)
    enter = np.zeros(n, dtype=bool)
    leave = np.zeros(n, dtype=bool)
    h, k = p.hold_sessions, -p.entry_day
    for decision in np.flatnonzero(strat_enter.to_numpy()):
        if not (s <= sessions[decision] <= e):
            continue  # only months whose strategy decision lies in the period
        entry = decision + 1  # the strategy's entry session (day -k)
        last = entry + k - 1  # the month's last session (day -1)
        first = int(np.flatnonzero(months == months[last])[0])
        latest = entry - h  # exit open at entry - h + h = the strategy's entry session at most
        if latest < max(first, 1):
            continue  # month too short for a disjoint window (never on the XNYS calendar)
        c = int(rng.integers(max(first, 1), latest + 1))
        enter[c - 1] = True
        leave[c + h - 1] = True  # exit decided after this close -> exit at the open of c + h
    return pd.Series(enter, index=sessions), pd.Series(leave, index=sessions)


def tom_builder(
    seed: int,
    prepared: Mapping[str, pd.DataFrame],
    sessions: pd.DatetimeIndex,
    start,
    end,
    p: TomParams,
) -> dict[str, pd.DataFrame]:
    enter, leave = control_windows(sessions, start, end, p, np.random.default_rng(seed))
    out = {}
    for ticker, f in prepared.items():
        out[ticker] = f.assign(
            enter_next=f.index.map(enter).fillna(False).astype(bool),
            exit_next=f.index.map(leave).fillna(False).astype(bool),
        )
    return out
