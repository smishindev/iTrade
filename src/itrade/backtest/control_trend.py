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


def _decisions(sessions: pd.DatetimeIndex, start, end) -> list[pd.Timestamp]:
    days = sessions[(sessions >= pd.Timestamp(start)) & (sessions <= pd.Timestamp(end))]
    # the engine takes no decision on the period's last session (H1 §6.6)
    return [d for d in days[:-1] if is_month_end(d, sessions)]


def _eligible(row, p: TrendParams) -> bool:
    return (
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


def _persistent_scores(
    rng: np.random.Generator,
    tickers: list[str],
    decisions: list[pd.Timestamp],
    redraw: float,
) -> dict[pd.Timestamp, dict[str, float]]:
    """A random score per ETF that survives from one decision to the next and is re-drawn with
    probability `redraw` (1.0 = a fresh random ranking every month)."""
    scores: dict[str, float] = {}
    out = {}
    for d in decisions:
        for t in tickers:  # fixed order: the draw depends only on the seed
            if t not in scores or rng.random() < redraw:
                scores[t] = rng.uniform(1e-6, 1.0)
        out[d] = dict(scores)
    return out


def _kept_share(targets: list[list[str]]) -> float:
    """Mean share of last month's target still in this month's target (rotation persistence)."""
    shares = [
        len(set(a) & set(b)) / len(a) for a, b in zip(targets, targets[1:], strict=False) if a
    ]
    return float(np.mean(shares)) if shares else float("nan")


def calibrate_redraw(
    prepared: Mapping[str, pd.DataFrame],
    sessions: pd.DatetimeIndex,
    start,
    end,
    p: TrendParams,
    groups: Mapping[str, str],
    seed: int = 0,
    sims: int = 20,
) -> float:
    """Re-draw probability that gives the random ranking the strategy's own persistence (review
    of H2, blocker: a control that reshuffles every month rotates about twice as often as
    momentum and is less invested, which inflates the strategy's percentile with no edge).
    Only rankings are simulated (no fills): eligibility, one-per-group and top N as the rules."""
    rules = p.make_rules(sessions, groups)
    decisions = _decisions(sessions, start, end)
    tickers = sorted(prepared)
    eligible = {
        d: {t for t in tickers if d in prepared[t].index and _eligible(prepared[t].loc[d], p)}
        for d in decisions
    }

    def targets(score_of) -> list[list[str]]:
        out = []
        for d in decisions:
            ranked = sorted(eligible[d], key=lambda t: (-score_of(d, t), t))
            target: list[str] = []
            for t in ranked:
                if rules._fits(t, target):
                    target.append(t)
                    if len(target) == p.top_n:
                        break
            out.append(target)
        return out

    strategy = _kept_share(targets(lambda d, t: float(prepared[t].loc[d, "mom"])))
    if np.isnan(strategy):
        return 1.0
    rng = np.random.default_rng(seed)
    best, best_gap = 1.0, float("inf")
    for redraw in np.round(np.arange(0.0, 1.0001, 0.05), 2):
        kept = []
        for _ in range(sims):
            sc = _persistent_scores(rng, tickers, decisions, float(redraw))
            kept.append(_kept_share(targets(lambda d, t, sc=sc: sc[d][t])))
        gap = abs(float(np.nanmean(kept)) - strategy)
        if gap < best_gap:
            best, best_gap = float(redraw), gap
    return best


def rotation_builder(
    seed: int,
    prepared: Mapping[str, pd.DataFrame],
    sessions: pd.DatetimeIndex,
    start,
    end,
    p: TrendParams,
    redraw: float = 1.0,
) -> dict[str, pd.DataFrame]:
    """Every eligible ETF gets a random positive momentum at each decision; the random score
    persists between decisions and is re-drawn with probability `redraw` (from
    `calibrate_redraw`: the strategy's own turnover)."""
    rng = np.random.default_rng(seed)
    decisions = _decisions(sessions, start, end)
    scores = _persistent_scores(rng, sorted(prepared), decisions, redraw)
    out = {t: f.copy() for t, f in prepared.items()}
    for d in decisions:
        for ticker, f in out.items():
            if d in f.index and _eligible(f.loc[d], p):
                f.loc[d, "mom"] = scores[d][ticker]
    return out
