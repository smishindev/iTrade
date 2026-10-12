"""Closing prices as they traded, exported for the platform (roadmap P3.3.02).

The curated bars carry the vendor's `close`, which is adjusted back in time for every later split:
after a 2:1 split all earlier closes are halved. The platform's books hold the number of shares that
was really held on a date, so they need the price that was really paid on that date. This module
undoes the adjustment and writes what the C# Host imports:

    <data>/derived/closing-prices/closing-prices.csv            ticker,session,close
    <data>/derived/closing-prices/closing-prices-sessions.csv   the exchange's sessions these cover
    <data>/derived/closing-prices/closing-prices-events.csv     ticker,day,kind,value (see below)
    <data>/derived/closing-prices/closing-prices.json           SHA-256 of the three; their origin

Only finished sessions are exported: a bar fetched while its session was still open holds the last
trade, not the close. The sessions come from the exchange calendar, so the platform can tell a day
the exchange was closed from a day whose price is missing.

The events are what a fund did to its holders on a day: a dividend per share as it was paid (the
vendor shows it divided by every later split, like its closes) and a split as new shares per old
one. The platform uses them to follow an index fund with the owner's contributions; the dividends
the owner really received come from the broker's statement, not from here.

These prices are for valuation only - never for tax and never for an order.
"""

from __future__ import annotations

import csv
import hashlib
import json
import math
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

import pandas as pd

from itrade.backtest.corporate_actions import (
    CorporateActionConflict,
    DividendOverride,
    apply_dividend_overrides,
    load_dividend_overrides,
)
from itrade.config import project_root
from itrade.data.store import Store

CSV_NAME = "closing-prices.csv"
SESSIONS_NAME = "closing-prices-sessions.csv"
MANIFEST_NAME = "closing-prices.json"
HEADER = "ticker,session,close"
SESSIONS_HEADER = "session"
EVENTS_NAME = "closing-prices-events.csv"
EVENTS_HEADER = "ticker,day,kind,value"
DIVIDEND, SPLIT = "dividend", "split"
EVENT_DECIMALS = 6  # what the platform keeps; a dividend is quoted to four to six places
CALENDAR = "XNYS"

# A bar is a close once its session is over. The margin covers the closing auction and the vendor
# publishing the official close; a bar fetched earlier is the last trade so far and is left out
# (the next ingest brings it).
FINAL_AFTER_CLOSE = pd.Timedelta(minutes=30)

# The vendor's numbers are 32-bit floats (769.640015 for 769.64), and undoing a split multiplies
# their error: about value x 2^-23 after any scaling seen so far, so twice that is allowed, with a
# floor for small prices. A price is read as what it was quoted in, or it is refused.
#
# Checked on all curated closes, 2026-10-12 (200,918 prices of 32 funds): 189,964 are whole cents;
# 10,902 are fractions of a dollar, all before 2001-01-27 (US exchanges quoted in 1/8 ... 1/64 until
# decimalisation); 52 are three-decimal closes of two funds in their first months (XLRE 2015-16,
# XLC 2018). None has four decimals.
NOISE_RELATIVE = 2.0**-22
NOISE_FLOOR = 0.00002
FRACTION_OF_A_DOLLAR = 64


class ClosesError(ValueError):
    """The curated bars of a ticker cannot be turned into closing prices."""


def split_scale(bars: pd.DataFrame) -> pd.Series:
    """S(d) = product of the split ratios dated after d: price as traded = vendor close x S(d).

    The same rule the research engine uses (`backtest.corporate_actions.split_scale`, strategy
    spec §1); a test holds the two together. Bars must be sorted by date.
    """
    ratio = bars["splits"].fillna(0.0).replace(0.0, 1.0).to_numpy(dtype=float)
    after = ratio[::-1].cumprod()[::-1]  # product over x >= d
    return pd.Series(after / ratio, index=bars.index)  # without d itself


