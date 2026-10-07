"""Trade-list and portfolio metrics of a backtest (spec §7, research-integrity skill)."""

from __future__ import annotations

import pandas as pd

# A pullback strategy with a 2xATR stop and costs at a small account size cannot plausibly win
# more often than this; such a result means a bug or look-ahead until proven otherwise.
MAX_PLAUSIBLE_WIN_RATE = 0.80
MAX_PLAUSIBLE_EXPECTANCY_R = 1.0


def implausibility_flags(trades: pd.DataFrame) -> list[str]:
    """Names of red flags raised by a trade list (empty = nothing implausible)."""
    if trades.empty:
        return []
    r = trades["r"].astype(float)
    flags = []
    if (r > 0).mean() > MAX_PLAUSIBLE_WIN_RATE:
        flags.append("win_rate")
    if r.mean() > MAX_PLAUSIBLE_EXPECTANCY_R:
        flags.append("expectancy")
    return flags
