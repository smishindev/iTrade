"""Sanity runs (roadmap P1.A.19): buy-and-hold total return, zero signals, plausibility.

(1) The hand-checked round trip to the cent is tests/test_engine.py::test_one_round_trip_by_hand.
"""

from __future__ import annotations

from decimal import Decimal as D

import numpy as np
import pandas as pd
import pytest

from itrade.backtest.benchmark import annualised, buy_and_hold
from itrade.backtest.engine import InstrumentInfo, market_frame, run_backtest
from itrade.backtest.metrics import implausibility_flags
from itrade.backtest.risk import RiskPolicy
from itrade.config import project_root
from itrade.strategies.etf_pullback_v1 import prepare_instrument
from test_engine import COSTS, POLICY, RULES, SP, run, scenario
from test_lookahead import make_universe


def synthetic_market(n=500, seed=5, div_every=63, div=0.3, split_at=None):
    rng = np.random.default_rng(seed)
    dates = pd.bdate_range("2015-01-02", periods=n)
    close = 50 * np.exp(np.cumsum(rng.normal(0.0004, 0.01, n)))
    bars = pd.DataFrame(
        {
            "date": dates,
            "open": close * (1 + rng.normal(0, 0.002, n)),
            "close": close,
            "dividends": 0.0,
            "splits": 0.0,
        }
    )
    bars["high"] = bars[["open", "close"]].max(axis=1) * 1.002
    bars["low"] = bars[["open", "close"]].min(axis=1) * 0.998
    bars.loc[bars.index % div_every == div_every - 1, "dividends"] = div
    if split_at is not None:
        bars.loc[split_at, "splits"] = 2.0
    return bars


def total_return(bars: pd.DataFrame) -> float:
    """Independent reference: first open -> first close, then daily (C_t + D_t) / C_{t-1}."""
    c, dv = bars["close"].to_numpy(), bars["dividends"].to_numpy()
    tr = bars["close"].iloc[0] / bars["open"].iloc[0]
    for i in range(1, len(c)):
        tr *= (c[i] + dv[i]) / c[i - 1]
    return float(tr)


def hold(bars: pd.DataFrame) -> pd.DataFrame:
    return buy_and_hold(market_frame(bars), bars["date"].iloc[0], bars["date"].iloc[-1], D("10000"))


def test_buy_and_hold_reproduces_total_return():
    bars = synthetic_market()
    got = float(hold(bars)["equity"].iloc[-1] / D("10000"))
    assert got == pytest.approx(total_return(bars), rel=1e-4)


def test_buy_and_hold_is_unaffected_by_split_adjustment():
    """One economic history, two encodings: no split (prices P), or a 2:1 split on bar 200 stored
    vendor-style (all prices P/2, splits[200] = 2; raw = P before, P/2 after, shares x2)."""
    no_split = synthetic_market()
    vendor = no_split.copy()
    vendor[["open", "high", "low", "close", "dividends"]] /= 2.0
    vendor.loc[200, "splits"] = 2.0
    a = float(hold(vendor)["equity"].iloc[-1])
    b = float(hold(no_split)["equity"].iloc[-1])
    assert a == pytest.approx(b, rel=1e-5)


def test_zero_signal_strategy_loses_exactly_nothing():
    market, prepared = scenario()
    prepared["AAA"]["rsi"] = 50.0  # never a signal
    res = run(market, prepared)
    assert res.trades.empty and res.signals == 0
    assert set(res.equity["equity"]) == {D("3280")}


def test_peeking_strategy_produces_an_implausible_result():
    """A strategy that knows the next two opens wins almost always: the report must flag it
    (and the look-ahead detector of P1.A.13 catches the cheat itself)."""
    bars, member, sessions = make_universe(n_tickers=3, n=500, seed=3)
    policy = RiskPolicy(**{**POLICY.__dict__, "granularity": D("0.0001"), "max_cost_in_r": D("9")})
    info = {t: InstrumentInfo(t, 2.0) for t in bars}
    market = {t: market_frame(b) for t, b in bars.items()}
    prepared = {}
    for t, b in bars.items():
        prep = prepare_instrument(b, member[t], sessions, SP)
        o = b.set_index("date")["open"]
        gain = o.shift(-2) > o.shift(-1) * 1.003  # the cheat: tomorrow's open -> the day after
        prepared[t] = prep.assign(
            rsi=np.where(gain.to_numpy(), 5.0, 50.0), sma_trend=0.0, sma_exit=0.0, member=True
        )
    res = run_backtest(
        market,
        prepared,
        info,
        sessions,
        SP,
        policy,
        COSTS,
        RULES,
        D("3280"),
        sessions[250],
        sessions[-1],
    )
    assert len(res.trades) > 30
    assert "win_rate" in implausibility_flags(res.trades)


SPY = project_root() / "data" / "curated" / "bars" / "SPY.parquet"


@pytest.mark.skipif(not SPY.exists(), reason="needs local curated data (itrade ingest)")
def test_real_spy_buy_and_hold_matches_vendor_total_return():
    """Real data 2006–2016: within 0.1%/yr of Yahoo's adj_close total return."""
    from itrade.data.store import Store

    bars = Store().read_bars("SPY").sort_values("date")
    period = bars[(bars["date"] >= "2006-01-03") & (bars["date"] <= "2016-12-30")].reset_index()
    eq = buy_and_hold(market_frame(bars), "2006-01-03", "2016-12-30", D("10000"))
    ours = annualised(eq["equity"], eq["date"])
    first = period.iloc[0]
    vendor_curve = period["adj_close"] / first["adj_close"] * (first["close"] / first["open"])
    vendor = annualised(vendor_curve, period["date"])
    assert abs(ours - vendor) < 0.001, f"ours {ours:.4%} vs vendor {vendor:.4%}"
