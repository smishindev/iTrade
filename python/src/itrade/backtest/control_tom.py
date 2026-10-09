"""Random control for ETF_TOM_V3 (spec §4, after the pre-registration review): for every
strategy window whose scheduled exit lies inside the period, one window of the same length h drawn
uniformly among the non-overlapping blocks of h sessions that end at the strategy's exit open —
the strategy's own block included — and start at least `gap` sessions after the previous
strategy exit (T+3 proceeds have settled). Same ETFs, sizing, limit, stop and costs: only the
timing is random. On a market without a turn-of-month effect the strategy's percentile is then
uniform (review: ≥ 95 in ~5–7% of runs; the earlier "any session of the month" design gave ~9–13%
because overlapping random windows understate the spread).
"""

from __future__ import annotations

from collections.abc import Mapping

import numpy as np
import pandas as pd

from itrade.strategies.etf_tom_v3 import TomParams, window_flags

SETTLEMENT_GAP = 4  # sessions after the previous strategy exit (T+3 settled before buying)


def in_period_windows(
    sessions: pd.DatetimeIndex, start, end, p: TomParams
) -> list[tuple[int, int]]:
    """(entry index, exit-open index) of the strategy windows that lie wholly inside the period:
    decision on or after `start`, exit open on or before `end` (spec §5, review S2)."""
    strat_enter, _ = window_flags(sessions, p)
    s, e = pd.Timestamp(start), pd.Timestamp(end)
    out = []
    for decision in np.flatnonzero(strat_enter.to_numpy()):
        entry, exit_open = decision + 1, decision + 1 + p.hold_sessions
        if sessions[decision] >= s and exit_open < len(sessions) and sessions[exit_open] <= e:
            out.append((entry, exit_open))
    return out


def control_windows(
    sessions: pd.DatetimeIndex,
    start,
    end,
    p: TomParams,
    rng: np.random.Generator,
    gap: int = SETTLEMENT_GAP,
) -> tuple[pd.Series, pd.Series]:
    """(enter_next, exit_next) flags for one control run."""
    n, h = len(sessions), p.hold_sessions
    enter = np.zeros(n, dtype=bool)
    leave = np.zeros(n, dtype=bool)
    strat_enter, _ = window_flags(sessions, p)
    exits = [d + 1 + h for d in np.flatnonzero(strat_enter.to_numpy())]
    for entry, exit_open in in_period_windows(sessions, start, end, p):
        previous = [x for x in exits if x < exit_open]
        earliest = previous[-1] + gap if previous else entry - 3 * h
        blocks = [entry - j * h for j in range(0, 64) if entry - j * h >= max(earliest, 1)]
        c = int(rng.choice(blocks))  # the strategy's own block (j = 0) is always a choice
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
