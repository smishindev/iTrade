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
        "1663cf3bac4c6092c41c60706ec8ae3bdf1af28859b2afab6ad034c91f982d9d"
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


def test_control_windows_are_disjoint_matched_and_reproducible():
    _, p, _, _ = setup("tom_spy")
    strat_enter, _ = window_flags(SESSIONS, p)
    a = control_windows(SESSIONS, START, END, p, np.random.default_rng(3))
    b = control_windows(SESSIONS, START, END, p, np.random.default_rng(3))
    assert a[0].equals(b[0]) and a[1].equals(b[1])
    in_period = strat_enter[(SESSIONS >= START) & (SESSIONS <= END)]
    assert a[0].sum() == in_period.sum() == a[1].sum()
    for d in SESSIONS[a[0].to_numpy()]:
        entry = SESSIONS.get_loc(d) + 1
        exit_open = SESSIONS[entry + p.hold_sessions]
        assert a[1].iloc[entry + p.hold_sessions - 1]  # exit decided h - 1 sessions after entry
        assert day_number(exit_open, SESSIONS)[1] <= p.entry_day  # ends before the window
    prepared = {t: prepare_tom_instrument(b, MEMBER[t], SESSIONS, p) for t, b in RAW.items()}
    built = tom_builder(5, prepared, SESSIONS, START, END, p)
    res, *_ = run("tom_spy", built)
    assert len(res.trades) > 0 and (res.trades["exit_reason"] != "tom_exit").sum() < len(res.trades)


def test_decisions_do_not_depend_on_the_future():
    _, p, _, _ = setup("tom_spy")
    groups = {t: "us_equity" for t in TICKERS}
    dates = list(SESSIONS[100:400:13])
    found = trend_causality_violations(
        RAW, MEMBER, SESSIONS, p, groups, dates, prepare_tom_instrument
    )
    assert found == []