def price_text(value: float) -> str:
    """The price as exact decimal text: a whole cent; a fraction of a dollar as quoted before 2001
    (95.5625); a price of three or four decimals. Each is recognised only within the float's own
    noise - a value that is none of them is refused, not rounded to the nearest. (From a few
    hundred dollars up the noise is as wide as the fourth decimal, so there the order decides:
    cents first.)
    """
    if not math.isfinite(value) or value <= 0:
        raise ClosesError(f"not a price: {value!r}")
    noise = max(NOISE_FLOOR, value * NOISE_RELATIVE)
    cents = round(value, 2)
    if cents > 0 and abs(value - cents) <= noise:
        return f"{cents:.2f}"
    fraction = round(value * FRACTION_OF_A_DOLLAR) / FRACTION_OF_A_DOLLAR  # exact in binary
    if fraction > 0 and abs(value - fraction) <= noise:
        return f"{fraction:.6f}".rstrip("0")
    for decimals in (3, 4):
        rounded = round(value, decimals)
        if rounded > 0 and abs(value - rounded) <= noise:
            return f"{rounded:.{decimals}f}"
    raise ClosesError(f"{value!r} is neither cents, nor a fraction of a dollar, nor 4 decimals")


def as_traded_closes(bars: pd.DataFrame, ticker: str) -> list[tuple[str, str]]:
    """(session, close) for every bar of a ticker, earliest first, the close as traded that day."""
    if bars.empty:
        raise ClosesError(f"{ticker}: no bars")
    dates = pd.to_datetime(bars["date"])
    if not dates.is_monotonic_increasing or dates.duplicated().any():
        raise ClosesError(f"{ticker}: bars are not one per date in order of date")
    splits = bars["splits"].fillna(0.0)
    if (splits < 0).any() or not splits.map(math.isfinite).all():
        raise ClosesError(f"{ticker}: a split ratio that is not a ratio")
    raw = bars["close"].astype(float) * split_scale(bars)
    closes = []
    for day, value in zip(dates, raw, strict=True):
        try:
            closes.append((day.date().isoformat(), price_text(value)))
        except ClosesError as exc:
            raise ClosesError(f"{ticker} {day.date()}: {exc}") from exc
    return closes


def amount_text(value: float) -> str:
    """A dividend per share or a split ratio as decimal text of at most six places, without the
    float's tail. Unlike a close it is not read as "what it was quoted in": six places are finer
    than any dividend is declared in, and nothing is paid or taxed by this figure."""
    if not math.isfinite(value) or value <= 0:
        raise ClosesError(f"not an amount: {value!r}")
    rounded = round(value, EVENT_DECIMALS)
    if rounded <= 0:
        raise ClosesError(f"{value!r} is nothing at {EVENT_DECIMALS} decimals")
    text = f"{rounded:.{EVENT_DECIMALS}f}".rstrip("0").rstrip(".")
    return text


def fund_events(bars: pd.DataFrame, ticker: str) -> list[tuple[str, str, str]]:
    """(day, kind, value) for every dividend and split of a ticker, earliest first: the dividend
    per share as it was paid that day, the split as new shares per old one."""
    dates = pd.to_datetime(bars["date"])
    scale = split_scale(bars)
    events = []
    for day, paid, ratio, s in zip(
        dates, bars["dividends"].fillna(0.0), bars["splits"].fillna(0.0), scale, strict=True
    ):
        try:
            if paid != 0:
                # Paid on the shares of that day: the day's own split is already in them.
                events.append((day.date().isoformat(), DIVIDEND, amount_text(float(paid) * s)))
            if ratio != 0:
                events.append((day.date().isoformat(), SPLIT, amount_text(float(ratio))))
        except ClosesError as exc:
            raise ClosesError(f"{ticker} {day.date()}: {exc}") from exc
    return events


def with_known_corrections(
    bars: pd.DataFrame, ticker: str, overrides: list[DividendOverride]
) -> tuple[pd.DataFrame, list[str]]:
    """The bars with the dividends the vendor is known to lack (config/corporate_actions.toml) put
    in, and the days that were corrected. A correction outside the days the bars cover is not of
    this series; one that contradicts the vendor stops the export."""
    dates = pd.to_datetime(bars["date"])
    mine = [o for o in overrides if o.ticker == ticker and dates.min() <= o.ex_date <= dates.max()]
    try:
        corrected = apply_dividend_overrides(bars, ticker, mine)
    except CorporateActionConflict as exc:
        raise ClosesError(str(exc)) from exc
    changed = corrected["dividends"].fillna(0.0) != bars["dividends"].fillna(0.0)
    return corrected, [d.date().isoformat() for d in dates[changed]]


