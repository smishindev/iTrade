"""Dividends, splits and ex-dates (spec §1, §4.1 #5, §6.4)."""

from __future__ import annotations

import pandas as pd
import pytest

from itrade.backtest.corporate_actions import (
    CorporateActionConflict,
    DividendOverride,
    apply_dividend_overrides,
    ex_dividend_dates,
    load_dividend_overrides,
    next_session,
    next_session_is_ex_date,
    overrides_sha256,
    split_events,
    suspicious_dividend_gaps,
)

SESSIONS = pd.DatetimeIndex(pd.bdate_range("2024-01-01", "2024-03-29"))


def bars_with(dividends: dict[str, float] | None = None, splits: dict[str, float] | None = None):
    df = pd.DataFrame({"date": SESSIONS, "close": 100.0, "dividends": 0.0, "splits": 0.0})
    for d, v in (dividends or {}).items():
        df.loc[df["date"] == pd.Timestamp(d), "dividends"] = v
    for d, v in (splits or {}).items():
        df.loc[df["date"] == pd.Timestamp(d), "splits"] = v
    return df


def override(date="2024-02-15", amount=0.5):
    return DividendOverride("XYZ", pd.Timestamp(date), amount, "test source")


def test_override_fills_missing_dividend_without_touching_input():
    bars = bars_with()
    out = apply_dividend_overrides(bars, "XYZ", [override()])
    assert out.loc[out["date"] == "2024-02-15", "dividends"].item() == 0.5
    assert bars["dividends"].sum() == 0  # input unchanged


def test_override_ignored_for_other_tickers():
    out = apply_dividend_overrides(bars_with(), "ABC", [override()])
    assert out["dividends"].sum() == 0


def test_override_equal_to_vendor_within_rounding_is_noop():
    out = apply_dividend_overrides(
        bars_with({"2024-02-15": 0.500}), "XYZ", [override(amount=0.5004)]
    )
    assert out.loc[out["date"] == "2024-02-15", "dividends"].item() == 0.5


def test_conflicting_override_stops_instead_of_guessing():
    with pytest.raises(CorporateActionConflict):
        apply_dividend_overrides(bars_with({"2024-02-15": 0.80}), "XYZ", [override(amount=0.5)])


def test_override_on_non_session_date_is_a_conflict():
    with pytest.raises(CorporateActionConflict):
        apply_dividend_overrides(bars_with(), "XYZ", [override(date="2024-02-17")])  # Saturday


def test_next_session_and_ex_date_check():
    ex = ex_dividend_dates(bars_with({"2024-02-15": 0.5}))
    assert list(ex) == [pd.Timestamp("2024-02-15")]
    assert next_session(SESSIONS, pd.Timestamp("2024-02-14")) == pd.Timestamp("2024-02-15")
    assert next_session_is_ex_date(ex, SESSIONS, pd.Timestamp("2024-02-14"))
    assert not next_session_is_ex_date(ex, SESSIONS, pd.Timestamp("2024-02-15"))
    # Friday -> next session Monday
    ex_monday = ex_dividend_dates(bars_with({"2024-02-19": 0.5}))
    assert next_session_is_ex_date(ex_monday, SESSIONS, pd.Timestamp("2024-02-16"))
    assert next_session(SESSIONS, SESSIONS[-1]) is None


def test_split_events():
    ev = split_events(bars_with(splits={"2024-03-01": 2.0, "2024-03-15": 0.25}))
    assert list(ev["ratio"]) == [2.0, 0.25]
    assert list(ev["date"]) == [pd.Timestamp("2024-03-01"), pd.Timestamp("2024-03-15")]


def test_suspicious_gap_screen():
    quarterly = pd.date_range("2015-03-20", periods=20, freq="QS-MAR") + pd.Timedelta(days=19)
    missing = quarterly.delete(10)  # drop one quarter
    gaps = suspicious_dividend_gaps(pd.DatetimeIndex(missing))
    assert len(gaps) == 1 and gaps[0][2] > 150
    assert suspicious_dividend_gaps(pd.DatetimeIndex(quarterly)) == []


def test_project_overrides_file_is_valid():
    overrides = load_dividend_overrides()
    assert any(o.ticker == "QQQ" and o.ex_date == pd.Timestamp("2020-09-21") for o in overrides)
    assert all(o.source for o in overrides)
    assert len(overrides_sha256()) == 64


def test_overrides_without_source_are_rejected(tmp_path):
    p = tmp_path / "ca.toml"
    p.write_text('[[dividends]]\nticker="X"\nex_date="2024-01-02"\namount=0.1\nsource=""\n')
    with pytest.raises(ValueError):
        load_dividend_overrides(p)
