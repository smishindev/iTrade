"""Trade review for ETF_TREND_V2 (P1.H2.09): re-derive each trade from the bars and the spec,
independently of the strategy code. Momentum, channels and SMA200 are recomputed here from the
dividend-neutral close C*; the rotation target is re-ranked from scratch at every decision.
A failed check is a simulator or spec bug to fix and test.
"""

from __future__ import annotations

from collections.abc import Mapping
from decimal import Decimal

import numpy as np
import pandas as pd

from itrade.backtest.fills import fill_stop
from itrade.backtest.review import Check, _bar, _rescale
from itrade.strategies.etf_pullback_v1 import floor_tick

R_STEP = Decimal("0.0001")


def _cs(prepared: pd.DataFrame) -> pd.Series:
    return prepared["close_star"]


def _pos(frame: pd.DataFrame, d: pd.Timestamp) -> int:
    return frame.index.get_loc(d)


def mom_at(f: pd.DataFrame, d: pd.Timestamp, n: int) -> float:
    i = _pos(f, d)
    return float("nan") if i < n else float(_cs(f).iloc[i] / _cs(f).iloc[i - n] - 1)


def high_at(f: pd.DataFrame, d: pd.Timestamp, n: int) -> float:
    i = _pos(f, d)
    return float("nan") if i < n else float(_cs(f).iloc[i - n : i].max())


def low_at(f: pd.DataFrame, d: pd.Timestamp, n: int) -> float:
    i = _pos(f, d)
    return float("nan") if i < n else float(_cs(f).iloc[i - n : i].min())


def above_trend(f: pd.DataFrame, d: pd.Timestamp, n: int) -> bool:
    i = _pos(f, d)
    return i >= n - 1 and float(_cs(f).iloc[i]) > float(_cs(f).iloc[i - n + 1 : i + 1].mean())


def month_end(d: pd.Timestamp, sessions: pd.DatetimeIndex) -> bool:
    i = sessions.get_loc(d)
    return i + 1 >= len(sessions) or sessions[i + 1].month != d.month


def rotation_eligible(f: pd.DataFrame, d: pd.Timestamp, p) -> bool:
    if d not in f.index or not bool(f.loc[d, "member"]):
        return False
    if np.isnan(f.loc[d, "atr_star"]) or np.isnan(f.loc[d, "adv"]):
        return False
    m = mom_at(f, d, p.momentum_sessions)
    return not np.isnan(m) and m > 0 and (not p.trend_filter or above_trend(f, d, p.trend_sma))


def rotation_ranking(
    d: pd.Timestamp,
    prepared: Mapping[str, pd.DataFrame],
    p,
    groups: Mapping[str, str],
    equity: Decimal | None = None,
) -> list[str]:
    """Every eligible (and, with whole shares, buyable at E_d) ETF, best first, keeping only the
    best of each group (one per group)."""

    def buyable(f) -> bool:
        if equity is None or not (p.fallback_unbuyable and p.whole_shares):
            return True
        return _levels(f.loc[d], p)[0] <= p.weight * equity

    ranked = sorted(
        (-mom_at(f, d, p.momentum_sessions), -float(f.loc[d, "adv"]), t)
        for t, f in prepared.items()
        if rotation_eligible(f, d, p) and buyable(f)
    )
    out, used = [], set()
    for _, _, t in ranked:
        if p.one_per_group and groups[t] in used:
            continue
        out.append(t)
        used.add(groups[t])
    return out


def rotation_target(
    d: pd.Timestamp,
    prepared: Mapping[str, pd.DataFrame],
    p,
    groups: Mapping[str, str],
    equity: Decimal | None = None,
) -> list[str]:
    return rotation_ranking(d, prepared, p, groups, equity)[: p.top_n]


def _levels(row, p) -> tuple[Decimal, Decimal]:
    atr = row["atr_star"] / row["m"]
    return (
        floor_tick((row["close"] + p.limit_atr * atr) * row["s"], p.tick_size),
        floor_tick((row["close"] - p.stop_atr * atr) * row["s"], p.tick_size),
    )


