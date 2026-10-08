"""ETF_TOM_V3 (hypothesis H3) — the turn-of-the-month window in index ETFs.
Specification: docs/STRATEGY_ETF_TOM_V3.md. Data, fills, costs and cash are those of H1 (spec §1).

The window is a calendar rule: `prepare_tom_instrument` marks, from the XNYS session calendar (known
in advance), the sessions after whose close the entry (`enter_next`) and the exit (`exit_next`) are
decided. The random control replaces exactly these two flags (backtest/control_tom.py).
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from decimal import Decimal

import numpy as np
import pandas as pd

from itrade.backtest.corporate_actions import split_scale
from itrade.strategies.etf_pullback_v1 import SkippedSignal, floor_tick
from itrade.strategies.indicators import atr_wilder, average_dollar_volume, dividend_neutral


@dataclass(frozen=True)
class TomParams:
    rules: str
    price_series: str
    atr_period: int
    liquidity_window: int
    tick_size: Decimal
    tickers: tuple[str, ...]
    entry_day: int  # -1: enter at the open of the month's last session; -2: one session earlier
    exit_day: int  # exit at the open after the close of new-month session +exit_day
    limit_atr: float
    stop_atr: float
    weight: Decimal  # of equity, split equally across the tickers

    @classmethod
    def from_strategy(cls, params: dict) -> TomParams:
        ind, tom = params["indicators"], params["tom"]
        if int(tom["entry_day"]) >= 0 or int(tom["exit_day"]) < 1:
            raise ValueError("entry_day must be negative and exit_day >= 1")
        return cls(
            rules=params["strategy"].get("rules", "tom"),
            price_series=ind["price_series"],
            atr_period=int(ind["atr_period"]),
            liquidity_window=int(ind["liquidity_window"]),
            tick_size=Decimal(str(params["risk"]["tick_size"])),
            tickers=tuple(tom["tickers"]),
            entry_day=int(tom["entry_day"]),
            exit_day=int(tom["exit_day"]),
            limit_atr=float(tom["limit_atr"]),
            stop_atr=float(tom["stop_atr"]),
            weight=Decimal(str(tom["weight"])),
        )

    @property
    def hold_sessions(self) -> int:
        """Sessions from the entry open to the exit open: -entry_day + exit_day (base: 4)."""
        return -self.entry_day + self.exit_day

    def make_rules(self, sessions: pd.DatetimeIndex, groups: Mapping[str, str]) -> TomRules:
        return TomRules(self)


def window_flags(sessions: pd.DatetimeIndex, p: TomParams) -> tuple[pd.Series, pd.Series]:
    """(enter_next, exit_next) by session: the strategy's turn-of-the-month decisions (spec §2).
    Month ends come from the session calendar, which the exchange publishes in advance."""
    n = len(sessions)
    months = sessions.to_period("M")
    is_last = np.append(months[1:] != months[:-1], True)
    is_first = np.insert(months[1:] != months[:-1], 0, True)
    enter = np.zeros(n, dtype=bool)
    leave = np.zeros(n, dtype=bool)
    k = -p.entry_day
    for last in np.flatnonzero(is_last[:-1]):  # the calendar's own last session has no next month
        entry = last - k + 1
        if entry - 1 >= 0:
            enter[entry - 1] = True
        exit_decision = last + p.exit_day  # first of the new month is last + 1 -> day +exit_day
        if exit_decision < n and is_first[last + 1]:
            leave[exit_decision] = True
    return pd.Series(enter, index=sessions), pd.Series(leave, index=sessions)


def prepare_tom_instrument(
    bars: pd.DataFrame, member: pd.Series, sessions: pd.DatetimeIndex, p: TomParams
) -> pd.DataFrame:
    """Per-date columns for one instrument; causal except S(t) (H1 §1) and the calendar flags."""
    df = bars.sort_values("date").reset_index(drop=True)
    date = pd.to_datetime(df["date"])
    if p.price_series == "dividend_neutral":
        star = dividend_neutral(df)
    elif p.price_series == "close":
        star = df[["open", "high", "low", "close"]].assign(m=1.0)
    else:
        raise ValueError(f"unknown price_series {p.price_series!r}")
    enter, leave = window_flags(sessions, p)
    out = pd.DataFrame(
        {
            "date": date,
            "close": df["close"],
            "close_star": star["close"],
            "m": star["m"],
            "s": split_scale(df),
            "atr_star": atr_wilder(star["high"], star["low"], star["close"], p.atr_period),
            "adv": average_dollar_volume(df["close"], df["volume"], p.liquidity_window),
            "member": date.map(member).fillna(False).astype(bool).to_numpy(),
            "enter_next": date.map(enter).fillna(False).astype(bool).to_numpy(),
            "exit_next": date.map(leave).fillna(False).astype(bool).to_numpy(),
        }
    )
    return out.set_index("date")


@dataclass(frozen=True)
class TomCandidate:
    date: pd.Timestamp
    ticker: str
    adv: float
    close_raw: Decimal
    limit: Decimal
    stop: Decimal


def levels(row, p: TomParams) -> tuple[Decimal, Decimal]:
    atr = row["atr_star"] / row["m"]
    return (
        floor_tick((row["close"] + p.limit_atr * atr) * row["s"], p.tick_size),
        floor_tick((row["close"] - p.stop_atr * atr) * row["s"], p.tick_size),
    )


@dataclass
class TomRules:
    p: TomParams

    def exit_decisions(self, d, positions, prepared, sessions) -> dict[str, str]:
        out = {}
        for ticker in positions:
            frame = prepared[ticker]
            if d in frame.index and bool(frame.loc[d]["exit_next"]):
                out[ticker] = "tom_exit"
        return out

    def entry_decisions(self, d, prepared, held):
        candidates, skipped = [], []
        for ticker in self.p.tickers:  # fixed order (no ranking)
            frame = prepared[ticker]
            if d not in frame.index:
                continue
            row = frame.loc[d]
            if not bool(row["enter_next"]) or not row["member"]:
                continue
            if np.isnan(row["atr_star"]) or np.isnan(row["adv"]):
                continue
            if ticker in held:
                skipped.append(SkippedSignal(d, ticker, "already_held"))
                continue
            limit, stop = levels(row, self.p)
            if not limit > stop:
                skipped.append(SkippedSignal(d, ticker, "invalid_levels"))
                continue
            close_raw = Decimal(repr(float(row["close"] * row["s"])))
            candidates.append(TomCandidate(d, ticker, float(row["adv"]), close_raw, limit, stop))
        return candidates, skipped

    def on_entry(self, ticker: str, d: pd.Timestamp) -> None:
        pass

    def on_skip(self, ticker: str, d: pd.Timestamp, reason: str) -> None:
        pass

    def is_new_signal(self, candidate) -> bool:
        return True

    def on_close(self, d: pd.Timestamp, equity: Decimal) -> None:
        pass
