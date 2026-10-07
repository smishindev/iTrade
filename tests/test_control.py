"""Random-entry control (spec §10)."""

from __future__ import annotations

from functools import partial

import numpy as np
import pandas as pd
import pytest

from itrade.backtest.control import (
    ControlRun,
    control_prepared,
    eligible_pairs,
    percentile,
    random_picks,
    run_control,
    strategy_counts_per_year,
)
from itrade.backtest.engine import InstrumentInfo, market_frame, run_backtest
from itrade.strategies.etf_pullback_v1 import prepare_instrument
from test_engine import COSTS, POLICY, RULES, SP
from test_lookahead import make_universe


@pytest.fixture(scope="module")
def world():
    bars, member, sessions = make_universe(n_tickers=3, n=520, seed=21)
    prepared = {t: prepare_instrument(b, member[t], sessions, SP) for t, b in bars.items()}
    return bars, sessions, prepared


def test_eligible_pairs_respect_every_filter_but_rsi(world):
    _, sessions, prepared = world
    el = eligible_pairs(prepared, sessions[230], sessions[-1])
    assert len(el) > 100
    for _, row in el.sample(40, random_state=1).iterrows():
        f = prepared[row.ticker].loc[row.date]
        assert f["member"] and f["close_star"] > f["sma_trend"] and not f["ex_next"]


def test_picks_match_strategy_counts_per_year_and_are_reproducible(world):
    _, sessions, prepared = world
    el = eligible_pairs(prepared, sessions[230], sessions[-1])
    counts = strategy_counts_per_year(el, SP.rsi_max)
    assert counts.sum() > 0
    a, b, c = random_picks(el, counts, 5), random_picks(el, counts, 5), random_picks(el, counts, 6)
    pd.testing.assert_frame_equal(a, b)
    assert not a[["date", "ticker"]].equals(c[["date", "ticker"]])
    assert (a.groupby(a["date"].dt.year).size() == counts).all()
    assert not a.duplicated(["date", "ticker"]).any()
    assert a["rsi"].between(0, 10).all()


def test_control_prepared_sets_rsi_only_at_picks(world):
    _, sessions, prepared = world
    el = eligible_pairs(prepared, sessions[230], sessions[-1])
    picks = random_picks(el, strategy_counts_per_year(el, SP.rsi_max), 1)
    cp = control_prepared(prepared, picks)
    total_low = sum(int((f["rsi"] <= 10).sum()) for f in cp.values())
    assert total_low == len(picks)
    untouched = ["close_star", "sma_trend", "atr_star", "member"]
    for t in prepared:
        pd.testing.assert_frame_equal(cp[t][untouched], prepared[t][untouched])


def _engine(prepared, *, market, info, sessions):
    return run_backtest(
        market,
        prepared,
        info,
        sessions,
        SP,
        POLICY,
        COSTS,
        RULES,
        __import__("decimal").Decimal("3280"),
        sessions[230],
        sessions[-1],
    )


def test_run_control_serial_is_deterministic(world):
    bars, sessions, prepared = world
    market = {t: market_frame(b) for t, b in bars.items()}
    info = {t: InstrumentInfo("g" + t, 2.0) for t in bars}
    fn = partial(_engine, market=market, info=info, sessions=sessions)
    el = eligible_pairs(prepared, sessions[230], sessions[-1])
    counts = strategy_counts_per_year(el, SP.rsi_max)
    a = run_control(fn, prepared, el, counts, runs=3, base_seed=100)
    b = run_control(fn, prepared, el, counts, runs=3, base_seed=100)
    assert a == b and [r.run for r in a] == [0, 1, 2]


def test_percentile():
    runs = [ControlRun(i, 50, v) for i, v in enumerate([-0.2, -0.1, 0.0, 0.1, np.nan])]
    assert percentile(0.05, runs) == 75.0
    assert percentile(-1.0, runs) == 0.0
