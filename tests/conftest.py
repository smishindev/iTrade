from __future__ import annotations

import numpy as np
import pandas as pd
import pytest

from itrade.config import load_toml
from itrade.costs import CostConfig


def make_bars(
    sessions: pd.DatetimeIndex, start_price: float = 100.0, seed: int = 0
) -> pd.DataFrame:
    """Synthetic, internally consistent daily bars on the given sessions."""
    rng = np.random.default_rng(seed)
    close = start_price * np.exp(np.cumsum(rng.normal(0, 0.01, len(sessions))))
    open_ = close * (1 + rng.normal(0, 0.002, len(sessions)))
    high = np.maximum(open_, close) * 1.003
    low = np.minimum(open_, close) * 0.997
    return pd.DataFrame(
        {
            "date": sessions,
            "open": open_,
            "high": high,
            "low": low,
            "close": close,
            "adj_close": close,
            "volume": 1_000_000.0,
            "dividends": 0.0,
            "splits": 0.0,
        }
    )


@pytest.fixture
def nyse_sessions() -> pd.DatetimeIndex:
    import exchange_calendars as xcals

    cal = xcals.get_calendar("XNYS", start="2024-01-01", end="2024-12-31")
    return pd.DatetimeIndex(cal.sessions_in_range("2024-01-02", "2024-12-31")).tz_localize(None)


@pytest.fixture
def bars(nyse_sessions: pd.DatetimeIndex) -> pd.DataFrame:
    return make_bars(nyse_sessions)


@pytest.fixture
def cost_cfg() -> CostConfig:
    return CostConfig.from_dict(load_toml("costs.toml"))
