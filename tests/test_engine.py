"""Day-by-day simulation (spec §6.1): hand-checkable single-instrument scenarios."""

from __future__ import annotations

from decimal import Decimal as D

import pandas as pd
import pytest

from itrade.backtest.account import settlement_rules
from itrade.backtest.engine import InstrumentInfo, run_backtest
from itrade.backtest.risk import RiskPolicy
from itrade.backtest.variants import RunOptions
from itrade.config import load_strategy
from itrade.costs import CostConfig
from itrade.costs.model import BUY, SELL
from itrade.strategies.etf_pullback_v1 import SignalParams

PARAMS = load_strategy("etf_pullback_v1")
COSTS = CostConfig.load()
SP = SignalParams.from_strategy(PARAMS)
POLICY = RiskPolicy.from_config(PARAMS, COSTS.min_per_order_usd, COSTS.slippage_bps)
RULES = settlement_rules(PARAMS)
SESSIONS = pd.DatetimeIndex(pd.bdate_range("2024-06-03", periods=12))  # Mon 3 Jun .. Tue 18 Jun
INFO = {"AAA": InstrumentInfo("g", 2.0)}


def scenario(opens=None, lows=None, closes=None, dividends=None, exit_day=3, signal_day=0):
    """One instrument. Signal after the close of `signal_day` (close 20, ATR 0.4 -> limit 20.20,
    stop 19.20); close* rises above SMA(5) on `exit_day` -> exit at the next open."""
    n = len(SESSIONS)
    o = opens or [20.0] * n
    c = closes or [20.0] * n
    lo = lows or [min(a, b) - 0.05 for a, b in zip(o, c, strict=True)]
    hi = [max(a, b) + 0.05 for a, b in zip(o, c, strict=True)]
    market = pd.DataFrame(
        {
            "open": o,
            "high": hi,
            "low": lo,
            "close": c,
            "dividends": dividends or [0.0] * n,
            "splits": 0.0,
            "s": 1.0,
        },
        index=SESSIONS,
    )
    prepared = pd.DataFrame(
        {
            "close": 20.0,
            "close_star": 20.0,
            "m": 1.0,
            "s": 1.0,
            "sma_trend": 15.0,
            "sma_exit": [19.0 if i == exit_day else 21.0 for i in range(n)],
            "rsi": [5.0 if i == signal_day else 50.0 for i in range(n)],
            "atr_star": 0.4,
            "adv": 1e8,
            "member": True,
            "ex_next": False,
        },
        index=SESSIONS,
    )
    return {"AAA": market}, {"AAA": prepared}


NO_OPTIONS = RunOptions()


def run(market, prepared, end=None, options=NO_OPTIONS):
    return run_backtest(
        market,
        prepared,
        INFO,
        SESSIONS,
        SP,
        POLICY,
        COSTS,
        RULES,
        D("3280"),
        SESSIONS[0],
        end or SESSIONS[-1],
        options=options,
    )


def cost(qty, price, side):
    from itrade.backtest.fills import order_cost

    return order_cost(COSTS, D(qty), D(price), side, 2.0)


def test_one_round_trip_by_hand():
    opens = [20.0, 20.10, 20.0, 20.0, 20.40, 20.0, 20.0, 20.0, 20.0, 20.0, 20.0, 20.0]
    res = run(*scenario(opens=opens))
    assert len(res.trades) == 1
    t = res.trades.iloc[0]
    # sized at q = 7 (budget $8.20 = 7 x $1.00 risk + round-trip costs; see test_risk)
    assert (t.ticker, t.qty, t.limit, t.stop) == ("AAA", D(7), D("20.20"), D("19.20"))
    assert t.entry_date == SESSIONS[1] and t.entry_price == D("20.1000")  # LOO at the open
    assert t.exit_date == SESSIONS[4] and t.exit_price == D("20.4000")  # MOO after exit signal
    assert (t.exit_reason, t.sessions_held) == ("exit_sma", 4)
    expected_pnl = (
        7 * (D("20.40") - D("20.10")) - cost("7", "20.10", BUY) - cost("7", "20.40", SELL)
    )
    assert t.pnl == expected_pnl
    assert t.r == (expected_pnl / (7 * (D("20.10") - D("19.20")))).quantize(D("0.0001"))
    assert res.equity["equity"].iloc[-1] == D("3280") + expected_pnl


def test_entry_not_filled_when_open_gaps_above_limit():
    opens = [20.0, 20.25] + [20.0] * 10
    res = run(*scenario(opens=opens))
    assert res.trades.empty
    assert list(res.skipped["reason"]) == ["limit_not_reached"]


def test_stop_same_day_as_entry():
    lows = [19.9, 19.0] + [19.9] * 10
    res = run(*scenario(lows=lows, exit_day=99))
    t = res.trades.iloc[0]
    assert t.entry_date == t.exit_date == SESSIONS[1]
    assert (t.exit_reason, t.exit_price) == ("stop", D("19.20"))


def test_gap_below_stop_on_entry_day_uses_planned_risk():
    opens = [20.0, 19.0] + [20.0] * 10  # opens below the stop: buy and stop out at the open
    res = run(*scenario(opens=opens, exit_day=99))
    t = res.trades.iloc[0]
    assert (t.exit_reason, t.entry_price, t.exit_price) == ("stop_gap", D("19.0000"), D("19.0000"))
    assert t.risk == D(7) * D("1.00")  # (limit - stop) x q
    assert t.pnl == -(cost("7", "19.00", BUY) + cost("7", "19.00", SELL))


def test_dividend_credited_for_position_held_overnight():
    divs = [0.0, 0.0, 0.40] + [0.0] * 9  # ex-date on session 2; bought on session 1
    res = run(*scenario(dividends=divs))
    assert res.trades.iloc[0].dividends == (D(7) * D("0.40") * D("0.75")).quantize(D("0.0001"))


def test_open_position_closed_at_end_of_period():
    res = run(*scenario(exit_day=99), end=SESSIONS[5])
    t = res.trades.iloc[0]
    assert (t.exit_reason, t.exit_date, t.exit_price) == (
        "end_of_period",
        SESSIONS[5],
        D("20.0000"),
    )


def test_owner_absent_on_signal_day_means_no_order():
    market, prepared = scenario(signal_day=2)  # session 2 = Wednesday 5 Jun 2024
    assert SESSIONS[2].day_name() == "Wednesday"
    res = run(market, prepared, options=RunOptions(skip_weekday="Wednesday"))
    assert res.trades.empty and res.signals == 0


@pytest.mark.parametrize("_", range(2))
def test_deterministic(_):
    a, b = run(*scenario()), run(*scenario())
    assert a.trades_hash() == b.trades_hash()
