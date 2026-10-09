"""The reference indicator vectors (spec §14) are generated, consistent and unchanged."""

from __future__ import annotations

import csv
import importlib.util
from decimal import Decimal
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "indicators"
NAMES = ["rising", "falling", "gap"]


def _generator():
    spec = importlib.util.spec_from_file_location(
        "vectors", ROOT / "tools" / "make_indicator_vectors.py"
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.mark.parametrize("name", NAMES)
def test_fixture_matches_generator(name):
    gen = _generator()
    expected = gen.render(gen.make_series()[name])
    actual = (FIXTURES / f"{name}.csv").read_text(encoding="utf-8")
    assert actual == expected, f"{name}.csv differs: rerun `python tools/make_indicator_vectors.py`"


@pytest.mark.parametrize("name", NAMES)
def test_fixture_bars_are_consistent(name):
    with (FIXTURES / f"{name}.csv").open(encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    assert len(rows) == 20
    for r in rows:
        o, h, lo, c = (Decimal(r[k]) for k in ("open", "high", "low", "close"))
        assert h >= max(o, c) and lo <= min(o, c), r["date"]


def test_hand_checked_values():
    """Values verified by hand in the spec §14 step table."""
    with (FIXTURES / "rising.csv").open(encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    assert rows[1]["rsi2"] == ""  # needs n + 1 = 3 closes
    assert Decimal(rows[2]["rsi2"]) == Decimal("66.6666666667")
    assert Decimal(rows[3]["rsi2"]) == Decimal("88.8888888889")
    assert rows[13]["atr14"] == ""  # needs 14 true ranges = 15 bars
    assert Decimal(rows[14]["atr14"]) == Decimal("2.0142857143")
