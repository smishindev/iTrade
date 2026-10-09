"""Trade review for ETF_TOM_V3: re-derive each trade from the bars, the session calendar and the
spec, independently of the strategy code (the window is recomputed here from session numbers
within the month, not from `window_flags`)."""

from __future__ import annotations

from collections.abc import Mapping
from decimal import Decimal

import pandas as pd

from itrade.backtest.fills import fill_stop
from itrade.backtest.review import Check, _bar, _rescale
from itrade.strategies.etf_pullback_v1 import floor_tick

R_STEP = Decimal("0.0001")


def day_number(d: pd.Timestamp, sessions: pd.DatetimeIndex) -> tuple[int, int]:
    """(number from the month's start: 1, 2, …; number from its end: -1, -2, …) of session d."""
    in_month = sessions[(sessions.year == d.year) & (sessions.month == d.month)]
    i = in_month.get_loc(d)
    return i + 1, i - len(in_month)


def check_tom_trade(
    trade: Mapping,
    market: Mapping[str, pd.DataFrame],
    prepared: Mapping[str, pd.DataFrame],
    sessions: pd.DatetimeIndex,
    p,
) -> list[Check]:
    ticker = trade["ticker"]
    m, f = market[ticker], prepared[ticker]
    entry, exit_ = pd.Timestamp(trade["entry_date"]), pd.Timestamp(trade["exit_date"])
    i_entry, i_exit = sessions.get_loc(entry), sessions.get_loc(exit_)
    t = sessions[i_entry - 1]
    _, from_end = day_number(entry, sessions)
    checks = [
        Check("ticker of the variant", ticker in p.tickers),
        Check("member on t", bool(f.loc[t, "member"])),
        Check(f"entry on day {p.entry_day}", from_end == p.entry_day, f"day {from_end}"),
    ]
    row = f.loc[t]
    atr = row["atr_star"] / row["m"]
    limit = floor_tick((row["close"] + p.limit_atr * atr) * row["s"], p.tick_size)
    stop = floor_tick((row["close"] - p.stop_atr * atr) * row["s"], p.tick_size)
    eb = _bar(m, entry)
    checks += [
        Check("limit", _rescale(limit, m, t, exit_) == trade["limit"], f"{trade['limit']}"),
        Check("stop", _rescale(stop, m, t, exit_) == trade["stop"], f"{trade['stop']}"),
        Check("LOO: open <= limit", eb.open <= limit, f"open {eb.open}"),
        Check(
            "entry at the open",
            trade["entry_price"] == _rescale(eb.open, m, entry, exit_),
            f"{trade['entry_price']}",
        ),
    ]
    # scheduled exit: the open after the close of new-month session +exit_day
    nxt = sessions[i_entry - p.entry_day]  # entry day -k + k sessions = first of the new month
    scheduled = sessions[sessions.get_loc(nxt) + p.exit_day]
    early = [
        f"stop touched {sessions[i].date()}"
        for i in range(i_entry, i_exit)
        if fill_stop(_rescale(stop, m, t, sessions[i]), _bar(m, sessions[i])) is not None
    ]
    checks.append(Check("nothing missed before the exit", not early, "; ".join(early)))
    reason, xp, xb = str(trade["exit_reason"]), trade["exit_price"], _bar(m, exit_)
    stop_x = _rescale(stop, m, t, exit_)
    if reason == "tom_exit":
        ok = exit_ == scheduled and xp == xb.open
        detail = f"scheduled {scheduled.date()}, open {xb.open}"
    elif reason == "stop_gap":
        ok = exit_ <= scheduled and xb.open <= stop_x and xp == xb.open
        detail = f"open {xb.open} <= stop {stop_x}"
    elif reason == "stop":
        ok = exit_ < scheduled and xb.open > stop_x >= xb.low and xp == stop_x
        detail = f"low {xb.low}, stop {stop_x}"
    elif reason == "end_of_period":
        ok, detail = exit_ <= scheduled and xp == xb.close, f"close {xb.close}"
    else:
        ok, detail = False, f"unknown reason {reason!r}"
    checks.append(Check(f"exit: {reason}", ok, detail))
    pnl = (
        trade["qty"] * (xp - trade["entry_price"])
        - trade["entry_costs"]
        - trade["exit_costs"]
        + trade["dividends"]
    )
    planned = trade["qty"] * (trade["limit"] - trade["stop"])
    r = (pnl / planned).quantize(R_STEP) if planned > 0 else Decimal(0)
    checks += [
        Check("P&L = qty x move - costs + dividends", pnl == trade["pnl"], f"{pnl}"),
        Check("R = P&L / planned risk", r == trade["r"], f"{r}"),
    ]
    return checks