def check_trend_trade(
    trade: Mapping,
    market: Mapping[str, pd.DataFrame],
    prepared: Mapping[str, pd.DataFrame],
    sessions: pd.DatetimeIndex,
    p,
    groups: Mapping[str, str],
    equity: Mapping[pd.Timestamp, Decimal] | None = None,
) -> list[Check]:
    """`equity` (E by date, from the run) lets the rotation check leave out ETFs one share of
    which does not fit the place (spec §2 p. 3a); without it, the target is unfiltered."""
    ticker = trade["ticker"]
    m, f = market[ticker], prepared[ticker]
    entry, exit_ = pd.Timestamp(trade["entry_date"]), pd.Timestamp(trade["exit_date"])
    reason = str(trade["exit_reason"])
    i_entry, i_exit = sessions.get_loc(entry), sessions.get_loc(exit_)
    t = sessions[i_entry - 1]  # decision session
    checks = [Check("member on t", bool(f.loc[t, "member"]), f"t = {t.date()}")]

    if p.rules == "breakout":
        hi = high_at(f, t, p.entry_sessions)
        checks.append(
            Check("breakout: C* > HIGH_n", float(_cs(f)[t]) > hi, f"{_cs(f)[t]:.4f} > {hi:.4f}")
        )
        if p.trend_filter:
            checks.append(Check("trend: C* > SMA200", above_trend(f, t, p.trend_sma)))
    else:
        decision = max(d for d in sessions[:i_entry] if month_end(d, sessions))
        e_d = equity.get(decision) if equity is not None else None
        allowed = rotation_target(decision, prepared, p, groups, e_d)
        checks += [
            Check("in the month's target", ticker in allowed, f"{decision.date()}: {allowed}"),
            Check("still eligible on t", rotation_eligible(f, t, p)),
        ]

    limit, stop = _levels(f.loc[t], p)
    limit_e, stop_x = _rescale(limit, m, t, entry), _rescale(stop, m, t, exit_)
    checks += [
        # the trade row is in exit-day prices: a split while holding rescales limit and entry
        Check("limit", _rescale(limit, m, t, exit_) == trade["limit"], f"{trade['limit']}"),
        Check("stop", stop_x == trade["stop"], f"{stop_x} vs {trade['stop']}"),
    ]
    eb = _bar(m, entry)
    checks += [
        Check("LOO: open <= limit", eb.open <= limit_e, f"open {eb.open}"),
        Check(
            "entry at the open",
            trade["entry_price"] == _rescale(eb.open, m, entry, exit_),
            f"{trade['entry_price']}",
        ),
    ]

    def exit_due(d: pd.Timestamp) -> str | None:
        """The rule's exit decision after the close of d (independently derived)."""
        if p.rules == "breakout":
            lo = low_at(f, d, p.exit_sessions)
            return "channel_exit" if not np.isnan(lo) and float(_cs(f)[d]) < lo else None
        e_d = equity.get(d) if equity is not None else None
        if month_end(d, sessions) and ticker not in rotation_target(d, prepared, p, groups, e_d):
            return "rotation_out"
        return None

    early = []
    for i in range(i_entry, i_exit):
        d = sessions[i]
        if fill_stop(_rescale(stop, m, t, d), _bar(m, d)) is not None:
            early.append(f"stop touched {d.date()}")
        if i < i_exit - 1 and exit_due(d):
            early.append(f"{exit_due(d)} {d.date()}")
    checks.append(Check("nothing missed before the exit", not early, "; ".join(early)))

    xb, prev = _bar(m, exit_), sessions[i_exit - 1]
    xp = trade["exit_price"]
    due = exit_due(prev) if prev >= entry else None
    if reason in ("channel_exit", "rotation_out"):
        ok, detail = due == reason and xp == xb.open, f"{prev.date()}: {due}; open {xb.open}"
    elif reason == "stop_gap":
        ok, detail = xb.open <= stop_x and xp == xb.open, f"open {xb.open} <= stop {stop_x}"
    elif reason == "stop":
        ok = due is None and xb.open > stop_x >= xb.low and xp == stop_x
        detail = f"open {xb.open}, low {xb.low}, stop {stop_x}"
    elif reason == "end_of_period":
        ok, detail = xp == xb.close, f"close {xb.close}"
    else:
        ok, detail = False, f"unknown reason {reason!r}"
    checks.append(Check(f"exit: {reason}", ok, detail))

    pnl = (
        trade["qty"] * (xp - trade["entry_price"])
        - trade["entry_costs"]
        - trade["exit_costs"]
        + trade["dividends"]
    )
    if p.rules == "rotation":  # R unit: risk_unit_atr below the decision close (review B3)
        atr = f.loc[t, "atr_star"] / f.loc[t, "m"]
        unit = floor_tick((f.loc[t, "close"] - p.risk_unit_atr * atr) * f.loc[t, "s"], p.tick_size)
        unit = _rescale(unit, m, t, exit_)
    else:
        unit = trade["stop"]
    planned = trade["qty"] * (trade["limit"] - unit)
    r = (pnl / planned).quantize(R_STEP) if planned > 0 else Decimal(0)
    checks += [
        Check("P&L = qty x move - costs + dividends", pnl == trade["pnl"], f"{pnl}"),
        Check("R = P&L / planned risk", r == trade["r"], f"{r}"),
    ]
    return checks
