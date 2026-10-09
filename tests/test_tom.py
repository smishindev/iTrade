"""ETF_TOM_V3 (hypothesis H3) on synthetic data only: H3 is not run on real data before its
pre-registration."""

from __future__ import annotations

from decimal import Decimal as D

import numpy as np
import pandas as pd
import pytest

from itrade.backtest.account import settlement_rules
from itrade.backtest.causality import trend_causality_violations
from itrade.backtest.control_tom import control_windows, tom_builder
from itrade.backtest.engine import InstrumentInfo, market_frame, run_backtest
from itrade.backtest.review_tom import check_tom_trade, day_number
from itrade.backtest.risk import RiskPolicy
from itrade.backtest.variants import apply_variant
from itrade.config import load_strategy, strategy_version
from itrade.costs import CostConfig
from itrade.strategies.etf_tom_v3 import TomParams, prepare_tom_instrument, window_flags

PARAMS = load_strategy("etf_tom_v3")
COSTS = CostConfig.load()
SESSIONS = pd.DatetimeIndex(pd.bdate_range("2019-01-01", periods=500))
TICKERS = ["SPY", "IWM", "QQQ"]


def setup(variant):
    params, options, _ = apply_variant(PARAMS, variant)
    p = TomParams.from_strategy(params)
    policy = RiskPolicy.for_tom(params, COSTS.min_per_order_usd, COSTS.slippage_bps)
    return params, p, policy, options


def bars(seed: int) -> pd.DataFrame:
    rng = np.random.default_rng(seed)
    n = len(SESSIONS)
    close = 100 * np.exp(np.cumsum(rng.normal(0.0004, 0.011, n)))
    opens = close * np.exp(rng.normal(0, 0.004, n))
    high = np.maximum(opens, close) * (1 + rng.uniform(0, 0.008, n))
    low = np.minimum(opens, close) * (1 - rng.uniform(0, 0.008, n))
    return pd.DataFrame({
        "date": SESSIONS, "open": opens, "high": high, "low": low, "close": close,
        "volume": 5e6, "dividends": np.where(np.arange(n) % 63 == 40, 0.4, 0.0), "splits": 0.0,
    })  # fmt: skip


RAW = {t: bars(i) for i, t in enumerate(TICKERS)}
MEMBER = {t: pd.Series(True, index=SESSIONS) for t in TICKERS}
INFO = {t: InstrumentInfo("us_equity", 1.0) for t in TICKERS}
START, END = SESSIONS[30], SESSIONS[-1]


def run(variant, prepared=None):
    params, p, policy, options = setup(variant)
    prepared = prepared or {
        t: prepare_tom_instrument(b, MEMBER[t], SESSIONS, p) for t, b in RAW.items()
    }
    market = {t: market_frame(b) for t, b in RAW.items()}
    res = run_backtest(
        market, prepared, INFO, SESSIONS, p, policy, COSTS, settlement_rules(params), D("3280"),
        START, END, options=options, r_denominator="planned", count_exiting_positions=False,
    )  # fmt: skip
    return res, market, prepared, p


def test_version_is_pinned_and_variants_parse():
    assert strategy_version("etf_tom_v3") == (
        "f6926b8b3c2ed62de6a15641839efd505f36ce9ef37e5a879fac8e8bf5ed3ee6"
    )
    assert len(PARAMS["variants"]) == 8 <= 20
    holds = {v["name"]: setup(v["name"])[1].hold_sessions for v in PARAMS["variants"]}
    assert holds == {
        "tom_spy": 4, "tom_iwm": 4, "tom_qqq": 4, "tom_basket": 4, "tom_entry2": 5,
        "tom_exit2": 3, "tom_exit4": 5, "tom_costs_x2": 4,
    }  # fmt: skip


@pytest.mark.parametrize("variant", ["tom_spy", "tom_entry2", "tom_exit2", "tom_exit4"])
def test_window_calendar(variant):
    _, p, _, _ = setup(variant)
    enter, leave = window_flags(SESSIONS, p)
    for d in SESSIONS[enter.to_numpy()]:
        entry = SESSIONS[SESSIONS.get_loc(d) + 1]
        assert day_number(entry, SESSIONS)[1] == p.entry_day
    for d in SESSIONS[leave.to_numpy()]:
        assert day_number(d, SESSIONS)[0] == p.exit_day
    assert enter.sum() == leave.sum() == len(SESSIONS.to_period("M").unique()) - 1


@pytest.mark.parametrize("variant", ["tom_spy", "tom_basket", "tom_entry2", "tom_exit4"])
def test_every_trade_is_re_derived(variant):
    res, market, prepared, p = run(variant)
    assert len(res.trades) >= 15
    for _, tr in res.trades.iterrows():
        bad = [c for c in check_tom_trade(tr, market, prepared, SESSIONS, p) if not c.ok]
        assert not bad, (variant, tr["ticker"], tr["entry_date"], bad)
    if variant == "tom_basket":
        assert set(res.trades["ticker"]) == set(TICKERS)
        assert (
            res.trades["qty"] * res.trades["entry_price"] <= D("3280") * D("0.8") / 3 + 200
        ).all()


