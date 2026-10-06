"""Market data sources. Each source returns daily bars in the canonical BAR_COLUMNS shape.

Yahoo ("close" is split-adjusted, "adj_close" is split- and dividend-adjusted) is free and
unofficial: good enough for research on liquid ETFs, not a source of truth. Add a second
source here before relying on any result that hinges on a few days of data.
"""

from __future__ import annotations

from typing import Protocol

import pandas as pd

from itrade.data import BAR_COLUMNS


class DataSource(Protocol):
    name: str

    def fetch(self, ticker: str, start: str, end: str | None = None) -> pd.DataFrame: ...


def normalize_bars(df: pd.DataFrame) -> pd.DataFrame:
    """Map a Yahoo-style frame (DatetimeIndex, Title Case columns) to BAR_COLUMNS."""
    if df.empty:
        return pd.DataFrame(columns=BAR_COLUMNS)
    out = df.rename(
        columns={
            "Open": "open",
            "High": "high",
            "Low": "low",
            "Close": "close",
            "Adj Close": "adj_close",
            "Volume": "volume",
            "Dividends": "dividends",
            "Stock Splits": "splits",
        }
    )
    index = pd.DatetimeIndex(out.index)
    if index.tz is not None:
        index = index.tz_localize(None)
    out.index = index.normalize()
    out = out.rename_axis("date").reset_index()
    for col in ("dividends", "splits"):
        if col not in out:
            out[col] = 0.0
    if "adj_close" not in out:
        out["adj_close"] = out["close"]
    return out[BAR_COLUMNS].astype({"volume": "float64"})


class YFinanceSource:
    name = "yfinance"

    def fetch(self, ticker: str, start: str, end: str | None = None) -> pd.DataFrame:
        import yfinance as yf

        raw = yf.Ticker(ticker).history(
            start=start, end=end, interval="1d", auto_adjust=False, actions=True
        )
        return normalize_bars(raw)


SOURCES: dict[str, type] = {"yfinance": YFinanceSource}


def get_source(name: str) -> DataSource:
    try:
        return SOURCES[name]()
    except KeyError:
        raise ValueError(f"Unknown data source {name!r}; known: {sorted(SOURCES)}") from None
