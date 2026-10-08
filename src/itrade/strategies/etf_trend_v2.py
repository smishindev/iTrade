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
    risk_unit_atr: float  # rotation's R unit (3 ATR for every variant, review B3)
    fallback_unbuyable: bool  # rotation: an unbuyable target -> next by rank
    weight: Decimal  # rotation: equal share of equity per position
    whole_shares: bool
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
            risk_unit_atr=float(rot["risk_unit_atr"]),
            fallback_unbuyable=bool(rot["fallback_unbuyable"]),
            weight=Decimal(str(rot["weight"])),
            whole_shares=params["risk"]["share_granularity"] == "whole",
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
    risk_stop: Decimal | None = None  # level that defines 1R when it is not the stop


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


def risk_stop(row, p: TrendParams) -> Decimal:
    """Rotation's 1R level: C - risk_unit_atr x ATR/M in traded prices, whatever the variant's
    stop (review B3: weight sizing does not depend on the stop, so R must not either)."""
    atr = row["atr_star"] / row["m"]
    return floor_tick((row["close"] - p.risk_unit_atr * atr) * row["s"], p.tick_size)


def _candidate(d, ticker, row, score, p, retry=False) -> TrendCandidate | SkippedSignal:
    limit, stop = levels(row, p)
    unit = risk_stop(row, p) if p.rules == ROTATION else None
    if not limit > stop or (unit is not None and not limit > unit):
        return SkippedSignal(d, ticker, "invalid_levels")
    close_raw = Decimal(repr(float(row["close"] * row["s"])))
    return TrendCandidate(d, ticker, score, float(row["adv"]), close_raw, limit, stop, retry, unit)


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
    ranked: list[str] = field(default_factory=list)  # this month's eligible, best first
    equity: Decimal | None = None  # E at the decision close (engine `on_close`)
    fresh: set[str] = field(default_factory=set)  # fallbacks not yet offered (new signals)
    exiting: set[str] = field(default_factory=set)  # sold rotation_out at the next open

    def eligible(self, row) -> bool:
        if not row["member"] or not _defined(row, "mom", "atr_star", "adv"):
            return False
        if self.p.trend_filter and not (
            _defined(row, "sma_trend") and row["close_star"] > row["sma_trend"]
        ):
            return False
        return bool(row["mom"] > 0)

    def on_close(self, d: pd.Timestamp, equity: Decimal) -> None:
        self.equity = equity

    def buyable(self, row) -> bool:
        """§2 p. 3a: with whole shares, one share at the limit must fit the weight x E place."""
        if not (self.p.fallback_unbuyable and self.p.whole_shares) or self.equity is None:
            return True
        limit, _ = levels(row, self.p)
        return limit <= self.p.weight * self.equity

    def rank(self, d: pd.Timestamp, prepared: Mapping) -> list[str]:
        """§2 p. 1–3: eligible, by momentum desc → ADV desc → ticker; top N, one per group.
        Also keeps the whole ranking for the fallback of an unbuyable target (§2 p. 3a)."""
        rows = []
        for ticker, frame in prepared.items():
            if d in frame.index and self.eligible(frame.loc[d]) and self.buyable(frame.loc[d]):
                r = frame.loc[d]
                rows.append((-float(r["mom"]), -float(r["adv"]), ticker))
        self.ranked = [ticker for _, _, ticker in sorted(rows)]
        target: list[str] = []
        for ticker in self.ranked:
            if self._fits(ticker, target):
                target.append(ticker)
                if len(target) == self.p.top_n:
                    break
        return target

    def _fits(self, ticker: str, target: list[str]) -> bool:
        group = self.groups.get(ticker, "none")
        return not (self.p.one_per_group and any(self.groups.get(t) == group for t in target))

    def _fallback(self, dropped: str) -> None:
        """§2 p. 3a: a target ETF one share of which exceeds its place (`size_zero`) is replaced
        by the next eligible ETF by rank that fits the group rule; it is offered from the next
        close on, as a new signal."""
        self.target = [t for t in self.target if t != dropped]
        for ticker in self.ranked:
            if ticker in self.target or ticker in self.done or ticker in self.exiting:
                continue
            if ticker == dropped:
                continue
            if self._fits(ticker, self.target):
                self.target.append(ticker)
                self.fresh.add(ticker)
                return

    def exit_decisions(self, d, positions, prepared, sessions) -> dict[str, str]:
        if not is_month_end(d, self.sessions):
            return {}
        self.target, self.fresh = self.rank(d, prepared), set()
        # kept positions already fill their place this month: a later stop leaves it empty
        self.done = {t for t in positions if t in self.target}
        self.exiting = {t for t in positions if t not in self.target}
        return {t: "rotation_out" for t in positions if t in self.exiting}  # deterministic order

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
            retry = not decision_day and ticker not in self.fresh
            self.fresh.discard(ticker)
            c = _candidate(d, ticker, row, float(row["mom"]), self.p, retry=retry)
            (candidates if isinstance(c, TrendCandidate) else skipped).append(c)
        return candidates, skipped

    def on_entry(self, ticker: str, d: pd.Timestamp) -> None:
        self.done.add(ticker)  # a stop-out leaves the place empty until the next decision

    def on_skip(self, ticker: str, d: pd.Timestamp, reason: str) -> None:
        if reason not in RETRY_REASONS:
            self.done.add(ticker)
        if reason == "size_zero" and self.p.fallback_unbuyable and ticker in self.target:
            self._fallback(ticker)

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

    def on_close(self, d: pd.Timestamp, equity: Decimal) -> None:
        pass
