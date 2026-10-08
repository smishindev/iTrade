"""ETF_TREND_V2 rules A and B on hand-built scenarios (P1.H2.05–P1.H2.07). Synthetic data only:
H2 is not run on real data before its pre-registration."""

from __future__ import annotations

from decimal import Decimal as D

import numpy as np
import pandas as pd

from itrade.backtest.account import settlement_rules
from itrade.backtest.engine import InstrumentInfo, run_backtest
from itrade.backtest.risk import RiskPolicy
from itrade.backtest.variants import apply_variant
from itrade.config import load_strategy
from itrade.costs import CostConfig
from itrade.strategies.etf_trend_v2 import TrendParams, is_month_end

PARAMS = load_strategy("etf_trend_v2")
COSTS = CostConfig.load()
SESSIONS = pd.DatetimeIndex(pd.bdate_range("2024-05-27", "2024-07-12"))
MAY_END, JUNE_END = pd.Timestamp("2024-05-31"), pd.Timestamp("2024-06-28")


def setup(variant: str):
    params, options, _ = apply_variant(PARAMS, variant)
    p = TrendParams.from_strategy(params)
    policy = RiskPolicy.for_trend(
        params, params["strategy"]["rules"], COSTS.min_per_order_usd, COSTS.slippage_bps
    )
    return params, p, policy, options


N = len(SESSIONS)


def frames(n=N, **cols):
    """Prepared rows: close 20, ATR* 0.4, everything eligible unless overridden per column."""
    base = {
        "close": 20.0, "close_star": 20.0, "m": 1.0, "s": 1.0, "sma_trend": 15.0, "atr_star": 0.4,
        "adv": 1e8, "mom": 0.1, "high_n": 25.0, "low_n": 15.0, "member": True,
    }  # fmt: skip
    data = {k: cols.get(k, v) for k, v in base.items()}
    data = {k: (v if isinstance(v, list) else [v] * n) for k, v in data.items()}
    return pd.DataFrame(data, index=SESSIONS)


def market(opens=None, lows=None, n=N):
    o = opens or [20.0] * n
    lo = lows or [19.9] * n
    return pd.DataFrame(
        {
            "open": o,
            "high": 20.1,
            "low": lo,
            "close": 20.0,
            "dividends": 0.0,
            "splits": 0.0,
            "s": 1.0,
        },
        index=SESSIONS,
    )


def run(mkt, prep, info, variant):
    params, p, policy, options = setup(variant)
    return run_backtest(
        mkt, prep, info, SESSIONS, p, policy, COSTS, settlement_rules(params), D("3280"),
        SESSIONS[0], SESSIONS[-1], options=options, r_denominator="planned",
        count_exiting_positions=False,
    )  # fmt: skip


def at(day: str) -> int:
    return SESSIONS.get_loc(pd.Timestamp(day))


def series(default, changes: dict[str, float]):
    """Per-session values: `default`, switching to each new value from its date on."""
    out = [default] * len(SESSIONS)
    for day, value in sorted(changes.items()):
        for i in range(at(day), len(SESSIONS)):
            out[i] = value
    return out


def test_month_end_calendar():
    assert is_month_end(MAY_END, SESSIONS) and is_month_end(JUNE_END, SESSIONS)
    assert not is_month_end(pd.Timestamp("2024-06-27"), SESSIONS)


def rotation_world(**overrides):
    groups = {"A": "g1", "B": "g1", "C": "g2", "D": "g3", "E": "g4"}
    mom = {
        "A": 0.30, "B": 0.20,  # same group: only A can be in the target
        "C": series(0.10, {"2024-06-28": -0.05}),  # drops out at the June decision
        "D": 0.05, "E": series(-0.10, {"2024-06-28": 0.15}),  # joins at the June decision
    }  # fmt: skip
    prep = {t: frames(mom=m) for t, m in mom.items()}
    mkt = {t: market() for t in groups}
    for t, kwargs in overrides.items():
        mkt[t] = market(**kwargs)
    info = {t: InstrumentInfo(g, 2.0) for t, g in groups.items()}
    return mkt, prep, info