def test_sizing_is_eighty_percent_in_whole_shares():
    res, *_ = run("tom_spy")
    first = res.trades.iloc[0]
    assert first["qty"] == int(D("0.80") * D("3280") / first["limit"])


def test_tampered_trades_are_caught():
    res, market, prepared, p = run("tom_spy")
    tr = res.trades.iloc[0].to_dict()

    def failed(**change):
        return {
            c.name for c in check_tom_trade(tr | change, market, prepared, SESSIONS, p) if not c.ok
        }

    assert "entry at the open" in failed(entry_price=tr["entry_price"] + D("0.10"))
    assert "R = P&L / planned risk" in failed(r=tr["r"] + 1)
    i = SESSIONS.get_loc(tr["entry_date"])
    assert "entry on day -1" in failed(entry_date=SESSIONS[i - 2])
    assert any(n.startswith("exit") for n in failed(exit_date=SESSIONS[i + 6]))


def test_control_windows_are_blocks_matched_and_reproducible():
    """Review B1: one window per strategy window, drawn among non-overlapping blocks of the same
    length ending at the strategy's exit (its own block included), starting >= 4 sessions after
    the previous strategy exit (settled cash)."""
    from itrade.backtest.control_tom import SETTLEMENT_GAP, in_period_windows

    _, p, _, _ = setup("tom_spy")
    h = p.hold_sessions
    a = control_windows(SESSIONS, START, END, p, np.random.default_rng(3))
    b = control_windows(SESSIONS, START, END, p, np.random.default_rng(3))
    assert a[0].equals(b[0]) and a[1].equals(b[1])
    windows = in_period_windows(SESSIONS, START, END, p)
    entries = np.flatnonzero(a[0].to_numpy()) + 1
    assert len(entries) == len(windows) == a[1].sum()
    strat_enter, _ = window_flags(SESSIONS, p)
    all_exits = [d + 1 + h for d in np.flatnonzero(strat_enter.to_numpy())]
    own = 0
    for (entry, exit_open), c in zip(windows, entries, strict=True):
        assert (exit_open - (c + h)) % h == 0 and c <= entry  # a block ending at the exit
        assert a[1].iloc[c + h - 1]
        before = [x for x in all_exits if x < exit_open]
        assert c >= (before[-1] + SETTLEMENT_GAP if before else entry - 3 * h)
        own += c == entry
    assert 0 < own < len(windows)  # the strategy's own block is sometimes drawn, not always
    prepared = {t: prepare_tom_instrument(b, MEMBER[t], SESSIONS, p) for t, b in RAW.items()}
    res, *_ = run("tom_spy", tom_builder(5, prepared, SESSIONS, START, END, p))
    unfilled = int((res.skipped["reason"] == "limit_not_reached").sum())
    assert len(res.trades) + unfilled == len(windows)
    assert not (res.skipped["reason"] == "settled_cash").any()  # the gap keeps cash settled


def test_only_windows_wholly_inside_the_period():
    """Review S2: a window whose scheduled exit falls after the period end is not opened."""
    res, *_ = run("tom_spy")
    assert "end_of_period" not in set(res.trades["exit_reason"])
    _, p, _, _ = setup("tom_spy")
    from itrade.backtest.control_tom import in_period_windows

    unfilled = int((res.skipped["reason"] == "limit_not_reached").sum())
    assert len(res.trades) + unfilled == len(in_period_windows(SESSIONS, START, END, p))


def test_decisions_do_not_depend_on_the_future():
    """Review S4: every entry and exit decision day and its neighbours."""
    _, p, _, _ = setup("tom_spy")
    groups = {t: "us_equity" for t in TICKERS}
    enter, leave = window_flags(SESSIONS, p)
    days = np.flatnonzero((enter | leave).to_numpy())
    near = sorted({i + k for i in days for k in (-1, 0, 1) if 40 <= i + k < len(SESSIONS) - 1})
    dates = list(SESSIONS[near])
    assert (
        trend_causality_violations(RAW, MEMBER, SESSIONS, p, groups, dates, prepare_tom_instrument)
        == []
    )


def test_a_peeking_rule_is_caught():
    _, p, _, _ = setup("tom_spy")
    groups = {t: "us_equity" for t in TICKERS}

    def peeking(b, member, sessions, params):
        out = prepare_tom_instrument(b, member, sessions, params)
        # lookahead-ok: deliberately enters only before a rise to prove the detector works
        up = out["close_star"].shift(-3) > out["close_star"]
        return out.assign(enter_next=out["enter_next"] & up)

    enter, _ = window_flags(SESSIONS, p)
    dates = list(SESSIONS[np.flatnonzero(enter.to_numpy())][3:15])
    assert trend_causality_violations(RAW, MEMBER, SESSIONS, p, groups, dates, peeking)