def finished_sessions(calendar: pd.DataFrame, fetched_at: datetime) -> list[str]:
    """The sessions of the calendar that were over when the data was fetched, earliest first."""
    over = calendar["close_utc"] + FINAL_AFTER_CLOSE <= pd.Timestamp(fetched_at)
    return [d.date().isoformat() for d in pd.to_datetime(calendar.loc[over, "session"])]


def catalogue_currencies() -> dict[str, str]:
    """Ticker -> currency from config/instruments.csv: what the platform takes a price to be in."""
    path = project_root() / "config" / "instruments.csv"
    if not path.exists():
        return {}
    lines = [
        line for line in path.read_text(encoding="utf-8").splitlines() if not line.startswith("#")
    ]
    return {row["ticker"]: row["currency"] for row in csv.DictReader(lines)}


@dataclass(frozen=True)
class ClosesExport:
    path: Path
    sessions_path: Path
    events_path: Path
    manifest_path: Path
    sha256: str
    rows: int
    sessions: int
    events: int
    last_session: str | None
    tickers: dict[str, dict]


def export_closes(
    store: Store,
    tickers: list[str] | None = None,
    now: datetime | None = None,
    calendar: pd.DataFrame | None = None,
    currencies: dict[str, str] | None = None,
    overrides: list[DividendOverride] | None = None,
) -> ClosesExport:
    """Writes the closing prices of the curated tickers (all, or the given ones), the sessions they
    cover, the funds' dividends and splits, and the manifest. Either all four files are complete or
    the old ones stay: a refused ticker stops the export.

    A bar is exported only if its session was over when the ticker was fetched (`promoted_at` of the
    curated manifest). A bar on a day that is not a session of the calendar is refused - the
    platform would take that day for a session of every fund.
    """
    manifest = store.read_manifest()
    # Exchange rates are not instruments (the platform takes its rate from the Bank of Israel).
    chosen = sorted(tickers if tickers is not None else [t for t in manifest if "=" not in t])
    if currencies is None:
        currencies = catalogue_currencies()
    if overrides is None:
        overrides = load_dividend_overrides()
    unknown = [t for t in chosen if t not in manifest]
    if unknown:
        raise ClosesError(f"{unknown[0]}: no curated data")
    fetched = {t: datetime.fromisoformat(manifest[t]["promoted_at"]) for t in chosen}
    series = {
        t: store.read_bars(t).drop(columns="ticker").sort_values("date").reset_index(drop=True)
        for t in chosen
    }
    if calendar is None:
        calendar = _calendar(series, fetched)
    all_sessions = [d.date().isoformat() for d in pd.to_datetime(calendar["session"])]
    is_session = set(all_sessions)

    lines = [HEADER]
    event_lines = [EVENTS_HEADER]
    summary: dict[str, dict] = {}
    for ticker in chosen:
        bars = series[ticker]
        finished = finished_sessions(calendar, fetched[ticker])
        through = finished[-1] if finished else ""
        closes = as_traded_closes(bars, ticker)
        kept = [(session, close) for session, close in closes if session <= through]
        stray = [session for session, _ in kept if session not in is_session]
        if stray:
            raise ClosesError(
                f"{ticker}: a bar on {stray[0]}, which is not a session of {CALENDAR}"
                + (f" (and {len(stray) - 1} more)" if len(stray) > 1 else "")
            )
        if not kept:
            raise ClosesError(f"{ticker}: no finished session among its bars")
        have = {session for session, _ in kept}
        lines.extend(f"{ticker},{session},{close}" for session, close in kept)
        # What the fund did on the sessions that are exported - an event of a bar left out waits
        # with its bar.
        corrected, corrections = with_known_corrections(bars, ticker, overrides)
        events = [e for e in fund_events(corrected, ticker) if e[0] in have]
        event_lines.extend(f"{ticker},{day},{kind},{value}" for day, kind, value in events)
        split_days = bars.loc[bars["splits"].fillna(0.0) != 0, ["date", "splits"]]
        summary[ticker] = {
            "rows": len(kept),
            "first": kept[0][0],
            "last": kept[-1][0],
            "raw_sha256": manifest[ticker].get("raw_sha256"),
            "fetched_at": fetched[ticker].astimezone(UTC).isoformat(timespec="seconds"),
            "finished_through": through,
            "unfinished_dropped": len(closes) - len(kept),
            "currency": currencies.get(ticker),
            "events": len(events),
            "dividends_corrected": [d for d in corrections if d in have],
            "splits": {
                d.date().isoformat(): float(r)
                for d, r in zip(
                    pd.to_datetime(split_days["date"]), split_days["splits"], strict=True
                )
                if d.date().isoformat() <= through
            },
            "missing_sessions": [
                s for s in all_sessions if kept[0][0] <= s <= kept[-1][0] and s not in have
            ],
        }

    # The sessions the prices cover: from the first bar of any ticker to the last session that was
    # over at the latest fetch - also a session for which the vendor gave nobody a price.
    first_session = min((t["first"] for t in summary.values()), default=None)
    last_session = max((t["finished_through"] for t in summary.values()), default=None)
    sessions = [s for s in all_sessions if first_session and first_session <= s <= last_session]

    folder = store.derived_dir / "closing-prices"
    folder.mkdir(parents=True, exist_ok=True)
    data = ("\n".join(lines) + "\n").encode("utf-8")
    sessions_data = ("\n".join([SESSIONS_HEADER, *sessions]) + "\n").encode("utf-8")
    events_data = ("\n".join(event_lines) + "\n").encode("utf-8")
    sha256 = hashlib.sha256(data).hexdigest()
    path, sessions_path = folder / CSV_NAME, folder / SESSIONS_NAME
    events_path, manifest_path = folder / EVENTS_NAME, folder / MANIFEST_NAME
    _replace(path, data)
    _replace(sessions_path, sessions_data)
    _replace(events_path, events_data)
    # The manifest is written last: a reader that finds a hash that does not match a table knows it
    # caught the export in the middle, or that somebody touched the table.
    _replace(
        manifest_path,
        json.dumps(
            {
                "file": CSV_NAME,
                "sha256": sha256,
                "rows": len(lines) - 1,
                "source": "yfinance",
                "price": "close as traded on the session (the vendor's split adjustment undone): "
                "a whole cent, a fraction of a dollar as quoted before 2001, or 3-4 decimals",
                "calendar": CALENDAR,
                "sessions_file": SESSIONS_NAME,
                "sessions_sha256": hashlib.sha256(sessions_data).hexdigest(),
                "sessions": len(sessions),
                "events_file": EVENTS_NAME,
                "events_sha256": hashlib.sha256(events_data).hexdigest(),
                "events": len(event_lines) - 1,
                "first_session": first_session,
                "last_session": last_session,
                "exported_at": (now or datetime.now(UTC)).isoformat(timespec="seconds"),
                "tickers": summary,
            },
            indent=2,
            sort_keys=True,
        ).encode("utf-8")
        + b"\n",
    )
    return ClosesExport(
        path,
        sessions_path,
        events_path,
        manifest_path,
        sha256,
        len(lines) - 1,
        len(sessions),
        len(event_lines) - 1,
        last_session,
        summary,
    )


def _calendar(series: dict[str, pd.DataFrame], fetched: dict[str, datetime]) -> pd.DataFrame:
    """The exchange's sessions from the first bar of any ticker to the day of the latest fetch."""
    from itrade.data.calendar import session_table

    firsts = [pd.to_datetime(b["date"]).min() for b in series.values() if not b.empty]
    if not firsts or not fetched:
        return pd.DataFrame(
            {"session": pd.to_datetime([]), "close_utc": pd.to_datetime([], utc=True)}
        )
    # A day past the latest fetch: the calendar wants a stretch, and a fund fetched on the day of
    # its first bar has none. A session that has not ended is not exported in any case.
    last = pd.Timestamp(max(fetched.values()).date()) + pd.Timedelta(days=1)
    return session_table(CALENDAR, str(min(firsts).date()), str(last.date()))


def _replace(path: Path, data: bytes) -> None:
    """Writes the whole file under another name first, so a reader never sees half of it."""
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_bytes(data)
    temporary.replace(path)
