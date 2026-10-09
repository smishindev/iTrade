"""Fetch -> snapshot raw -> validate -> promote to curated."""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from datetime import date
from pathlib import Path

from itrade.config import Universe
from itrade.data.quality import QualityReport, validate_bars
from itrade.data.sources import DataSource
from itrade.data.store import Store


@dataclass
class IngestResult:
    ticker: str
    report: QualityReport
    raw_path: Path | None
    promoted: bool
    error: str | None = None


def ingest(
    universe: Universe,
    source: DataSource,
    store: Store,
    tickers: Iterable[str] | None = None,
    start: str | None = None,
    today: date | None = None,
) -> list[IngestResult]:
    results = []
    for ticker in tickers or universe.tickers:
        inst = universe.get(ticker)
        try:
            df = source.fetch(ticker, start or universe.start_date)
        except Exception as exc:  # network/vendor failures must not abort the batch
            empty = QualityReport(ticker, 0, None, None)
            results.append(IngestResult(ticker, empty, None, False, f"{type(exc).__name__}: {exc}"))
            continue

        raw_path = store.write_raw(source.name, ticker, df)
        report = validate_bars(
            df,
            ticker,
            kind=inst.kind,
            calendar=None if inst.kind == "fx" else universe.calendar,
            today=today,
        )
        if report.ok:
            store.promote(ticker, raw_path, df)
        results.append(IngestResult(ticker, report, raw_path, report.ok))
    return results
