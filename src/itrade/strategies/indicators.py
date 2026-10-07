"""Indicators of docs/STRATEGY_ETF_PULLBACK_V1.md §1–§2, implemented literally.

Every function takes values ordered by date and returns a Series aligned to the input, using only
values at or before each position (no look-ahead). Undefined values are NaN, never 0. Wilder RSI
and ATR use explicit loops: pandas' ewm seeds differently and would not match the spec vectors.
"""

from __future__ import annotations

import math

import numpy as np
import pandas as pd


def sma(close: pd.Series, n: int) -> pd.Series:
    """§2.1 — mean of the last n values; defined from the n-th value."""
    return close.rolling(n, min_periods=n).mean()


def rsi_wilder(close: pd.Series, n: int) -> pd.Series:
    """§2.2 — Wilder RSI; seed = simple means of the first n gains/losses (needs n + 1 closes)."""
    c = close.to_numpy(dtype=float)
    out = np.full(len(c), np.nan)
    if len(c) <= n:
        return pd.Series(out, index=close.index)
    delta = np.diff(c)  # delta[i - 1] = C_i - C_{i-1}
    gains = np.maximum(delta, 0.0)
    losses = np.maximum(-delta, 0.0)
    ag = gains[:n].mean()
    al = losses[:n].mean()
    out[n] = _rsi(ag, al)
    for i in range(n + 1, len(c)):
        ag = (ag * (n - 1) + gains[i - 1]) / n
        al = (al * (n - 1) + losses[i - 1]) / n
        out[i] = _rsi(ag, al)
    return pd.Series(out, index=close.index)


def _rsi(ag: float, al: float) -> float:
    if al == 0.0:
        return 100.0 if ag > 0.0 else 50.0
    return 100.0 - 100.0 / (1.0 + ag / al)


def true_range(high: pd.Series, low: pd.Series, close: pd.Series) -> pd.Series:
    """§2.3 — TR_i for i >= 1 (the first bar has no previous close)."""
    prev = close.shift(1)  # previous bar: past data only
    tr = pd.concat([high - low, (high - prev).abs(), (low - prev).abs()], axis=1).max(axis=1)
    tr.iloc[0] = np.nan
    return tr


def atr_wilder(high: pd.Series, low: pd.Series, close: pd.Series, n: int) -> pd.Series:
    """§2.3 — Wilder ATR; seed at bar n = mean(TR_1..TR_n)."""
    tr = true_range(high, low, close).to_numpy(dtype=float)
    out = np.full(len(tr), np.nan)
    if len(tr) <= n:
        return pd.Series(out, index=close.index)
    atr = tr[1 : n + 1].mean()
    out[n] = atr
    for i in range(n + 1, len(tr)):
        atr = (atr * (n - 1) + tr[i]) / n
        out[i] = atr
    return pd.Series(out, index=close.index)


def average_dollar_volume(close: pd.Series, volume: pd.Series, w: int) -> pd.Series:
    """§2.4 — mean of close x volume over the last w bars (split-adjusted both = raw dollars)."""
    return (close * volume).rolling(w, min_periods=w).mean()


def dividend_multiplier(close: pd.Series, dividends: pd.Series) -> pd.Series:
    """§1 — M_t = prod over ex-dates x <= t of (1 + D_x / C_x). Built forward only: a later
    dividend never changes an earlier M."""
    factor = 1.0 + dividends.fillna(0.0) / close
    factor = factor.where(dividends.fillna(0.0) > 0, 1.0)
    if not np.isfinite(factor.to_numpy()).all():
        raise ValueError("dividend on a bar with zero or missing close")
    return factor.cumprod()


def dividend_neutral(bars: pd.DataFrame) -> pd.DataFrame:
    """§1 — O*, H*, L*, C* = price x M_t, plus M itself. `bars` must be sorted by date."""
    m = dividend_multiplier(bars["close"], bars["dividends"])
    return pd.DataFrame(
        {
            "open": bars["open"] * m,
            "high": bars["high"] * m,
            "low": bars["low"] * m,
            "close": bars["close"] * m,
            "m": m,
        },
        index=bars.index,
    )


def is_close(a: float, b: float, tol: float = 1e-9) -> bool:
    """Helper for tests and differential checks: equal within tol, NaN equals NaN."""
    if math.isnan(a) or math.isnan(b):
        return math.isnan(a) and math.isnan(b)
    return abs(a - b) <= tol
