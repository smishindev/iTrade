"""config/instruments.csv (roadmap P3.1.01) agrees with the universe files it is generated from."""

from __future__ import annotations

import csv
import tomllib

from itrade.config import project_root

CATALOGUE = project_root() / "config" / "instruments.csv"
UNIVERSES = project_root() / "config" / "universes"


def catalogue_rows() -> list[dict[str, str]]:
    lines = [
        line
        for line in CATALOGUE.read_text(encoding="utf-8").splitlines()
        if line and not line.startswith("#")
    ]
    return list(csv.DictReader(lines))


def universe_candidates() -> dict[str, dict[str, str]]:
    found: dict[str, dict[str, str]] = {}
    for path in sorted(UNIVERSES.glob("*.toml")):
        with path.open("rb") as f:
            universe = tomllib.load(f)
        for item in universe.get("candidates", []):
            found.setdefault(item["ticker"], {**item, "currency": universe["universe"]["currency"]})
    return found


def test_catalogue_lists_every_universe_candidate_once():
    rows = catalogue_rows()
    tickers = [row["ticker"] for row in rows]

    assert sorted(tickers) == sorted(universe_candidates())
    assert len(set(tickers)) == len(tickers)
    assert tickers == sorted(tickers)


def test_catalogue_rows_agree_with_the_universe_and_carry_their_identifiers():
    candidates = universe_candidates()

    for row in catalogue_rows():
        candidate = candidates[row["ticker"]]
        assert row["exchange"] == candidate["listing"], row["ticker"]
        assert row["currency"] == candidate["currency"], row["ticker"]
        assert row["name"] == candidate["name"], row["ticker"]
        assert len(row["isin"]) == 12, row["ticker"]
        assert row["ibkr_con_id"].isdigit(), row["ticker"]

    con_ids = [row["ibkr_con_id"] for row in catalogue_rows()]
    assert len(set(con_ids)) == len(con_ids)


def test_catalogue_is_a_note_then_a_header_with_unix_line_endings():
    raw = CATALOGUE.read_bytes()

    assert raw.startswith(b"# Instrument catalogue.")
    assert b"\r\n" not in raw
    assert raw.splitlines()[1] == b"ticker,exchange,currency,name,isin,ibkr_con_id"
