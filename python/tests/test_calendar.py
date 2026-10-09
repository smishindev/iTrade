"""XNYS session calendar: holidays, early closes, UTC times, export round trip."""

from __future__ import annotations

import pandas as pd
import pytest

from itrade.data.calendar import add_sessions, export_calendar, load_sessions, session_table

HOLIDAYS_2024 = [
    "2024-01-01", "2024-01-15", "2024-02-19", "2024-03-29", "2024-05-27",
    "2024-06-19", "2024-07-04", "2024-09-02", "2024-11-28", "2024-12-25",
]  # fmt: skip
EARLY_CLOSES_2024 = ["2024-07-03", "2024-11-29", "2024-12-24"]


@pytest.fixture(scope="module")
def table_2024() -> pd.DataFrame:
    return session_table("XNYS", "2024-01-01", "2024-12-31")


def test_2024_has_252_sessions_and_no_holidays(table_2024):
    sessions = set(table_2024["session"])
    assert len(table_2024) == 252
    assert not sessions & {pd.Timestamp(d) for d in HOLIDAYS_2024}
    assert all(d.weekday() < 5 for d in sessions)


def test_2024_early_closes(table_2024):
    early = table_2024.loc[table_2024["early_close"], "session"]
    assert [d.strftime("%Y-%m-%d") for d in early] == EARLY_CLOSES_2024
    row = table_2024.set_index("session").loc[pd.Timestamp("2024-11-29")]
    assert row["close_utc"] == pd.Timestamp("2024-11-29 18:00", tz="UTC")  # 13:00 New York (EST)


def test_open_close_in_utc_follow_us_daylight_saving(table_2024):
    t = table_2024.set_index("session")
    assert t.loc[pd.Timestamp("2024-01-02"), "open_utc"] == pd.Timestamp(
        "2024-01-02 14:30", tz="UTC"
    )
    assert t.loc[pd.Timestamp("2024-07-01"), "open_utc"] == pd.Timestamp(
        "2024-07-01 13:30", tz="UTC"
    )
    assert t.loc[pd.Timestamp("2024-07-01"), "close_utc"] == pd.Timestamp(
        "2024-07-01 20:00", tz="UTC"
    )


def test_export_round_trip_and_add_sessions(tmp_path):
    export_calendar(tmp_path, "XNYS", "2024-01-01", "2024-12-31")
    sessions = load_sessions(tmp_path, "XNYS")
    assert len(sessions) == 252 and sessions.is_monotonic_increasing
    # Friday before Presidents' Day + 1 session = Tuesday
    assert add_sessions(sessions, pd.Timestamp("2024-02-16"), 1) == pd.Timestamp("2024-02-20")
    # T+2 across Good Friday
    assert add_sessions(sessions, pd.Timestamp("2024-03-27"), 2) == pd.Timestamp("2024-04-01")
    with pytest.raises(ValueError):
        add_sessions(sessions, sessions[-1], 1)
