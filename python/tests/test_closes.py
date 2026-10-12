"""Closing prices as they traded, exported for the platform (roadmap P3.3.02)."""

from __future__ import annotations

import hashlib
import json
import os
from datetime import UTC, datetime

import numpy as np
import pandas as pd
import pytest

from itrade.backtest import corporate_actions
from itrade.data.closes import (
    HEADER,
    ClosesError,
    as_traded_closes,
    export_closes,
    finished_sessions,
    price_text,
    split_scale,
)
from itrade.data.store import Store

# Fetched in the night after the session of 6 January 2026: both sessions the tests use are over.
AFTER_6_JANUARY = datetime(2026, 1, 7, 3, 0, tzinfo=UTC)


def bars(rows: list[tuple[str, float, float]]) -> pd.DataFrame:
    """(date, vendor close, split ratio on that date) -> bars as the pipeline stores them."""
    return pd.DataFrame(
        {
            "date": pd.to_datetime([r[0] for r in rows]),
            "close": [r[1] for r in rows],
            "splits": [r[2] for r in rows],
        }
    )


def test_split_adjustment_is_undone_for_the_days_before_the_split():
    # A fund closes at 289.99 and 291.07, splits 2:1, then closes at 146.60. The vendor halves the
    # two earlier closes (and stores them as 32-bit floats).
    table = bars(
        [
            ("2025-12-03", 144.994995, 0.0),
            ("2025-12-04", 145.535004, 0.0),
            ("2025-12-05", 146.600006, 2.0),
            ("2025-12-08", 147.630005, 0.0),
        ]
    )

    assert as_traded_closes(table, "XLK") == [
        ("2025-12-03", "289.99"),
        ("2025-12-04", "291.07"),
        ("2025-12-05", "146.60"),  # the split day already trades at the new price
        ("2025-12-08", "147.63"),
    ]


def test_several_splits_multiply_and_a_reverse_split_divides():
    # 3:1 twice, then 1:4 reverse. Scale on a day = product of the ratios after it.
    scales = [3 * 3 * 0.25, 3 * 0.25, 0.25, 1.0, 1.0]
    traded = [101.00, 33.5625, 45.10, 12.34, 12.50]  # 33 9/16: a price from before decimalisation
    table = bars(
        [
            # What the vendor shows: the traded price divided by the scale, as a 32-bit float.
            (day, float(np.float32(price / scale)), ratio)
            for day, price, scale, ratio in zip(
                ["2003-04-14", "2005-06-09", "2008-07-24", "2016-11-07", "2016-11-08"],
                traded,
                scales,
                [0.0, 3.0, 3.0, 0.25, 0.0],
                strict=True,
            )
        ]
    )

    assert [round(s, 4) for s in split_scale(table)] == [2.25, 0.75, 0.25, 1.0, 1.0]
    assert round(table["close"][0], 4) == 44.8889  # 101.00 / 2.25, nothing like the traded price
    assert [close for _, close in as_traded_closes(table, "T")] == [
        "101.00",
        "33.5625",
        "45.10",
        "12.34",
        "12.50",
    ]


def test_same_scale_as_the_research_engine():
    table = bars(
        [
            ("2020-01-02", 10.0, 0.0),
            ("2020-01-03", 11.0, 2.0),
            ("2020-01-06", 12.0, 0.0),
            ("2020-01-07", 13.0, 1.231),
            ("2020-01-08", 14.0, 0.25),
            ("2020-01-09", 15.0, 0.0),
        ]
    )

    assert list(split_scale(table)) == list(corporate_actions.split_scale(table))


