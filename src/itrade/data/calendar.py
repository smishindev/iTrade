"""Exchange session calendar (XNYS) — one table shared by the Python spike and the C# platform.

Columns: session (date), open_utc, close_utc (tz-aware UTC timestamps), early_close (bool).
The C# side imports the same Parquet file (roadmap P3.2.01), so both use identical sessions.
"""

from __future__ import annotations

from pathlib import Path

import pandas as pd

CALENDAR_COLUMNS = ["session", "open_utc", "close_utc", "early_close"]


def session_table(calendar: str, start: str, end: str) -> pd.DataFrame:
    import exchange_calendars as xcals

    cal = xcals.get_calendar(calendar, start=start, end=end)
    schedule = cal.schedule.loc[start:end]
    early = set(pd.DatetimeIndex(cal.early_closes).normalize())
    sessions = pd.DatetimeIndex(schedule.index).tz_localize(None).normalize()
    return pd.DataFrame(
        {
            "session": sessions,
            "open_utc": pd.DatetimeIndex(schedule["open"]).tz_convert("UTC"),
            "close_utc": pd.DatetimeIndex(schedule["close"]).tz_convert("UTC"),
            "early_close": [s in early for s in sessions],
        }
    )[CALENDAR_COLUMNS].reset_index(drop=True)


def calendar_path(store_root: Path, calendar: str) -> Path:
    return store_root / "curated" / "calendar" / f"{calendar}.parquet"


def export_calendar(store_root: Path, calendar: str, start: str, end: str) -> Path:
    table = session_table(calendar, start, end)
    path = calendar_path(store_root, calendar)
    path.parent.mkdir(parents=True, exist_ok=True)
    table.to_parquet(path, index=False)
    return path


def load_sessions(store_root: Path, calendar: str = "XNYS") -> pd.DatetimeIndex:
    """Session dates (tz-naive, sorted) from the exported calendar."""
    table = pd.read_parquet(calendar_path(store_root, calendar), columns=["session"])
    return pd.DatetimeIndex(table["session"]).sort_values()


def add_sessions(sessions: pd.DatetimeIndex, t: pd.Timestamp, n: int) -> pd.Timestamp:
    """The session n sessions after t (t must be a session). Used for settlement T+n."""
    i = sessions.get_loc(pd.Timestamp(t))
    if i + n >= len(sessions):
        raise ValueError(f"calendar ends before {t.date()} + {n} sessions")
    return sessions[i + n]