def test_rotation_targets_top3_one_per_group_and_rotates_at_month_end():
    res = run(*rotation_world(), "rot_base")
    t = res.trades.set_index("ticker")
    assert set(t.index) == {"A", "C", "D", "E"}  # B never: same group as A
    june3 = pd.Timestamp("2024-06-03")
    assert (t.loc[["A", "C", "D"], "entry_date"] == june3).all()
    july1 = pd.Timestamp("2024-07-01")
    assert t.loc["C", "exit_date"] == july1 and t.loc["C", "exit_reason"] == "rotation_out"
    assert t.loc["E", "entry_date"] == july1  # bought at the same open C is sold (free cash)
    # equal weight 20%: q = floor(0.20 x 3280 / limit 20.40) = 32
    assert (t["qty"] == 32).all()
    # R on planned risk: limit 20.40, stop 18.80 (C ± ATR multiples)
    c = t.loc["C"]
    assert (c["limit"], c["stop"]) == (D("20.40"), D("18.80"))
    assert c["risk"] == 32 * (D("20.40") - D("18.80"))


def test_rotation_retries_an_unfilled_entry_until_month_end():
    opens = series(20.0, {"2024-06-03": 21.0, "2024-06-04": 20.0})  # gap above the limit once
    res = run(*rotation_world(D={"opens": opens}), "rot_base")
    d = res.trades.set_index("ticker").loc["D"]
    assert d["entry_date"] == pd.Timestamp("2024-06-04")
    assert "limit_not_reached" in set(res.skipped["reason"])
    assert res.signals == 4  # A, C, D in May + E in June; the retry is not a new signal


def test_rotation_stop_leaves_the_place_empty_until_the_next_decision():
    lows = series(19.9, {"2024-06-10": 18.0, "2024-06-11": 19.9})  # touches the 18.80 stop once
    res = run(*rotation_world(D={"lows": lows}), "rot_base")
    d_trades = res.trades[res.trades["ticker"] == "D"]
    assert list(d_trades["exit_reason"])[0] == "stop"
    assert d_trades.iloc[0]["exit_date"] == pd.Timestamp("2024-06-10")
    # re-entered only after the June decision (it is still in the target), not on 11 June
    assert list(d_trades["entry_date"])[1:] == [pd.Timestamp("2024-07-01")]


def test_rotation_without_trend_filter_variant():
    _, p, _, _ = setup("rot_no_trend")
    assert p.rules == "rotation" and not p.trend_filter
    world = rotation_world()
    world[1]["D"] = frames(mom=0.05, sma_trend=30.0)  # below its SMA200
    with_filter = run(*world, "rot_base").trades
    without = run(*world, "rot_no_trend").trades
    assert "D" not in set(with_filter["ticker"]) and "D" in set(without["ticker"])


def breakout_world(high_n, low_n, lows=None):
    prep = {"X": frames(high_n=high_n, low_n=low_n)}
    return {"X": market(lows=lows)}, prep, {"X": InstrumentInfo("g1", 2.0)}


def test_breakout_entry_risk_sizing_and_channel_exit():
    high = series(25.0, {"2024-06-05": 19.5, "2024-06-06": 25.0})  # breakout on 5 June only
    low = series(15.0, {"2024-06-12": 20.5})  # close 20 < LOW_20 from 12 June
    res = run(*breakout_world(high, low), "brk_base")
    assert len(res.trades) == 1
    t = res.trades.iloc[0]
    assert t["entry_date"] == pd.Timestamp("2024-06-06")
    assert t["exit_date"] == pd.Timestamp("2024-06-13") and t["exit_reason"] == "channel_exit"
    assert (t["limit"], t["stop"]) == (D("20.20"), D("19.20"))
    # risk budget 0.5% x 3280 = 16.40: q x 1.00 + round trip must fit, q + 1 must not
    assert t["qty"] == 15
    assert t["risk"] == 15 * (D("20.20") - D("19.20"))


def test_breakout_cost_gate_rejects_tiny_risk():
    # ATR 0.04 -> stop 0.08 below: planned risk ~ $1.3 for a 20% position, costs > 0.10R
    high = series(25.0, {"2024-06-05": 19.5, "2024-06-06": 25.0})
    world = breakout_world(high, 15.0)
    world[1]["X"] = frames(high_n=high, atr_star=0.04)
    res = run(*world, "brk_base")
    assert res.trades.empty
    assert "cost_too_high" in set(res.skipped["reason"])


def test_breakout_trend_variant_and_neighbours_parse():
    _, p, policy, _ = setup("brk_trend")
    assert p.trend_filter and p.stop_atr == 2.0 and policy.sizing == "risk"
    _, p, _, _ = setup("brk_20_10")
    assert (p.entry_sessions, p.exit_sessions) == (20, 10)
    _, _, policy, _ = setup("rot_top4")
    assert policy.max_open_positions == 4 and policy.sizing == "weight"
    assert policy.weight == D("0.2") and policy.max_positions_per_group == 1
    assert not np.isnan(frames()["mom"]).any()
