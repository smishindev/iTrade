"""ETF_TREND_V2 (hypothesis H2) — rules A (monthly rotation) and B (channel breakout).
Specification: docs/STRATEGY_ETF_TREND_V2.md. Data, the dividend-neutral series, fills, costs and
cash are those of ETF_PULLBACK_V1 (spec §1); this module only adds the signals.

`prepare_trend_instrument` columns are causal (row t uses bars <= t), except the split scale S(t)
that converts adjusted to traded prices (H1 spec §1). The rules objects are stateful per run
(the rotation remembers this month's target); build a fresh one with `TrendParams.make_rules`.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from decimal import Decimal

import numpy as np
import pandas as pd

from itrade.backtest.corporate_actions import split_scale
from itrade.strategies.etf_pullback_v1 import SkippedSignal, floor_tick
from itrade.strategies.indicators import (
    atr_wilder,
    average_dollar_volume,
    dividend_neutral,
    momentum,
    prior_high,
    prior_low,
    sma,
)

ROTATION, BREAKOUT = "rotation", "breakout"

# A target entry is retried only after these outcomes (spec §2 p. 5: limit, no settled cash);
# any other rejection leaves the place empty until the next monthly decision.
RETRY_REASONS = frozenset({"limit_not_reached", "no_bar", "settled_cash", "settled_cash_at_fill"})


@dataclass(frozen=True)
class TrendParams:
    rules: str  # "rotation" | "breakout"
    price_series: str
    trend_sma: int
    atr_period: int
    liquidity_window: int
    tick_size: Decimal
    # rotation
    momentum_sessions: int
    top_n: int
    one_per_group: bool
    retry_entries: bool
    # breakout
    entry_sessions: int
    exit_sessions: int
    # the active rule set's
    trend_filter: bool
    limit_atr: float
    stop_atr: float

    @classmethod
    def from_strategy(cls, params: dict) -> TrendParams:
        rules = params["strategy"]["rules"]
        if rules not in (ROTATION, BREAKOUT):
            raise ValueError(f"unknown rules {rules!r}")
        ind, rot, brk = params["indicators"], params["rotation"], params["breakout"]
        active = rot if rules == ROTATION else brk
        return cls(
            rules=rules,
            price_series=ind["price_series"],
            trend_sma=int(ind["trend_sma"]),
            atr_period=int(ind["atr_period"]),
            liquidity_window=int(ind["liquidity_window"]),
            tick_size=Decimal(str(params["risk"]["tick_size"])),
            momentum_sessions=int(rot["momentum_sessions"]),
            top_n=int(rot["top_n"]),
            one_per_group=bool(rot["one_per_group"]),
            retry_entries=bool(rot["retry_entries_until_month_end"]),
            entry_sessions=int(brk["entry_sessions"]),
            exit_sessions=int(brk["exit_sessions"]),
            trend_filter=bool(active["trend_filter"]),
            limit_atr=float(active["limit_atr"]),
            stop_atr=float(active["stop_atr"]),
        )

    def make_rules(self, sessions: pd.DatetimeIndex, groups: Mapping[str, str]):
        if self.rules == ROTATION:
            return RotationRules(self, sessions, dict(groups))
        return BreakoutRules(self)


@dataclass(frozen=True)
class TrendCandidate:
    date: pd.Timestamp
    ticker: str
    score: float  # rotation: momentum; breakout: breakout strength in ATR
    adv: float
    close_raw: Decimal
    limit: Decimal  # raw (traded) price, floored to the tick
    stop: Decimal
    retry: bool = False


def prepare_trend_instrument(
    bars: pd.DataFrame, member: pd.Series, sessions: pd.DatetimeIndex, p: TrendParams
) -> pd.DataFrame:
    """Per-date columns for one instrument (spec §1); `sessions` kept for symmetry with H1."""
    df = bars.sort_values("date").reset_index(drop=True)
    date = pd.to_datetime(df["date"])
    if p.price_series == "dividend_neutral":
        star = dividend_neutral(df)
    elif p.price_series == "close":
        star = df[["open", "high", "low", "close"]].assign(m=1.0)
    else:
        raise ValueError(f"unknown price_series {p.price_series!r}")
    c = star["close"]
    out = pd.DataFrame(
        {
            "date": date,
            "close": df["close"],
            "close_star": c,
            "m": star["m"],
            "s": split_scale(df),
            "sma_trend": sma(c, p.trend_sma),
            "atr_star": atr_wilder(star["high"], star["low"], c, p.atr_period),
            "adv": average_dollar_volume(df["close"], df["volume"], p.liquidity_window),
            "mom": momentum(c, p.momentum_sessions),
            "high_n": prior_high(c, p.entry_sessions),
            "low_n": prior_low(c, p.exit_sessions),
            "member": date.map(member).fillna(False).astype(bool).to_numpy(),
        }
    )
    return out.set_index("date")


def levels(row, p: TrendParams) -> tuple[Decimal, Decimal]:
    """Limit and stop in traded prices: ATR to the day's scale (/ M), x S, floored (H1 §4.1)."""
    atr = row["atr_star"] / row["m"]
    limit = floor_tick((row["close"] + p.limit_atr * atr) * row["s"], p.tick_size)
    stop = floor_tick((row["close"] - p.stop_atr * atr) * row["s"], p.tick_size)
    return limit, stop


