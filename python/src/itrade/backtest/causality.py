"""Look-ahead detector: decisions at session d must not change when prices after d change.

For each checked date d, every bar dated after d gets random price and volume multipliers
(dividends and splits are kept: the spec treats them as known in advance, §4.1 #5 and §1).
Entry candidates, skipped signals and exit decisions at d are recomputed and must be identical,
bit for bit, to the run on the original data. A strategy that peeks at the future is caught.
"""

from __future__ import annotations

from collections.abc import Callable, Iterable, Mapping
from decimal import Decimal

import numpy as np
import pandas as pd

from itrade.strategies.etf_pullback_v1 import (
    SignalParams,
    entry_signals,
    exit_signal,
    prepare_instrument,
)

PrepareFn = Callable[[pd.DataFrame, pd.Series, pd.DatetimeIndex, SignalParams], pd.DataFrame]


def mutate_future(bars: pd.DataFrame, d: pd.Timestamp, seed: int) -> pd.DataFrame:
    """Copy of `bars` with every bar after d scaled by a random factor (OHLC together, so bars
    stay internally consistent) and random volume. Dividends and splits are untouched."""
    out = bars.copy()
    future = pd.to_datetime(out["date"]) > pd.Timestamp(d)
    n = int(future.sum())
    if n == 0:
        return out
    rng = np.random.default_rng(seed)
    price_factor = rng.uniform(0.5, 1.5, n)
    for col in ("open", "high", "low", "close"):
        out.loc[future, col] = out.loc[future, col].to_numpy() * price_factor
    out.loc[future, "volume"] = out.loc[future, "volume"].to_numpy() * rng.uniform(0.1, 10.0, n)
    return out


def decisions_at(
    d: pd.Timestamp,
    prepared: Mapping[str, pd.DataFrame],
    sessions: pd.DatetimeIndex,
    p: SignalParams,
    hold_from: int = 3,
):
    """Everything the strategy decides after the close of d: entries, skips and, for a
    hypothetical position opened `hold_from` sessions earlier in every instrument, exits."""
    entries, skipped = entry_signals(d, prepared, set(), p)
    i = sessions.get_loc(pd.Timestamp(d))
    entry_date = sessions[max(i - hold_from, 0)]
    exits = {
        t: exit_signal(d, t, entry_date, frame, sessions, p)
        for t, frame in prepared.items()
        if d in frame.index
    }
    return entries, skipped, exits


def causality_violations(
    bars: Mapping[str, pd.DataFrame],
    member: Mapping[str, pd.Series],
    sessions: pd.DatetimeIndex,
    p: SignalParams,
    dates: Iterable[pd.Timestamp],
    prepare: PrepareFn = prepare_instrument,
    seed: int = 0,
) -> list[pd.Timestamp]:
    """Dates whose decisions changed when only the future was changed (empty = causal)."""
    original = {t: prepare(b, member[t], sessions, p) for t, b in bars.items()}
    violations = []
    for k, d in enumerate(dates):
        d = pd.Timestamp(d)
        mutated = {
            t: prepare(mutate_future(b, d, seed + 1000 * k + j), member[t], sessions, p)
            for j, (t, b) in enumerate(bars.items())
        }
        if decisions_at(d, original, sessions, p) != decisions_at(d, mutated, sessions, p):
            violations.append(d)
    return violations


def trend_decisions_at(d: pd.Timestamp, prepared, sessions: pd.DatetimeIndex, p, groups):
    """ETF_TREND_V2: exits (every instrument hypothetically held) and entries after the close
    of d, from a fresh rules object — at a month end this includes the rotation ranking."""
    rules = p.make_rules(sessions, groups)
    rules.on_close(d, Decimal("3280"))  # exercises the rotation's buyability filter
    held = {t: None for t, f in prepared.items() if d in f.index}
    exits = rules.exit_decisions(d, held, prepared, sessions)
    entries, skipped = rules.entry_decisions(d, prepared, set())
    return exits, entries, skipped, list(getattr(rules, "target", []))


def trend_causality_violations(
    bars: Mapping[str, pd.DataFrame],
    member: Mapping[str, pd.Series],
    sessions: pd.DatetimeIndex,
    p,
    groups: Mapping[str, str],
    dates: Iterable[pd.Timestamp],
    prepare: Callable | None = None,
    seed: int = 0,
) -> list[pd.Timestamp]:
    """Dates whose ETF_TREND_V2 decisions changed when only the future was changed."""
    from itrade.strategies.etf_trend_v2 import prepare_trend_instrument

    prepare = prepare or prepare_trend_instrument
    original = {t: prepare(b, member[t], sessions, p) for t, b in bars.items()}
    violations = []
    for k, d in enumerate(dates):
        d = pd.Timestamp(d)
        mutated = {
            t: prepare(mutate_future(b, d, seed + 1000 * k + j), member[t], sessions, p)
            for j, (t, b) in enumerate(bars.items())
        }
        a = trend_decisions_at(d, original, sessions, p, groups)
        b = trend_decisions_at(d, mutated, sessions, p, groups)
        if a != b:
            violations.append(d)
    return violations
