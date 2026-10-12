"""The reference calculation of returns the platform's C# code is held against (P3.4.02)."""

from __future__ import annotations

import os
from datetime import date
from decimal import Decimal
from fractions import Fraction

import pytest

from itrade.accounting.returns import Point, cases, fixture, fixture_path, returns


def test_two_contributions_with_ten_percent_after_each():
    # 1,000 on 1 January grows to 1,100; another 1,000 on 1 April; all of it grows 10% again.
    result = returns(cases()["two contributions, ten percent after each"])

    assert result.time_weighted == Fraction(
        21, 100
    )  # 1.1 x 1.1: when the money came in is no matter
    assert result.gain == Decimal(310)  # 2,310 left of 2,000 paid in
    assert result.invested_since == date(2025, 12, 31)
    # Each thousand earned 10% a quarter for as long as it was in: one rate explains it all, and
    # over the half year it comes to (almost) the same 21%. 1,000 x 1.21 + 1,000 x 1.1 = 2,310.
    assert result.money_weighted == pytest.approx(0.209602, abs=1e-6)
    daily = (1 + result.money_weighted) ** (1 / 181)
    assert 1000 * daily**181 + 1000 * daily**91 == pytest.approx(2310, abs=1e-6)
    assert (
        result.money_weighted_annual is None and result.time_weighted_annual is None
    )  # half a year


def test_money_weighted_return_is_the_rate_that_explains_the_flows():
    points = cases()["a large contribution just before a fall"]
    result = returns(points)

    # 1,000 grew 20% in half a year, then 10,000 came in and everything fell 10% in a year.
    assert result.time_weighted == Fraction(108, 100) - 1  # 1.2 x 0.9
    assert result.time_weighted_annual == pytest.approx(1.08 ** (365 / 546) - 1, abs=1e-12)
    # Most of the money was there only for the fall.
    rate = result.money_weighted_annual
    assert rate == pytest.approx(-0.080239, abs=1e-6)
    left = 1000 * (1 + rate) ** (546 / 365) + 10000 * (1 + rate) ** (365 / 365)
    assert left == pytest.approx(10080, abs=1e-6)


def test_with_one_contribution_both_returns_are_the_same():
    result = returns(cases()["one contribution that grows"])

    assert result.time_weighted == Fraction(1, 10)
    assert result.money_weighted == pytest.approx(0.1, abs=1e-12)


def test_a_withdrawal_is_not_a_loss_and_a_contribution_is_not_a_gain():
    result = returns(cases()["a withdrawal in the middle"])

    # 5,000 became 6,000.50 by the day 800 were taken out (5,200.50 left), then grew 6%.
    assert (
        result.time_weighted
        == Fraction("6000.50") / 5000 * Fraction("5512.53") / Fraction("5200.50") - 1
    )
    assert result.gain == Decimal("1312.53")  # 5,512.53 + 800 - 5,000


def test_edges_have_an_answer_or_none():
    lost = returns(cases()["everything is lost"])
    assert (lost.time_weighted, lost.money_weighted) == (-1, -1.0)

    never = returns(cases()["never any money"])
    assert (never.invested_since, never.time_weighted, never.money_weighted) == (None, 0, None)

    later = returns(cases()["empty at first, money comes later"])
    assert later.invested_since == date(2026, 2, 28)  # the end of the day before the money came
    assert later.time_weighted == Fraction("2940.75") / 3000 - 1

    again = returns(cases()["all of it taken out, then paid in again"])
    assert again.time_weighted == Fraction(105, 100) * Fraction(102, 100) - 1
    # In for 31 + 61 of the 181 days: no rate is carried across the months the money was out.
    assert (again.days_at_work, again.money_weighted) == (92, None)

    late = returns(cases()["a dividend after everything was taken out"])
    assert (late.time_weighted, late.gained_on_nothing) == (Fraction(25, 1000), Decimal("3.20"))
    assert late.gain == Decimal("103.20")

    around = returns(cases()["more than a year at work around an empty quarter"])
    assert (around.days_at_work, around.money_weighted) == (548, None)
    assert around.money_weighted_annual == pytest.approx(0.1028, abs=1e-4)  # while invested


def test_generated_cases_are_what_they_claim():
    for name, points in cases().items():
        assert all(a.day < b.day for a, b in zip(points, points[1:], strict=False)), name
        assert all(p.value >= 0 and p.inflow >= 0 and p.outflow >= 0 for p in points), name
        for before, point in zip(points, points[1:], strict=False):
            if point.inflow > 0:
                assert (point.day - before.day).days == 1, name
    assert sum(any(p.outflow > 0 for p in c) for c in cases().values()) >= 5


def test_money_weighted_rate_needs_money_both_ways():
    opening = Point(date(2025, 12, 31), Decimal(0))
    assert returns([opening, Point(date(2026, 6, 30), Decimal(0))]).money_weighted is None


def test_fixture_the_platform_reads_is_what_the_oracle_says():
    # tests/ITrade.UnitTests/Fixtures/returns-oracle.json is read by the C# tests of PeriodReturns.
    # To write it anew after a deliberate change, run this test with ITRADE_UPDATE_FIXTURES=1.
    path = fixture_path()
    if os.environ.get("ITRADE_UPDATE_FIXTURES") == "1":
        path.write_bytes(fixture().encode("utf-8"))

    assert path.read_bytes() == fixture().encode("utf-8")