def _defined(row, *cols: str) -> bool:
    return all(not np.isnan(row[c]) for c in cols)


def _candidate(d, ticker, row, score, p, retry=False) -> TrendCandidate | SkippedSignal:
    limit, stop = levels(row, p)
    if not limit > stop:
        return SkippedSignal(d, ticker, "invalid_levels")
    close_raw = Decimal(repr(float(row["close"] * row["s"])))
    return TrendCandidate(d, ticker, score, float(row["adv"]), close_raw, limit, stop, retry)


def is_month_end(d: pd.Timestamp, sessions: pd.DatetimeIndex) -> bool:
    """d is the last XNYS session of its month (no later session in the calendar counts too)."""
    i = sessions.get_loc(pd.Timestamp(d))
    return i + 1 >= len(sessions) or sessions[i + 1].month != pd.Timestamp(d).month


# --- rules A: monthly rotation (spec §2) -----------------------------------------------------


@dataclass
class RotationRules:
    p: TrendParams
    sessions: pd.DatetimeIndex
    groups: dict[str, str]
    target: list[str] = field(default_factory=list)
    done: set[str] = field(default_factory=set)  # entered or dropped this month

    def eligible(self, row) -> bool:
        if not row["member"] or not _defined(row, "mom", "atr_star", "adv"):
            return False
        if self.p.trend_filter and not (
            _defined(row, "sma_trend") and row["close_star"] > row["sma_trend"]
        ):
            return False
        return bool(row["mom"] > 0)

    def rank(self, d: pd.Timestamp, prepared: Mapping) -> list[str]:
        """§2 p. 1–3: eligible, by momentum desc → ADV desc → ticker; top N, one per group."""
        rows = []
        for ticker, frame in prepared.items():
            if d in frame.index and self.eligible(frame.loc[d]):
                r = frame.loc[d]
                rows.append((-float(r["mom"]), -float(r["adv"]), ticker))
        target, used = [], set()
        for _, _, ticker in sorted(rows):
            group = self.groups.get(ticker, "none")
            if self.p.one_per_group and group in used:
                continue
            target.append(ticker)
            used.add(group)
            if len(target) == self.p.top_n:
                break
        return target

    def exit_decisions(self, d, positions, prepared, sessions) -> dict[str, str]:
        if not is_month_end(d, self.sessions):
            return {}
        self.target, self.done = self.rank(d, prepared), set()
        return {t: "rotation_out" for t in positions if t not in self.target}

    def entry_decisions(self, d, prepared, held):
        candidates, skipped = [], []
        decision_day = is_month_end(d, self.sessions)
        if not decision_day and not self.p.retry_entries:
            return candidates, skipped
        for ticker in self.target:
            if ticker in held or ticker in self.done:
                continue
            frame = prepared[ticker]
            if d not in frame.index:
                continue
            row = frame.loc[d]
            if not self.eligible(row):  # a retry only while the ETF stays eligible (§2 p. 5)
                self.done.add(ticker)
                skipped.append(SkippedSignal(d, ticker, "no_longer_eligible"))
                continue
            c = _candidate(d, ticker, row, float(row["mom"]), self.p, retry=not decision_day)
            (candidates if isinstance(c, TrendCandidate) else skipped).append(c)
        return candidates, skipped

    def on_entry(self, ticker: str, d: pd.Timestamp) -> None:
        self.done.add(ticker)  # a stop-out leaves the place empty until the next decision

    def on_skip(self, ticker: str, d: pd.Timestamp, reason: str) -> None:
        if reason not in RETRY_REASONS:
            self.done.add(ticker)

    def is_new_signal(self, candidate) -> bool:
        return not candidate.retry


# --- rules B: channel breakout (spec §3) -----------------------------------------------------


@dataclass
class BreakoutRules:
    p: TrendParams

    def exit_decisions(self, d, positions, prepared, sessions) -> dict[str, str]:
        out = {}
        for ticker in positions:
            frame = prepared[ticker]
            if d not in frame.index:
                continue
            row = frame.loc[d]
            if _defined(row, "low_n") and row["close_star"] < row["low_n"]:
                out[ticker] = "channel_exit"
        return out

    def entry_decisions(self, d, prepared, held):
        candidates, skipped = [], []
        for ticker, frame in prepared.items():
            if d not in frame.index:
                continue
            row = frame.loc[d]
            if not row["member"] or not _defined(row, "high_n", "atr_star", "adv"):
                continue
            if self.p.trend_filter and not (
                _defined(row, "sma_trend") and row["close_star"] > row["sma_trend"]
            ):
                continue
            if not row["close_star"] > row["high_n"]:
                continue
            if ticker in held:
                skipped.append(SkippedSignal(d, ticker, "already_held"))
                continue
            strength = (row["close_star"] - row["high_n"]) / row["atr_star"]
            c = _candidate(d, ticker, row, float(strength), self.p)
            (candidates if isinstance(c, TrendCandidate) else skipped).append(c)
        candidates.sort(key=lambda c: (-c.score, -c.adv, c.ticker))
        return candidates, skipped

    def on_entry(self, ticker: str, d: pd.Timestamp) -> None:
        pass

    def on_skip(self, ticker: str, d: pd.Timestamp, reason: str) -> None:
        pass

    def is_new_signal(self, candidate) -> bool:
        return True
