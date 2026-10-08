"""Independent re-derivation of ETF_TREND_V2 trades (P1.H2.09), end to end on synthetic bars:
random-walk bars -> prepare_trend_instrument -> engine -> every trade re-derived and checked."""

from __future__ import annotations

from decimal import Decimal as D

import numpy as np
import pandas as pd
import pytest

from itrade.backtest.account import settlement_rules
from itrade.backtest.engine import InstrumentInfo, market_frame, run_backtest
from itrade.backtest.review_trend import check_trend_trade
from itrade.strategies.etf_trend_v2 import prepare_trend_instrument
from test_trend_rules import COSTS, setup

SESSIONS = pd.DatetimeIndex(pd.bdate_range("2020-01-01", periods=700))
GROUPS = {"A": "g1", "B": "g1", "C": "g2", "D": "g3", "E": "g4", "F": "g5"}


def bars(seed: int, drift: float) -> pd.DataFrame:
    rng = np.random.default_rng(seed)
    close = 50 * np.exp(np.cumsum(rng.normal(drift, 0.012, len(SESSIONS))))
    opens = close * np.exp(rng.normal(0, 0.004, len(SESSIONS)))
    high = np.maximum(opens, close) * (1 + rng.uniform(0, 0.01, len(SESSIONS)))
    low = np.minimum(opens, close) * (1 - rng.uniform(0, 0.01, len(SESSIONS)))
    div = np.where(np.arange(len(SESSIONS)) % 63 == 30, 0.2, 0.0)
    return pd.DataFrame({
        "date": SESSIONS, "open": opens, "high": high, "low": low, "close": close,
        "volume": 3e6, "dividends": div, "splits": 0.0,
    })  # fmt: skip


@pytest.fixture(scope="module", params=["rot_base", "brk_base"])
def world(request):
    variant = request.param
    params, p, policy, options = setup(variant)
    raw = {t: bars(i, 0.0006 * (i - 2)) for i, t in enumerate(GROUPS)}
    member = pd.Series(True, index=SESSIONS)
    prepared = {t: prepare_trend_instrument(b, member, SESSIONS, p) for t, b in raw.items()}
    market = {t: market_frame(b) for t, b in raw.items()}
    info = {t: InstrumentInfo(g, 2.0) for t, g in GROUPS.items()}
    res = run_backtest(
        market, prepared, info, SESSIONS, p, policy, COSTS, settlement_rules(params), D("3280"),
        SESSIONS[260], SESSIONS[-1], options=options, r_denominator="planned",
        count_exiting_positions=False,
    )  # fmt: skip
    return variant, res, market, prepared, p


def checks_of(trade, market, prepared, p):
    return check_trend_trade(trade, market, prepared, SESSIONS, p, GROUPS)


def test_every_simulated_trade_passes(world):
    variant, res, market, prepared, p = world
    assert len(res.trades) >= 5, variant
    reasons = set(res.trades["exit_reason"])
    assert reasons & {"rotation_out", "channel_exit"}, reasons
    for _, tr in res.trades.iterrows():
        bad = [c for c in checks_of(tr, market, prepared, p) if not c.ok]
        assert not bad, (variant, tr["ticker"], tr["entry_date"], bad)


def test_tampered_trades_are_caught(world):
    _, res, market, prepared, p = world
    tr = res.trades.iloc[0].to_dict()

    def failed(**change):
        return {c.name for c in checks_of(tr | change, market, prepared, p) if not c.ok}

    assert "entry at the open" in failed(entry_price=tr["entry_price"] + D("0.05"))
    assert "limit" in failed(limit=tr["limit"] + D("0.10"))
    assert "R = P&L / planned risk" in failed(r=tr["r"] + 1)
    later = SESSIONS[SESSIONS.get_loc(tr["exit_date"]) + 5]
    assert any(n.startswith("exit") or n.startswith("nothing") for n in failed(exit_date=later))


def test_wrong_signal_day_is_caught(world):
    variant, res, market, prepared, p = world
    tr = res.trades.iloc[0].to_dict()
    i = SESSIONS.get_loc(tr["entry_date"])
    moved = tr | {"entry_date": SESSIONS[i - 3]}  # no signal three sessions earlier
    bad = {c.name for c in checks_of(moved, market, prepared, p) if not c.ok}
    assert bad, variant


def test_full_report_builds_for_both_rule_sets(tmp_path):
    """P1.H2.09 done-criterion: the report of a base variant forms on synthetic data."""
    import dataclasses
    import json

    from itrade.backtest.inputs import StrategyInputs
    from itrade.backtest.report import write_report
    from itrade.backtest.runner import Run, engine_kwargs
    from itrade.config import load_named_universe

    real = load_named_universe("etf_pullback_v1")
    names = ["SPY", "QQQ", "EFA", "EEM", "TLT", "GLD"]
    universe = dataclasses.replace(
        real, instruments=tuple(i for i in real.instruments if i.ticker in names)
    )
    for variant in ("rot_base", "brk_base"):
        params, p, _, options = setup(variant)
        raw = {t: bars(i + 10, 0.0005 * (i - 2)) for i, t in enumerate(names)}
        member = pd.Series(True, index=SESSIONS)
        prepared = {t: prepare_trend_instrument(b, member, SESSIONS, p) for t, b in raw.items()}
        inputs = StrategyInputs(params, p, universe, raw, pd.DataFrame(), SESSIONS, prepared)
        start, end = str(SESSIONS[260].date()), str(SESSIONS[-1].date())
        res = run_backtest(prepared=prepared, **engine_kwargs(inputs, params, start, end, options))
        assert len(res.trades) > 0
        run = Run("etf_trend_v2", variant, "development", start, end, False, options, inputs, res)
        report = write_report(run, tmp_path)
        meta = json.loads((report.directory / "meta.json").read_text(encoding="utf-8"))
        assert meta["run_options"]["cost_multiplier"] == 1.0
        assert 0 < meta["summary"]["time_in_market"] <= 1
        assert meta["equal_weight_cagr"] is not None
        text = (report.directory / "report.md").read_text(encoding="utf-8")
        assert "Equal-weight universe" in text and "Time with a position" in text
        # R on planned risk in the report's trades
        t = res.trades.iloc[0]
        assert t["risk"] == t["qty"] * (t["limit"] - t["stop"])
