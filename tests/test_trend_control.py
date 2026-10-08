"""ETF_TREND_V2 random control (P1.H2.08) on synthetic frames."""

from __future__ import annotations

from functools import partial

import numpy as np
import pandas as pd

from itrade.backtest.control import run_control_with
from itrade.backtest.control_trend import (
    breakout_builder,
    breakout_counts_per_year,
    breakout_eligible,
    rotation_builder,
)
from test_trend_rules import SESSIONS, at, frames, rotation_world, run, series, setup

START, END = SESSIONS[0], SESSIONS[-1]


def breakout_frames():
    high = series(25.0, {"2024-06-05": 19.5, "2024-06-06": 25.0, "2024-06-20": 19.0})
    return {
        "X": frames(high_n=high),
        "Y": frames(member=series(True, {"2024-06-10": False})),  # never breaks out
    }


def test_breakout_eligible_and_counts():
    _, p, _, _ = setup("brk_base")
    el = breakout_eligible(breakout_frames(), START, END, p)
    assert len(el) == 2 * len(SESSIONS) - (len(SESSIONS) - at("2024-06-10"))
    counts = breakout_counts_per_year(el)
    assert counts.to_dict() == {2024: len(SESSIONS) - at("2024-06-20") + 1}


def test_breakout_builder_draws_matched_random_breakouts():
    _, p, _, _ = setup("brk_base")
    prep = breakout_frames()
    el = breakout_eligible(prep, START, END, p)
    counts = breakout_counts_per_year(el)
    a = breakout_builder(7, prep, el, counts)
    b = breakout_builder(7, prep, el, counts)
    broke = sum(int((f["close_star"] > f["high_n"]).sum()) for f in a.values())
    assert broke == int(counts.sum())
    for t in a:
        pd.testing.assert_frame_equal(a[t], b[t])  # reproducible by seed
        assert (a[t]["low_n"] == prep[t]["low_n"]).all()  # exits untouched
        hit = a[t]["close_star"] > a[t]["high_n"]
        strength = (a[t]["close_star"] - a[t]["high_n"])[hit] / a[t]["atr_star"][hit]
        assert ((strength > 0) & (strength <= 1)).all()


def test_rotation_builder_randomises_only_eligible_month_end_momentum():
    _, p, _, _ = setup("rot_base")
    _, prep, _ = rotation_world()
    out = rotation_builder(3, prep, SESSIONS, START, END, p)
    month_ends = [pd.Timestamp("2024-05-31"), pd.Timestamp("2024-06-28")]
    for t, f in out.items():
        other = ~f.index.isin(month_ends)
        assert (f.loc[other, "mom"] == prep[t].loc[other, "mom"]).all()
        for d in month_ends:
            if prep[t].loc[d, "mom"] > 0:
                assert 0 < f.loc[d, "mom"] <= 1
            else:  # not eligible: unchanged, still filtered out
                assert f.loc[d, "mom"] == prep[t].loc[d, "mom"]
    again = rotation_builder(3, prep, SESSIONS, START, END, p)
    assert all(np.array_equal(out[t]["mom"], again[t]["mom"]) for t in out)


def test_rotation_control_runs_are_deterministic_and_vary():
    mkt, prep, info = rotation_world()
    _, p, _, _ = setup("rot_base")

    def fn(prepared):
        return run(mkt, prepared, info, "rot_base")

    builder = partial(rotation_builder, prepared=prep, sessions=SESSIONS, start=START, end=END, p=p)
    a = run_control_with(fn, builder, runs=6, base_seed=11)
    b = run_control_with(fn, builder, runs=6, base_seed=11)
    assert a == b
    picked = {
        tuple(sorted(run(mkt, builder(11 + k), info, "rot_base").trades["ticker"]))
        for k in range(6)
    }
    assert len(picked) > 1  # the ranking really is random
    assert all(r.trades > 0 for r in a)