@pytest.mark.parametrize(
    ("value", "text"),
    [
        (769.640015, "769.64"),  # the vendor's float for 769.64
        (289.98999, "289.99"),
        (100.0099983, "100.01"),  # 33.336666 x 3
        (146.6, "146.60"),
        (1234.5, "1234.50"),
        # Fractions of a dollar, as quoted until 2001: exact, however many decimals that takes.
        (95.5625, "95.5625"),  # 9/16
        (12.125, "12.125"),  # 1/8
        (6.28125, "6.28125"),  # 9/32 - at four decimals this is a midpoint that float noise flips
        (10.015625, "10.015625"),  # 1/64
        (95.56250763, "95.5625"),  # the same sixteenth after a split was undone
        # Three-decimal closes of thinly traded new funds, and not the nearest fraction or cent.
        (49.53200149536133, "49.532"),
        (30.344, "30.344"),  # 0.00025 from 30 11/32, and not that
        (30.341, "30.341"),
        (30.349, "30.349"),  # a tenth of a cent from 30.35, and not that
        (12.3457, "12.3457"),
        (0.0101, "0.0101"),
    ],
)
def test_price_is_read_as_what_it_was_quoted_in(value, text):
    assert price_text(value) == text


@pytest.mark.parametrize("value", [12.34567, 45.67895, 0.00001])
def test_value_that_is_no_quoted_price_is_refused_and_not_rounded(value):
    with pytest.raises(ClosesError, match="neither cents"):
        price_text(value)


def test_the_noise_of_a_float_at_a_high_price_is_still_read_as_cents():
    # A 32-bit float near 5,000 is 0.0005 apart from its neighbours; half of that is its error.
    assert price_text(float(np.float32(4999.99))) == "4999.99"
    assert price_text(float(np.float32(4999.99 / 3)) * 3) == "4999.99"


@pytest.mark.parametrize("value", [0.0, -1.0, float("nan"), float("inf")])
def test_what_is_not_a_price_is_refused(value):
    with pytest.raises(ClosesError, match="not a price"):
        price_text(value)


def test_recovered_price_does_not_depend_on_how_far_back_the_vendor_adjusted():
    # The same day before and after a new 3:1 split is known to the vendor: 898.37 as it is, and as
    # 299.456667 (a 32-bit float) to be multiplied by 3. The exported text must be the same, or a
    # split would look like a correction of the whole history.
    before = bars([("2026-01-05", float(np.float32(898.37)), 0.0)])
    after = bars([("2026-01-05", float(np.float32(898.37 / 3)), 0.0), ("2026-01-06", 300.0, 3.0)])

    assert (
        as_traded_closes(before, "T")[0]
        == as_traded_closes(after, "T")[0]
        == ("2026-01-05", "898.37")
    )


def test_bars_that_are_not_one_per_date_in_order_are_refused():
    with pytest.raises(ClosesError, match="not one per date"):
        as_traded_closes(bars([("2026-01-06", 1.0, 0.0), ("2026-01-05", 1.0, 0.0)]), "T")
    with pytest.raises(ClosesError, match="not one per date"):
        as_traded_closes(bars([("2026-01-05", 1.0, 0.0), ("2026-01-05", 1.0, 0.0)]), "T")
    with pytest.raises(ClosesError, match="T 2026-01-05: not a price"):
        as_traded_closes(bars([("2026-01-05", float("nan"), 0.0)]), "T")
    with pytest.raises(ClosesError, match="no bars"):
        as_traded_closes(bars([]), "T")
    with pytest.raises(ClosesError, match="split ratio"):
        as_traded_closes(bars([("2026-01-05", 1.0, -2.0)]), "T")


def store_with(tmp_path, series: dict[str, pd.DataFrame], fetched: datetime) -> Store:
    """A store holding these bars, each ticker fetched (promoted) at the given time."""
    store = Store(tmp_path)
    for ticker, table in series.items():
        full = table.assign(open=1.0, high=1.0, low=1.0, adj_close=1.0, volume=1.0, dividends=0.0)
        raw = store.write_raw("test", ticker, full)
        store.promote(ticker, raw, full)
    fetch(store, dict.fromkeys(series, fetched))
    return store


def fetch(store: Store, when: dict[str, datetime]) -> None:
    """Sets the time the tickers were fetched: the store itself stamps the moment of the run."""
    manifest = store.read_manifest()
    for ticker, moment in when.items():
        manifest[ticker]["promoted_at"] = moment.isoformat(timespec="seconds")
    store.manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")


