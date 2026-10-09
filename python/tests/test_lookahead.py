"""No look-ahead (spec §4, research-integrity rule 1): the detector passes the real strategy and
catches strategies that peek at the future."""

from __future__ import annotations

import numpy as np
import pandas as pd
import pytest

from itrade.backtest.causality import causality_violations, decisions_at, mutate_future
from itrade.config import load_strategy
from itrade.strategies.etf_pullback_v1 import SignalParams, prepare_instrument

P = SignalParams.from_strategy(load_strategy("etf_pullback_v1"))


def make_universe(n_tickers: int = 4, n: int = 420, seed: int = 11):
    """Synthetic uptrending ETFs with sharp 2-day dips (so RSI(2) <= 10 signals occur),
    quarterly dividends and one split."""
    rng = np.random.default_rng(seed)
    sessions = pd.DatetimeIndex(pd.bdate_range("2020-01-02", periods=n))
    bars, member = {}, {}
    for k in range(n_tickers):
        r = rng.normal(0.0008, 0.008, n)
        dips = rng.choice(np.arange(220, n - 2), size=12, replace=False)
        r[dips] -= 0.03
        r[dips + 1] -= 0.02
        close = 50.0 * (k + 1) * np.exp(np.cumsum(r))
        df = pd.DataFrame(
            {
                "date": sessions,
                "open": close * (1 + rng.normal(0, 0.002, n)),
                "close": close,
                "volume": rng.uniform(2e6, 4e6, n),
                "dividends": 0.0,
                "splits": 0.0,
            }
        )
        df["high"] = df[["open", "close"]].max(axis=1) * 1.004
        df["low"] = df[["open", "close"]].min(axis=1) * 0.996
        df.loc[df.index % 63 == 30, "dividends"] = 0.2 * (k + 1)
        if k == 0:
            df.loc[350, "splits"] = 2.0
        ticker = f"T{k}"
        bars[ticker] = df
        member[ticker] = pd.Series(True, index=sessions)
    return bars, member, sessions


@pytest.fixture(scope="module")
def universe():
    return make_universe()


@pytest.fixture(scope="module")
def check_dates(universe):
    """Dates with entry signals (so the check is not vacuous) plus quiet dates."""
    bars, member, sessions = universe
    prepared = {t: prepare_instrument(b, member[t], sessions, P) for t, b in bars.items()}
    with_signals = [d for d in sessions[230:] if decisions_at(d, prepared, sessions, P)[0]]
    assert len(with_signals) >= 10, "synthetic data must produce entry signals"
    quiet = [d for d in sessions[230:] if d not in set(with_signals)]
    return with_signals[:12] + quiet[:6]


def test_mutate_future_changes_only_the_future(universe):
    bars, _, _ = universe
    df = bars["T0"]
    d = df["date"].iloc[300]
    m = mutate_future(df, d, seed=1)
    past = df["date"] <= d
    pd.testing.assert_frame_equal(m[past], df[past])
    assert not np.allclose(m.loc[~past, "close"], df.loc[~past, "close"])
    assert (m["dividends"] == df["dividends"]).all() and (m["splits"] == df["splits"]).all()
    assert (m.loc[~past, "high"] >= m.loc[~past, ["open", "close"]].max(axis=1)).all()


def test_real_strategy_is_causal(universe, check_dates):
    bars, member, sessions = universe
    assert causality_violations(bars, member, sessions, P, check_dates) == []


def peek_tomorrows_rsi(bars, member, sessions, p):
    prep = prepare_instrument(bars, member, sessions, p)
    return prep.assign(rsi=prep["rsi"].shift(-1))  # the cheat: tomorrow's RSI


def peek_tomorrows_close(bars, member, sessions, p):
    prep = prepare_instrument(bars, member, sessions, p)
    future_up = prep["close"].shift(-1) > prep["close"]  # the cheat: buy only if tomorrow rises
    return prep.assign(rsi=prep["rsi"].where(future_up, 99.0))


def centered_sma(bars, member, sessions, p):
    prep = prepare_instrument(bars, member, sessions, p)
    centered = prep["close_star"].rolling(p.exit_sma, center=True).mean()  # the cheat
    return prep.assign(sma_exit=centered)


@pytest.mark.parametrize("cheat", [peek_tomorrows_rsi, peek_tomorrows_close, centered_sma])
def test_detector_catches_peeking_strategies(universe, check_dates, cheat):
    bars, member, sessions = universe
    violations = causality_violations(bars, member, sessions, P, check_dates, prepare=cheat)
    assert violations, f"{cheat.__name__} looked into the future but was not caught"


def test_signal_uses_the_bar_of_t_itself(universe, check_dates):
    """Decisions at t use t's own close (not only t-1): lifting just t's close removes the
    pullback signal and moves the limit price."""
    bars, member, sessions = universe
    d = check_dates[0]  # a date with an entry signal
    prepared = {t: prepare_instrument(b, member[t], sessions, P) for t, b in bars.items()}
    entries, _, _ = decisions_at(d, prepared, sessions, P)
    ticker = entries[0].ticker
    lifted = bars[ticker].copy()
    on_d = lifted["date"] == d
    lifted.loc[on_d, ["open", "high", "low", "close"]] *= 1.10
    prep_lifted = {**prepared, ticker: prepare_instrument(lifted, member[ticker], sessions, P)}
    entries_lifted, _, _ = decisions_at(d, prep_lifted, sessions, P)
    assert ticker not in {c.ticker for c in entries_lifted}
