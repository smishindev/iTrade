"""Dividends, splits and ex-dividend dates (spec §1, §4.1 #5, §6.4).

Vendor bars carry `dividends` (amount per share on the ex-date, split-adjusted) and `splits`
(ratio on the ex-date). Manual corrections from config/corporate_actions.toml are applied on top,
never written back into the data files.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from pathlib import Path

import pandas as pd

from itrade.config import project_root


class CorporateActionConflict(ValueError):
    """A manual correction disagrees with the vendor: stop the instrument, do not guess."""


@dataclass(frozen=True)
class DividendOverride:
    ticker: str
    ex_date: pd.Timestamp
    amount: float
    source: str


def overrides_path() -> Path:
    return project_root() / "config" / "corporate_actions.toml"


def load_dividend_overrides(path: Path | None = None) -> list[DividendOverride]:
    import tomllib

    path = path or overrides_path()
    if not path.exists():
        return []
    with path.open("rb") as f:
        raw = tomllib.load(f)
    out = []
    for entry in raw.get("dividends", []):
        if not str(entry.get("source", "")).strip():
            raise ValueError(f"Dividend override for {entry.get('ticker')} has no source")
        if float(entry["amount"]) <= 0:
            raise ValueError(f"Dividend override for {entry['ticker']} must be positive")
        out.append(
            DividendOverride(
                ticker=entry["ticker"],
                ex_date=pd.Timestamp(entry["ex_date"]),
                amount=float(entry["amount"]),
                source=entry["source"],
            )
        )
    return out


def overrides_sha256(path: Path | None = None) -> str:
    path = path or overrides_path()
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else ""


def apply_dividend_overrides(
    bars: pd.DataFrame, ticker: str, overrides: list[DividendOverride], tolerance: float = 1e-3
) -> pd.DataFrame:
    """Return a copy of `bars` with missing dividends filled from `overrides`.

    Raises CorporateActionConflict if the override date is not a bar date, or the vendor already
    has a different amount on that date (beyond Yahoo's $0.001 rounding).
    """
    out = bars.copy()
    dates = pd.to_datetime(out["date"])
    for o in (o for o in overrides if o.ticker == ticker):
        hit = dates == o.ex_date
        if not hit.any():
            raise CorporateActionConflict(
                f"{ticker}: override ex-date {o.ex_date.date()} has no bar"
            )
        vendor = float(out.loc[hit, "dividends"].iloc[0])
        if vendor == 0:
            out.loc[hit, "dividends"] = o.amount
        elif abs(vendor - o.amount) > tolerance:
            raise CorporateActionConflict(
                f"{ticker} {o.ex_date.date()}: vendor dividend {vendor} != override {o.amount}"
            )
    return out


def ex_dividend_dates(bars: pd.DataFrame) -> pd.DatetimeIndex:
    """Ex-dates of an instrument (dates with a positive dividend)."""
    return pd.DatetimeIndex(pd.to_datetime(bars.loc[bars["dividends"] > 0, "date"])).sort_values()


def split_events(bars: pd.DataFrame) -> pd.DataFrame:
    """Split (or Yahoo-encoded spin-off) events: date and ratio (2.0 = 2:1, 0.25 = 1:4)."""
    ev = bars.loc[bars["splits"].fillna(0) != 0, ["date", "splits"]].rename(
        columns={"splits": "ratio"}
    )
    return ev.assign(date=pd.to_datetime(ev["date"])).reset_index(drop=True)


def next_session(sessions: pd.DatetimeIndex, t: pd.Timestamp) -> pd.Timestamp | None:
    """The first session strictly after t (sessions must be sorted)."""
    i = sessions.searchsorted(pd.Timestamp(t), side="right")
    return sessions[i] if i < len(sessions) else None


def next_session_is_ex_date(
    ex_dates: pd.DatetimeIndex, sessions: pd.DatetimeIndex, t: pd.Timestamp
) -> bool:
    """Spec §4.1 #5: is session t+1 an ex-dividend date of the instrument?"""
    nxt = next_session(sessions, t)
    return nxt is not None and nxt in ex_dates


def suspicious_dividend_gaps(
    ex_dates: pd.DatetimeIndex, factor: float = 1.6, window: int = 8
) -> list[tuple[pd.Timestamp, pd.Timestamp, int]]:
    """Gaps between consecutive ex-dates longer than `factor` x the trailing median gap.

    A screening aid for missing vendor dividends: funds that change their schedule (annual,
    irregular payers) also show up and must be judged by a person (docs/data-notes.md).
    """
    ex = pd.Series(ex_dates.sort_values())
    if len(ex) < 4:
        return []
    gaps = ex.diff().dt.days
    typical = gaps.rolling(window, min_periods=3).median().shift(1)  # past gaps only
    return [
        (ex.iloc[i - 1], ex.iloc[i], int(gaps.iloc[i]))
        for i in range(1, len(ex))
        if pd.notna(typical.iloc[i]) and gaps.iloc[i] > factor * typical.iloc[i]
    ]
