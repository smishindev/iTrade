from __future__ import annotations

from datetime import date

import pandas as pd

from itrade.data.quality import ERROR, WARNING, validate_bars


def checks(report, severity=None):
    return {i.check for i in report.issues if severity is None or i.severity == severity}


def test_clean_bars_pass(bars):
    report = validate_bars(bars, "TEST")
    assert report.ok, report.issues
    assert report.rows == len(bars)


def test_empty_is_error():
    report = validate_bars(pd.DataFrame(columns=["date"]), "TEST")
    assert not report.ok
    assert "empty" in checks(report, ERROR)


def test_duplicate_dates_error(bars):
    dup = pd.concat([bars, bars.iloc[[10]]]).sort_values("date", kind="stable")
    report = validate_bars(dup.reset_index(drop=True), "TEST")
    assert "duplicate_dates" in checks(report, ERROR)


def test_unsorted_error(bars):
    report = validate_bars(bars.iloc[::-1].reset_index(drop=True), "TEST")
    assert "unsorted" in checks(report, ERROR)


def test_missing_close_error(bars):
    bars.loc[5, "close"] = None
    assert "missing_values" in checks(validate_bars(bars, "TEST"), ERROR)


def test_non_positive_price_error(bars):
    bars.loc[7, "adj_close"] = 0.0
    assert "non_positive_price" in checks(validate_bars(bars, "TEST"), ERROR)


def test_ohlc_inconsistent_error_for_instrument_warning_for_fx(bars):
    bars.loc[3, "high"] = bars.loc[3, "low"] * 0.5
    assert "ohlc_inconsistent" in checks(validate_bars(bars, "TEST"), ERROR)
    fx = validate_bars(bars, "ILS=X", kind="fx", calendar=None)
    assert "ohlc_inconsistent" in checks(fx, WARNING)
    assert fx.ok


def test_few_missing_sessions_warn_many_error(bars):
    few = validate_bars(bars.drop(index=[20]).reset_index(drop=True), "TEST")
    assert "missing_sessions" in checks(few, WARNING)
    assert few.ok
    many = validate_bars(bars.drop(index=range(20, 40)).reset_index(drop=True), "TEST")
    assert "missing_sessions" in checks(many, ERROR)


def test_weekend_bar_flagged(bars):
    extra = bars.iloc[[0]].assign(date=pd.Timestamp("2024-01-06"))  # Saturday
    df = pd.concat([bars, extra]).sort_values("date").reset_index(drop=True)
    assert "non_session_dates" in checks(validate_bars(df, "TEST"), WARNING)


def test_return_outlier_warns(bars):
    bars.loc[100:, ["open", "high", "low", "close", "adj_close"]] *= 0.5  # unadjusted 2:1 split
    report = validate_bars(bars, "TEST")
    assert "return_outlier" in checks(report, WARNING)


def test_stale_warns(bars):
    report = validate_bars(bars, "TEST", today=date(2025, 3, 1))
    assert "stale" in checks(report, WARNING)


def test_fx_long_gap_warns(bars):
    df = bars.drop(index=range(50, 60)).reset_index(drop=True)
    report = validate_bars(df, "ILS=X", kind="fx", calendar=None)
    assert "gap" in checks(report, WARNING)
    assert "missing_sessions" not in checks(report)