def test_a_session_is_finished_half_an_hour_after_its_close():
    from itrade.data.calendar import session_table

    calendar = session_table("XNYS", "2026-01-02", "2026-01-07")

    def through(moment: datetime) -> str:
        return finished_sessions(calendar, moment)[-1]

    # 6 January 2026 closes at 16:00 in New York, 21:00 UTC.
    assert through(datetime(2026, 1, 6, 18, 0, tzinfo=UTC)) == "2026-01-05"  # the session is open
    assert (
        through(datetime(2026, 1, 6, 21, 29, tzinfo=UTC)) == "2026-01-05"
    )  # the auction, the print
    assert through(datetime(2026, 1, 6, 21, 30, tzinfo=UTC)) == "2026-01-06"
    # A half day closes at 13:00 in New York: 27 November 2026, the day after Thanksgiving.
    november = session_table("XNYS", "2026-11-24", "2026-11-30")
    assert finished_sessions(november, datetime(2026, 11, 27, 18, 30, tzinfo=UTC))[-2:] == [
        "2026-11-25",  # the 26th is the holiday
        "2026-11-27",
    ]


def test_export_writes_the_prices_their_sessions_and_a_manifest_that_vouches_for_both(tmp_path):
    store = store_with(
        tmp_path,
        {
            "VT": bars([("2026-01-05", 120.129997, 0.0), ("2026-01-06", 121.5, 0.0)]),
            "XLK": bars([("2026-01-05", 144.994995, 0.0), ("2026-01-06", 146.600006, 2.0)]),
            "ILS=X": bars([("2026-01-05", 3.1, 0.0)]),  # a rate, not an instrument: left out
        },
        fetched=AFTER_6_JANUARY,
    )

    export = export_closes(
        store, now=datetime(2026, 1, 7, 6, 0, tzinfo=UTC), currencies={"XLK": "USD"}
    )

    assert export.path == tmp_path / "derived" / "closing-prices" / "closing-prices.csv"
    data = export.path.read_bytes()
    assert (
        data
        == (
            f"{HEADER}\n"
            "VT,2026-01-05,120.13\n"
            "VT,2026-01-06,121.50\n"
            "XLK,2026-01-05,289.99\n"
            "XLK,2026-01-06,146.60\n"
        ).encode()
    )
    sessions = export.sessions_path.read_bytes()
    assert sessions == b"session\n2026-01-05\n2026-01-06\n"
    manifest = json.loads(export.manifest_path.read_text(encoding="utf-8"))
    assert manifest["sha256"] == hashlib.sha256(data).hexdigest() == export.sha256
    assert manifest["sessions_sha256"] == hashlib.sha256(sessions).hexdigest()
    assert {k: v for k, v in manifest.items() if k not in ("tickers", "price")} == {
        "file": "closing-prices.csv",
        "sha256": export.sha256,
        "rows": 4,
        "source": "yfinance",
        "calendar": "XNYS",
        "sessions_file": "closing-prices-sessions.csv",
        "sessions_sha256": manifest["sessions_sha256"],
        "sessions": 2,
        "first_session": "2026-01-05",
        "last_session": "2026-01-06",
        "exported_at": "2026-01-07T06:00:00+00:00",
    }
    assert manifest["tickers"]["XLK"] == {
        "rows": 2,
        "first": "2026-01-05",
        "last": "2026-01-06",
        "raw_sha256": store.read_manifest()["XLK"]["raw_sha256"],
        "fetched_at": "2026-01-07T03:00:00+00:00",
        "finished_through": "2026-01-06",
        "unfinished_dropped": 0,
        "currency": "USD",
        "splits": {"2026-01-06": 2.0},  # on that day the price rightly halves
        "missing_sessions": [],
    }
    assert manifest["tickers"]["VT"]["currency"] is None  # not in the catalogue given
    assert manifest["tickers"]["VT"]["splits"] == {}
    assert sorted(manifest["tickers"]) == ["VT", "XLK"]
    assert (export.rows, export.sessions, export.last_session) == (4, 2, "2026-01-06")
    # Exporting the same data again gives the same bytes: the platform sees nothing new.
    assert export_closes(store).sha256 == export.sha256


