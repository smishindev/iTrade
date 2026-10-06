"""Data quality checks. Errors block promotion to curated; warnings are reported.

A backtest is only as good as its bars. Each check here exists because the failure it
catches silently produces a too-good (or too-bad) backtest.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date

import pandas as pd

ERROR = "error"
WARNING = "warning"

MAX_ABS_DAILY_RETURN = 0.20
MAX_FX_GAP_BUSINESS_DAYS = 5
STALE_AFTER_SESSIONS = 5


@dataclass(frozen=True)
class Issue:
    severity: str
    check: str
    message: str


@dataclass
class QualityReport:
    ticker: str
    rows: int
    first: date | None
    last: date | None
    issues: list[Issue] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not any(i.severity == ERROR for i in self.issues)

    @property
    def errors(self) -> list[Issue]:
        return [i for i in self.issues if i.severity == ERROR]

    @property
    def warnings(self) -> list[Issue]:
        return [i for i in self.issues if i.severity == WARNING]


def _sample(dates: pd.Index | pd.Series, n: int = 5) -> str:
    items = [str(pd.Timestamp(d).date()) for d in list(dates)[:n]]
    more = len(dates) - n
    return ", ".join(items) + (f" (+{more} more)" if more > 0 else "")


def _expected_sessions(calendar: str, start: pd.Timestamp, end: pd.Timestamp) -> pd.DatetimeIndex:
    import exchange_calendars as xcals

    cal = xcals.get_calendar(calendar, start=start, end=end)
    sessions = cal.sessions_in_range(start, end)
    return pd.DatetimeIndex(sessions).tz_localize(None).normalize()


def validate_bars(
    df: pd.DataFrame,
    ticker: str,
    *,
    kind: str = "instrument",
    calendar: str | None = "XNYS",
    today: date | None = None,
) -> QualityReport:
    """Run every check on a BAR_COLUMNS frame. `kind="fx"` relaxes exchange-specific checks."""
    if df.empty:
        return QualityReport(ticker, 0, None, None, [Issue(ERROR, "empty", "no rows returned")])

    dates = pd.to_datetime(df["date"])
    report = QualityReport(ticker, len(df), dates.min().date(), dates.max().date())
    add = report.issues.append
    is_fx = kind == "fx"

    dupes = dates[dates.duplicated()]
    if len(dupes):
        add(Issue(ERROR, "duplicate_dates", f"{len(dupes)} duplicate dates: {_sample(dupes)}"))
    if not dates.is_monotonic_increasing:
        add(Issue(ERROR, "unsorted", "dates are not in ascending order"))

    for col in ("close", "adj_close"):
        n = int(df[col].isna().sum())
        if n:
            add(Issue(ERROR, "missing_values", f"{n} missing {col} values"))
    for col in ("open", "high", "low"):
        n = int(df[col].isna().sum())
        if n:
            add(Issue(WARNING, "missing_values", f"{n} missing {col} values"))

    prices = df[["open", "high", "low", "close", "adj_close"]]
    bad_price = dates[(prices <= 0).any(axis=1)]
    if len(bad_price):
        add(Issue(ERROR, "non_positive_price", f"{len(bad_price)} rows: {_sample(bad_price)}"))

    tol = 1e-6 * df["close"].abs()
    ohlc_bad = (df["high"] + tol < df[["open", "close", "low"]].max(axis=1)) | (
        df["low"] - tol > df[["open", "close"]].min(axis=1)
    )
    bad = dates[ohlc_bad.fillna(False)]
    if len(bad):
        # Yahoo FX bars are mid-quote snapshots and are routinely slightly inconsistent.
        add(
            Issue(
                WARNING if is_fx else ERROR,
                "ohlc_inconsistent",
                f"{len(bad)} rows where high/low do not bound open/close: {_sample(bad)}",
            )
        )

    if not is_fx:
        neg = dates[df["volume"] < 0]
        if len(neg):
            add(Issue(ERROR, "negative_volume", f"{len(neg)} rows: {_sample(neg)}"))
        zero = dates[df["volume"] == 0]
        if len(zero):
            add(Issue(WARNING, "zero_volume", f"{len(zero)} rows: {_sample(zero)}"))

    returns = df["adj_close"].pct_change()
    jumps = dates[returns.abs() > MAX_ABS_DAILY_RETURN]
    if len(jumps):
        add(
            Issue(
                WARNING,
                "return_outlier",
                f"{len(jumps)} days with |return| > {MAX_ABS_DAILY_RETURN:.0%} "
                f"(bad split/dividend adjustment?): {_sample(jumps)}",
            )
        )

    unique_dates = pd.DatetimeIndex(dates.drop_duplicates())
    if is_fx or calendar is None:
        # Business days strictly between consecutive bars.
        between = [
            len(pd.bdate_range(a, b)) - 2
            for a, b in zip(unique_dates[:-1], unique_dates[1:], strict=True)
        ]
        long_gaps = [
            unique_dates[i + 1] for i, n in enumerate(between) if n > MAX_FX_GAP_BUSINESS_DAYS
        ]
        if long_gaps:
            add(
                Issue(
                    WARNING,
                    "gap",
                    f"{len(long_gaps)} gaps > {MAX_FX_GAP_BUSINESS_DAYS} business days ending "
                    f"{_sample(long_gaps)}",
                )
            )
    else:
        expected = _expected_sessions(calendar, unique_dates.min(), unique_dates.max())
        missing = expected.difference(unique_dates)
        extra = unique_dates.difference(expected)
        if len(missing):
            sev = ERROR if len(missing) > 0.01 * len(expected) else WARNING
            add(
                Issue(
                    sev,
                    "missing_sessions",
                    f"{len(missing)} {calendar} sessions missing: {_sample(missing)}",
                )
            )
        if len(extra):
            add(
                Issue(
                    WARNING,
                    "non_session_dates",
                    f"{len(extra)} dates not in {calendar}: {_sample(extra)}",
                )
            )

    if today is not None:
        lag = len(pd.bdate_range(unique_dates.max(), pd.Timestamp(today))) - 1
        if lag > STALE_AFTER_SESSIONS:
            add(Issue(WARNING, "stale", f"last bar {report.last} is {lag} business days old"))

    split_days = dates[df["splits"].fillna(0) != 0]
    if len(split_days):
        add(
            Issue(
                WARNING,
                "split_event",
                f"{len(split_days)} split events (verify adjustment): {_sample(split_days)}",
            )
        )

    return report
