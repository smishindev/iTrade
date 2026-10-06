"""Parquet storage.

Layout:
    data/raw/<source>/<TICKER>/<UTC timestamp>.parquet   immutable snapshots, never edited
    data/curated/bars/<TICKER>.parquet                   latest snapshot that passed quality checks
    data/curated/manifest.json                           provenance for each curated file

Curated files are always rebuilt from a raw snapshot, so any result can be traced back to
exactly the bytes that were downloaded.
"""

from __future__ import annotations

import hashlib
import json
import re
from datetime import UTC, datetime
from pathlib import Path

import duckdb
import pandas as pd

from itrade.config import data_dir


def safe_name(ticker: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "_", ticker)


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Store:
    def __init__(self, root: Path | None = None):
        self.root = root or data_dir()

    @property
    def raw_dir(self) -> Path:
        return self.root / "raw"

    @property
    def bars_dir(self) -> Path:
        return self.root / "curated" / "bars"

    @property
    def manifest_path(self) -> Path:
        return self.root / "curated" / "manifest.json"

    def write_raw(self, source: str, ticker: str, df: pd.DataFrame) -> Path:
        stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%S%fZ")
        folder = self.raw_dir / source / safe_name(ticker)
        folder.mkdir(parents=True, exist_ok=True)
        # Windows clocks can repeat a timestamp; never overwrite, add a counter instead.
        path = folder / f"{stamp}.parquet"
        n = 1
        while path.exists():
            path = folder / f"{stamp}-{n}.parquet"
            n += 1
        df.to_parquet(path, index=False)
        return path

    def promote(self, ticker: str, raw_path: Path, df: pd.DataFrame) -> Path:
        """Publish a validated raw snapshot as the curated series for `ticker`."""
        self.bars_dir.mkdir(parents=True, exist_ok=True)
        out = df.assign(ticker=ticker)
        path = self.bars_dir / f"{safe_name(ticker)}.parquet"
        out.to_parquet(path, index=False)

        manifest = self.read_manifest()
        manifest[ticker] = {
            "rows": len(df),
            "first": str(df["date"].min().date()) if len(df) else None,
            "last": str(df["date"].max().date()) if len(df) else None,
            "raw_snapshot": raw_path.relative_to(self.root).as_posix(),
            "raw_sha256": _sha256(raw_path),
            "promoted_at": datetime.now(UTC).isoformat(timespec="seconds"),
        }
        self.manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True))
        return path

    def read_manifest(self) -> dict:
        if self.manifest_path.exists():
            return json.loads(self.manifest_path.read_text())
        return {}

    def read_bars(self, ticker: str) -> pd.DataFrame:
        path = self.bars_dir / f"{safe_name(ticker)}.parquet"
        if not path.exists():
            raise FileNotFoundError(f"No curated data for {ticker}; run `itrade ingest` first")
        return pd.read_parquet(path)

    def connect(self) -> duckdb.DuckDBPyConnection:
        """In-memory DuckDB with a `bars` view over every curated file."""
        con = duckdb.connect()
        pattern = (self.bars_dir / "*.parquet").as_posix()
        if any(self.bars_dir.glob("*.parquet")):
            con.execute(f"CREATE VIEW bars AS SELECT * FROM read_parquet('{pattern}')")
        return con
