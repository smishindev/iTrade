"""ETF_PULLBACK_V1 signals (docs/STRATEGY_ETF_PULLBACK_V1.md §4).

`prepare_instrument` computes, once per instrument, every column the rules need. All columns are
causal (row t uses bars <= t; tests prove prefix invariance), except two documented exceptions:
the split scale S(t) that converts adjusted to traded prices (§1) and the "next session is an
ex-dividend date" flag, an assumption of the spec (§4.1 #5, §12: ex-dates are announced in advance).
`entry_signals` is a pure function of the prepared rows at t.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from decimal import ROUND_FLOOR, ROUND_HALF_EVEN, Decimal

import numpy as np
import pandas as pd

from itrade.backtest.corporate_actions import ex_dividend_dates, split_scale
from itrade.strategies.indicators import (
    atr_wilder,
    average_dollar_volume,
    dividend_neutral,
    rsi_wilder,
    sma,
)


@dataclass(frozen=True)
class SignalParams:
    price_series: str
    trend_sma: int
    rsi_period: int
    atr_period: int
    exit_sma: int
    liquidity_window: int
    rsi_max: float
    limit_atr: float
    stop_atr: float
    tick_size: Decimal

    @classmethod
    def from_strategy(cls, params: dict) -> SignalParams:
        ind, entry, exit_ = params["indicators"], params["entry"], params["exit"]
        return cls(
            price_series=ind["price_series"],
            trend_sma=int(ind["trend_sma"]),
            rsi_period=int(ind["rsi_period"]),
            atr_period=int(ind["atr_period"]),
            exit_sma=int(ind["exit_sma"]),
            liquidity_window=int(ind["liquidity_window"]),
            rsi_max=float(entry["rsi_max"]),
            limit_atr=float(entry["limit_atr"]),
            stop_atr=float(exit_["stop_atr"]),
            tick_size=Decimal(str(params["risk"]["tick_size"])),
        )


@dataclass(frozen=True)
class EntryCandidate:
    date: pd.Timestamp
    ticker: str
    rsi: float
    adv: float
    close_raw: Decimal
    limit: Decimal  # raw (traded) price, floored to the tick
    stop: Decimal  # raw price, floored to the tick


@dataclass(frozen=True)
class SkippedSignal:
    date: pd.Timestamp
    ticker: str
    reason: str  # "already_held" | "ex_dividend_next" | "invalid_levels"


def prepare_instrument(
    bars: pd.DataFrame,
    member: pd.Series,
    sessions: pd.DatetimeIndex,
    p: SignalParams,
) -> pd.DataFrame:
    """Per-date columns for one instrument. `bars`: vendor columns incl. dividends/splits (after
    overrides); `member`: bool Series indexed by date; `sessions`: sorted XNYS session dates."""
    df = bars.sort_values("date").reset_index(drop=True)
    date = pd.to_datetime(df["date"])
    if p.price_series == "dividend_neutral":
        star = dividend_neutral(df)
    elif p.price_series == "close":
        star = df[["open", "high", "low", "close"]].assign(m=1.0)
    else:
        raise ValueError(f"unknown price_series {p.price_series!r}")

    nxt_idx = sessions.searchsorted(date, side="right")
    in_calendar = (date >= sessions[0]).to_numpy()  # before the calendar starts: unknown
    next_session = pd.Series(
        [
            sessions[i] if ok and i < len(sessions) else pd.NaT
            for i, ok in zip(nxt_idx, in_calendar, strict=True)
        ],
        index=df.index,
    )
    ex = set(ex_dividend_dates(df))

    out = pd.DataFrame(
        {
            "date": date,
            "close": df["close"],
            "close_star": star["close"],
            "m": star["m"],
            "s": split_scale(df),
            "sma_trend": sma(star["close"], p.trend_sma),
            "sma_exit": sma(star["close"], p.exit_sma),
            "rsi": rsi_wilder(star["close"], p.rsi_period),
            "atr_star": atr_wilder(star["high"], star["low"], star["close"], p.atr_period),
            "adv": average_dollar_volume(df["close"], df["volume"], p.liquidity_window),
            "member": date.map(member).fillna(False).astype(bool).to_numpy(),
            "ex_next": next_session.isin(ex).to_numpy(),
        }
    )
    return out.set_index("date")


NANO = Decimal("0.000000001")


def floor_tick(value: float, tick: Decimal) -> Decimal:
    """Spec §4.1: round to 1e-9 first (so 100.99999999999999 from binary floating point counts as
    101.00), then floor to the tick. The C# core must apply the same two steps."""
    exact = Decimal(repr(float(value))).quantize(NANO, rounding=ROUND_HALF_EVEN)  # numpy-safe repr
    return (exact / tick).to_integral_value(rounding=ROUND_FLOOR) * tick


def entry_levels(
    close: float, atr_star: float, m: float, s: float, p: SignalParams
) -> tuple[Decimal, Decimal]:
    """§4.1 levels: ATR back to the day's price scale (/ M), to traded price (x S), floored."""
    atr = atr_star / m
    limit = floor_tick((close + p.limit_atr * atr) * s, p.tick_size)
    stop = floor_tick((close - p.stop_atr * atr) * s, p.tick_size)
    return limit, stop


def entry_signals(
    t: pd.Timestamp,
    prepared: Mapping[str, pd.DataFrame],
    held: set[str],
    p: SignalParams,
) -> tuple[list[EntryCandidate], list[SkippedSignal]]:
    """§4.1–4.2: entry candidates after the close of t, in the pre-registered order."""
    t = pd.Timestamp(t)
    candidates: list[EntryCandidate] = []
    skipped: list[SkippedSignal] = []
    for ticker, frame in prepared.items():
        if t not in frame.index:
            continue
        row = frame.loc[t]
        if not row["member"] or np.isnan(row["sma_trend"]) or np.isnan(row["rsi"]):
            continue
        if not (row["close_star"] > row["sma_trend"] and row["rsi"] <= p.rsi_max):
            continue
        if ticker in held:
            skipped.append(SkippedSignal(t, ticker, "already_held"))
            continue
        if row["ex_next"]:
            skipped.append(SkippedSignal(t, ticker, "ex_dividend_next"))
            continue
        if np.isnan(row["atr_star"]):
            skipped.append(SkippedSignal(t, ticker, "invalid_levels"))
            continue
        limit, stop = entry_levels(row["close"], row["atr_star"], row["m"], row["s"], p)
        if stop <= 0 or limit <= stop:
            skipped.append(SkippedSignal(t, ticker, "invalid_levels"))
            continue
        candidates.append(
            EntryCandidate(
                date=t,
                ticker=ticker,
                rsi=float(row["rsi"]),
                adv=float(row["adv"]),
                close_raw=Decimal(repr(float(row["close"] * row["s"]))).quantize(
                    Decimal("0.0001"), rounding=ROUND_HALF_EVEN
                ),
                limit=limit,
                stop=stop,
            )
        )
    candidates.sort(key=lambda c: (c.rsi, -c.adv, c.ticker))
    return candidates, skipped