def test_a_bar_fetched_while_its_session_was_open_is_not_a_close(tmp_path):
    # VT was fetched in the middle of the session of 6 January: its last bar is the last trade so
    # far. XLK was fetched after that session. The sessions run to the last one anybody finished.
    store = store_with(
        tmp_path,
        {
            "VT": bars([("2026-01-05", 120.13, 0.0), ("2026-01-06", 119.02, 0.0)]),
            "XLK": bars([("2026-01-05", 144.994995, 0.0), ("2026-01-06", 146.6, 2.0)]),
        },
        fetched=AFTER_6_JANUARY,
    )
    fetch(store, {"VT": datetime(2026, 1, 6, 18, 0, tzinfo=UTC)})

    export = export_closes(store)

    lines = export.path.read_text(encoding="utf-8").splitlines()
    assert [line for line in lines if line.startswith("VT")] == ["VT,2026-01-05,120.13"]
    assert "XLK,2026-01-06,146.60" in lines
    vt = export.tickers["VT"]
    assert (vt["last"], vt["finished_through"], vt["unfinished_dropped"]) == (
        "2026-01-05",
        "2026-01-05",
        1,
    )
    assert export.sessions_path.read_text(encoding="utf-8").split() == [
        "session",
        "2026-01-05",
        "2026-01-06",
    ]
    assert export.last_session == "2026-01-06"

    # Nobody saw the end of that session: it is not in the export at all.
    fetch(store, {"XLK": datetime(2026, 1, 6, 21, 10, tzinfo=UTC)})
    early = export_closes(store)
    assert early.last_session == "2026-01-05"
    assert early.path.read_text(encoding="utf-8").splitlines()[1:] == [
        "VT,2026-01-05,120.13",
        # Before the split of the 6th is known to be final, the 5th is still told as it traded.
        "XLK,2026-01-05,289.99",
    ]
    assert early.tickers["XLK"]["splits"] == {}  # the split belongs to the session left out


def test_a_split_on_a_dropped_day_still_scales_the_days_before_it(tmp_path):
    # The vendor's closes are already halved for the split of the unfinished day; leaving the bar
    # out of the export must not leave its split out of the arithmetic.
    store = store_with(
        tmp_path,
        {"XLK": bars([("2026-01-05", 144.994995, 0.0), ("2026-01-06", 146.6, 2.0)])},
        fetched=datetime(2026, 1, 6, 15, 0, tzinfo=UTC),
    )

    export = export_closes(store)

    assert export.path.read_text(encoding="utf-8").splitlines()[1:] == ["XLK,2026-01-05,289.99"]


def test_a_session_nobody_has_a_bar_for_is_still_a_session(tmp_path):
    store = store_with(
        tmp_path,
        {"VT": bars([("2026-01-02", 119.5, 0.0), ("2026-01-06", 121.5, 0.0)])},
        fetched=AFTER_6_JANUARY,
    )

    export = export_closes(store)

    # The weekend of 3-4 January is no session; Monday the 5th is one, and its price is missing.
    assert export.sessions_path.read_text(encoding="utf-8").split()[1:] == [
        "2026-01-02",
        "2026-01-05",
        "2026-01-06",
    ]
    assert export.tickers["VT"]["missing_sessions"] == ["2026-01-05"]


def test_sessions_run_to_the_last_finished_one_also_when_no_bar_reached_it(tmp_path):
    # Fetched after the session of the 6th, and the vendor's last bar is of the 5th: the 6th was a
    # session all the same, and the platform must see that the fund is a session behind.
    store = store_with(
        tmp_path,
        {"VT": bars([("2026-01-02", 119.5, 0.0), ("2026-01-05", 120.13, 0.0)])},
        fetched=AFTER_6_JANUARY,
    )

    export = export_closes(store)

    assert export.sessions_path.read_text(encoding="utf-8").split()[1:] == [
        "2026-01-02",
        "2026-01-05",
        "2026-01-06",
    ]
    assert (export.last_session, export.tickers["VT"]["last"]) == ("2026-01-06", "2026-01-05")
    assert export.tickers["VT"]["finished_through"] == "2026-01-06"


