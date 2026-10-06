from __future__ import annotations

import pandas as pd
import pytest

from itrade.config import load_universe
from itrade.data.ingest import ingest
from itrade.data.sources import normalize_bars
from itrade.data.store import Store

UNIVERSE_RAW = {
    "project": {
        "base_currency": "ILS",
        "trading_currency": "USD",
        "calendar": "XNYS",
        "start_date": "2024-01-01",
    },
    "benchmark": {"ticker": "GOOD", "fallback_ticker": "GOOD"},
    "instruments": [{"ticker": "GOOD"}, {"ticker": "BAD"}, {"ticker": "BOOM"}],
}


class FakeSource:
    name = "fake"

    def __init__(self, frames: dict[str, pd.DataFrame]):
        self.frames = frames

    def fetch(self, ticker, start, end=None):
        if ticker == "BOOM":
            raise ConnectionError("vendor down")
        return self.frames[ticker]


def test_ingest_promotes_only_clean_series(tmp_path, bars):
    bad = bars.copy()
    bad.loc[3, "close"] = -1.0
    store = Store(tmp_path)
    results = ingest(load_universe(UNIVERSE_RAW), FakeSource({"GOOD": bars, "BAD": bad}), store)
    by = {r.ticker: r for r in results}

    assert by["GOOD"].promoted
    assert not by["BAD"].promoted
    assert by["BAD"].raw_path.exists()  # raw snapshot kept even when rejected
    assert by["BOOM"].error and "vendor down" in by["BOOM"].error

    manifest = store.read_manifest()
    assert set(manifest) == {"GOOD"}
    assert manifest["GOOD"]["rows"] == len(bars)
    assert len(manifest["GOOD"]["raw_sha256"]) == 64


def test_raw_snapshots_are_never_overwritten(tmp_path, bars):
    store = Store(tmp_path)
    a = store.write_raw("fake", "X", bars)
    b = store.write_raw("fake", "X", bars)
    assert a != b and a.exists() and b.exists()


def test_duckdb_view_over_curated(tmp_path, bars):
    store = Store(tmp_path)
    ingest(load_universe(UNIVERSE_RAW), FakeSource({"GOOD": bars}), store, tickers=["GOOD"])
    n = store.connect().sql("select count(*) from bars where ticker = 'GOOD'").fetchone()[0]
    assert n == len(bars)


def test_read_missing_ticker_raises(tmp_path):
    with pytest.raises(FileNotFoundError):
        Store(tmp_path).read_bars("NOPE")


def test_normalize_yahoo_frame():
    idx = pd.DatetimeIndex(["2024-01-02 00:00", "2024-01-03 00:00"]).tz_localize("America/New_York")
    yahoo = pd.DataFrame(
        {
            "Open": [1.0, 2.0],
            "High": [1.5, 2.5],
            "Low": [0.5, 1.5],
            "Close": [1.2, 2.2],
            "Adj Close": [1.1, 2.1],
            "Volume": [10, 20],
            "Dividends": [0.0, 0.0],
            "Stock Splits": [0.0, 0.0],
        },
        index=idx,
    )
    out = normalize_bars(yahoo)
    assert list(out.columns)[:2] == ["date", "open"]
    assert out["date"].dt.tz is None
    assert out["adj_close"].tolist() == [1.1, 2.1]
