"""Random control for ETF_TREND_V2 (spec §6). Same engine, filters, sizing, stops and exits; only
the choice is random.

- Rotation (A): at every month-end decision inside the period, each ETF eligible under §2 p. 1 gets
  a random positive momentum, so the ranking — not the trend filter, the group rule or the exits —
  is replaced by chance.
- Breakout (B): like H1 §10 — (session, member) pairs that pass every entry filter except the
  breakout itself; per calendar year as many are drawn as the strategy has breakouts. A picked pair
  gets HIGH_n = C* − u·ATR* (a breakout of random strength u in (0, 1]); every other pair gets
  HIGH_n = +inf (no breakout). LOW_n, and therefore the exits, are untouched.
"""

from __future__ import annotations

from collections.abc import Mapping

import numpy as np
import pandas as pd

from itrade.backtest.control import random_picks
from itrade.strategies.etf_trend_v2 import TrendParams, is_month_end


def _in_period(f: pd.DataFrame, start, end) -> pd.Series:
    return (f.index >= pd.Timestamp(start)) & (f.index <= pd.Timestamp(end))


def _trend_ok(f: pd.DataFrame, p: TrendParams) -> pd.Series:
    if not p.trend_filter:
        return pd.Series(True, index=f.index)
    return f["sma_trend"].notna() & (f["close_star"] > f["sma_trend"])


# --- B: breakout ------------------------------------------------------------------------------


def breakout_eligible(
    prepared: Mapping[str, pd.DataFrame], start, end, p: TrendParams
) -> pd.DataFrame:
    """(date, ticker, breakout) of pairs passing every §3 entry filter except the breakout."""
    frames = []
    for ticker, f in prepared.items():
        ok = (
            f["member"]
            & f["high_n"].notna()
            & f["atr_star"].notna()
            & f["adv"].notna()
            & _trend_ok(f, p)
            & _in_period(f, start, end)
        )
        sel = f.loc[ok]
        frames.append(
            pd.DataFrame(
                {
                    "date": sel.index,
                    "ticker": ticker,
                    "breakout": (sel["close_star"] > sel["high_n"]).to_numpy(),
                }
            )
        )
    out = pd.concat(frames, ignore_index=True)
    return out.sort_values(["date", "ticker"], ignore_index=True)


def breakout_counts_per_year(eligible: pd.DataFrame) -> pd.Series:
    signals = eligible[eligible["breakout"]]
    return signals.groupby(signals["date"].dt.year).size()


def breakout_builder(
    seed: int,
    prepared: Mapping[str, pd.DataFrame],
    eligible: pd.DataFrame,
    counts: pd.Series,
) -> dict[str, pd.DataFrame]:
    picks = random_picks(eligible, counts, seed)  # its `rsi` column in [0, 10] -> strength
    out = {}
    for ticker, f in prepared.items():
        pk = picks[picks["ticker"] == ticker].set_index("date")
        high = pd.Series(np.inf, index=f.index)
        u = 1.0 - pk["rsi"].to_numpy() / 10.0  # in (0, 1]
        high.loc[pk.index] = (
            f.loc[pk.index, "close_star"] - u * f.loc[pk.index, "atr_star"]
        ).to_numpy()
        out[ticker] = f.assign(high_n=high)
    return out


# --- A: rotation ------------------------------------------------------------------------------


def rotation_builder(
    seed: int,
    prepared: Mapping[str, pd.DataFrame],
    sessions: pd.DatetimeIndex,
    start,
    end,
    p: TrendParams,
) -> dict[str, pd.DataFrame]:
    rng = np.random.default_rng(seed)
    days = sessions[(sessions >= pd.Timestamp(start)) & (sessions <= pd.Timestamp(end))]
    # the engine takes no decision on the period's last session (H1 §6.6)
    decisions = [d for d in days[:-1] if is_month_end(d, sessions)]
    out = {t: f.copy() for t, f in prepared.items()}
    for d in decisions:
        for ticker in sorted(out):  # fixed order: the draw depends only on the seed
            f = out[ticker]
            if d not in f.index:
                continue
            row = f.loc[d]
            eligible = (
                bool(row["member"])
                and not np.isnan(row["mom"])
                and not np.isnan(row["atr_star"])
                and not np.isnan(row["adv"])
                and row["mom"] > 0
                and (
                    not p.trend_filter
                    or (not np.isnan(row["sma_trend"]) and row["close_star"] > row["sma_trend"])
                )
            )
            if eligible:
                f.loc[d, "mom"] = rng.uniform(1e-6, 1.0)
    return out