def test_a_bar_on_a_day_the_exchange_was_closed_is_refused(tmp_path):
    store = store_with(
        tmp_path,
        {"VT": bars([("2026-01-02", 119.5, 0.0), ("2026-01-03", 119.6, 0.0)])},  # a Saturday
        fetched=AFTER_6_JANUARY,
    )

    with pytest.raises(ClosesError, match="VT: a bar on 2026-01-03, which is not a session"):
        export_closes(store)


def test_a_ticker_with_no_finished_session_is_refused(tmp_path):
    store = store_with(
        tmp_path,
        {"VT": bars([("2026-01-06", 121.5, 0.0)])},
        fetched=datetime(2026, 1, 6, 15, 0, tzinfo=UTC),
    )

    with pytest.raises(ClosesError, match="VT: no finished session"):
        export_closes(store)


def test_export_of_a_refused_ticker_leaves_the_old_files_alone(tmp_path):
    store = store_with(tmp_path, {"VT": bars([("2026-01-05", 120.13, 0.0)])}, AFTER_6_JANUARY)
    first = export_closes(store)
    files = (first.path, first.sessions_path, first.manifest_path)
    before = [f.read_bytes() for f in files]
    bad = bars([("2026-01-05", 120.13, 0.0), ("2026-01-06", -1.0, 0.0)])
    store_with(tmp_path, {"VT": bad}, AFTER_6_JANUARY)

    with pytest.raises(ClosesError, match="VT"):
        export_closes(store)

    assert [f.read_bytes() for f in files] == before


def test_currencies_come_from_the_platforms_catalogue():
    from itrade.data.closes import catalogue_currencies

    currencies = catalogue_currencies()

    assert currencies["VT"] == "USD"
    assert set(currencies.values()) == {"USD"}  # the day this fails, a price needs its currency


def fixture_store(tmp_path) -> Store:
    return store_with(
        tmp_path,
        {
            "VT": bars([("2026-01-05", 120.129997, 0.0), ("2026-01-06", 121.5, 0.0)]),
            "XLK": bars([("2026-01-05", 144.994995, 0.0), ("2026-01-06", 146.600006, 2.0)]),
            # A fund that starts later, with a three-decimal close and a session without a bar.
            "XLC": bars([("2025-12-30", 49.53200149536133, 0.0), ("2026-01-02", 49.75, 0.0)]),
            # Fractions of a dollar as quoted before 2001 (recent dates keep the calendar short).
            "QQQ": bars([("2025-12-29", 110.8125, 0.0), ("2025-12-30", 107.6875, 0.0)]),
        },
        fetched=AFTER_6_JANUARY,
    )


def test_fixture_the_platform_reads_is_what_the_exporter_writes(tmp_path):
    # tests/ITrade.UnitTests/Fixtures/closing-prices*.{csv,json} are read by the C# reader's test;
    # this test holds them to the exporter, so the two sides cannot drift apart unnoticed.
    # To write them anew after a deliberate change, run this test with ITRADE_UPDATE_FIXTURES=1.
    from itrade.config import project_root

    fixtures = project_root() / "tests" / "ITrade.UnitTests" / "Fixtures"

    export = export_closes(
        fixture_store(tmp_path),
        now=datetime(2026, 1, 7, 6, 0, tzinfo=UTC),
        currencies={"VT": "USD", "XLK": "USD", "XLC": "USD"},
    )

    if os.environ.get("ITRADE_UPDATE_FIXTURES") == "1":
        for written in (export.path, export.sessions_path, export.manifest_path):
            (fixtures / written.name).write_bytes(written.read_bytes())

    for written in (export.path, export.sessions_path):
        assert written.read_bytes() == (fixtures / written.name).read_bytes(), written.name
    # The manifest also names the hash of each raw snapshot, which is a Parquet file and so
    # changes with the library that writes it; everything else is held to the letter.
    assert told(export.manifest_path) == told(fixtures / export.manifest_path.name)


def told(manifest_path) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for ticker in manifest["tickers"].values():
        assert len(ticker.pop("raw_sha256")) == 64
    return manifest
