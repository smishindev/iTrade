"""Reference calculation of portfolio returns (roadmap P3.4.02).

The platform computes returns in C# (`ITrade.Domain.PeriodReturns`). This module is its oracle: the
same definitions worked out another way - exact fractions for the time-weighted return, the textbook
annual-rate equation (what a spreadsheet calls XIRR) for the money-weighted one - so that the two
can be held against each other on synthetic flows. Nothing here reads the owner's books.

Timing, the same on both sides: a contribution counts from the start of its day (the end of the day
before), a withdrawal at the end of its day.
"""

from __future__ import annotations

import json
import random
from dataclasses import dataclass
from datetime import date, timedelta
from decimal import Decimal
from fractions import Fraction
from pathlib import Path

DAYS_IN_A_YEAR = 365


@dataclass(frozen=True)
class Point:
    """The value at the end of a day, with the money that came in and went out on that day."""

    day: date
    value: Decimal
    inflow: Decimal = Decimal(0)
    outflow: Decimal = Decimal(0)


@dataclass(frozen=True)
class Returns:
    invested_since: date | None
    days_at_work: int
    gained_on_nothing: Decimal
    time_weighted: Fraction
    time_weighted_annual: float | None
    money_weighted: float | None
    money_weighted_annual: float | None
    gain: Decimal


def returns(points: list[Point]) -> Returns:
    opening, closing = points[0], points[-1]
    growth = Fraction(1)
    since = opening.day if opening.value > 0 else None
    days = 0  # on which there was money in the portfolio
    on_nothing = Decimal(0)  # what turned up while there was none: a gain, in no return
    for before, point in zip(points, points[1:], strict=False):
        at_work = Fraction(before.value) + Fraction(point.inflow)
        if at_work > 0:
            growth *= (Fraction(point.value) + Fraction(point.outflow)) / at_work
            since = since or before.day
            days += (point.day - before.day).days
        else:
            on_nothing += point.value + point.outflow
    gain = (
        closing.value
        + sum(p.outflow for p in points)
        - opening.value
        - sum(p.inflow for p in points)
    )
    if since is None:
        return Returns(None, 0, on_nothing, growth - 1, None, None, None, gain)

    a_year_or_more = days >= DAYS_IN_A_YEAR
    annual = money_weighted_annual_rate(points)
    # Across a stretch in which the portfolio stood empty a rate is not carried: the money was out.
    stood_empty = days < (closing.day - since).days
    over_the_period = annual is not None and not stood_empty
    return Returns(
        since,
        days,
        on_nothing,
        growth - 1,
        float(growth) ** (DAYS_IN_A_YEAR / days) - 1 if a_year_or_more else None,
        (1 + annual) ** (days / DAYS_IN_A_YEAR) - 1 if over_the_period else None,
        annual if a_year_or_more else None,
        gain,
    )


def money_weighted_annual_rate(points: list[Point]) -> float | None:
    """The annual rate r with sum(amount / (1 + r) ** (days / 365)) = 0: money in positive, money
    out and the value left negative. None when no rate does it."""
    opening, closing = points[0], points[-1]
    flows: list[tuple[date, float]] = []
    if opening.value > 0:
        flows.append((opening.day, float(opening.value)))
    for before, point in zip(points, points[1:], strict=False):
        if point.inflow > 0:
            flows.append((before.day, float(point.inflow)))
        if point.outflow > 0:
            flows.append((point.day, -float(point.outflow)))
    if closing.value > 0:
        flows.append((closing.day, -float(closing.value)))
    if not any(amount > 0 for _, amount in flows):
        return None
    if not any(amount < 0 for _, amount in flows):
        return -1.0  # everything paid in is gone

    first = min(day for day, _ in flows)
    years = [((day - first).days / DAYS_IN_A_YEAR, amount) for day, amount in flows]

    def total(rate: float) -> float:
        return sum(amount / (1 + rate) ** t for t, amount in years)

    low, high = -0.999999, 1.0
    while total(high) < 0 and high < 1e12:  # a rate above 100% a year
        high *= 10
    if total(low) > 0 or total(high) < 0:
        return None
    for _ in range(400):
        middle = (low + high) / 2
        if total(middle) > 0:
            high = middle
        else:
            low = middle
    return (low + high) / 2


# ---------------------------------------------------------------------------------------------
# Synthetic flows for the fixture the C# tests read
# ---------------------------------------------------------------------------------------------


def _cents(value: float) -> Decimal:
    return Decimal(round(value * 100)) / 100


def synthetic(seed: int, months: int, withdraw: bool) -> list[Point]:
    """Monthly contributions on the first of the month and a value at the end of each month, with
    the day before every contribution in between; now and then a withdrawal in the middle."""
    rng = random.Random(seed)
    day = date(2023, 12, 31)
    points = [Point(day, _cents(rng.choice([0, 0, 2500, 10000])))]
    value = float(points[0].value)
    for _ in range(months):
        first = day + timedelta(days=1)
        paid = _cents(rng.choice([0, 500, 1000, 1000, 2500]))
        value = (value + float(paid)) * (1 + rng.gauss(0.0002, 0.01))
        points.append(Point(first, _cents(value), inflow=paid))
        value = float(points[-1].value)
        if withdraw and rng.random() < 0.25 and value > 100:
            middle = first + timedelta(days=14)
            value *= 1 + rng.gauss(0.003, 0.03)
            taken = _cents(value * rng.uniform(0.05, 0.3))
            points.append(Point(middle, _cents(value) - taken, outflow=taken))
            value = float(points[-1].value)
        end = (first.replace(day=28) + timedelta(days=4)).replace(day=1) - timedelta(days=1)
        value *= 1 + rng.gauss(0.006, 0.04)
        points.append(Point(end, _cents(value)))
        value, day = float(points[-1].value), end
    return points


def _d(year: int, month: int, day: int) -> date:
    return date(year, month, day)


def cases() -> dict[str, list[Point]]:
    """Named synthetic periods: the ones worth reading by hand first, then generated ones."""
    c = Decimal
    named = {
        "one contribution that grows": [
            Point(_d(2025, 12, 31), c(0)),
            Point(_d(2026, 1, 1), c(1000), inflow=c(1000)),
            Point(_d(2026, 6, 30), c(1100)),
        ],
        "two contributions, ten percent after each": [
            Point(_d(2025, 12, 31), c(0)),
            Point(_d(2026, 1, 1), c(1000), inflow=c(1000)),
            Point(_d(2026, 3, 31), c(1100)),
            Point(_d(2026, 4, 1), c(2100), inflow=c(1000)),
            Point(_d(2026, 6, 30), c(2310)),
        ],
        "a large contribution just before a fall": [
            Point(_d(2024, 12, 31), c(1000)),
            Point(_d(2025, 6, 30), c(1200)),
            Point(_d(2025, 7, 1), c(11200), inflow=c(10000)),
            Point(_d(2026, 6, 30), c(10080)),
        ],
        "a withdrawal in the middle": [
            Point(_d(2024, 12, 31), c(5000)),
            Point(_d(2025, 9, 15), c("5200.50"), outflow=c(800)),
            Point(_d(2026, 3, 31), c("5512.53")),
        ],
        "everything is lost": [
            Point(_d(2025, 12, 31), c(0)),
            Point(_d(2026, 1, 1), c(1000), inflow=c(1000)),
            Point(_d(2026, 2, 1), c(0)),
        ],
        "no flows at all, two years": [
            Point(_d(2023, 12, 31), c(10000)),
            Point(_d(2025, 12, 31), c(12100)),
        ],
        "empty at first, money comes later": [
            Point(_d(2025, 12, 31), c(0)),
            Point(_d(2026, 2, 28), c(0)),
            Point(_d(2026, 3, 1), c(3000), inflow=c(3000)),
            Point(_d(2026, 3, 31), c("2940.75")),
        ],
        "all of it taken out, then paid in again": [
            Point(_d(2025, 12, 31), c(2000)),
            Point(_d(2026, 1, 31), c(0), outflow=c(2100)),
            Point(_d(2026, 4, 30), c(0)),
            Point(_d(2026, 5, 1), c(1500), inflow=c(1500)),
            Point(_d(2026, 6, 30), c(1530)),
        ],
        "never any money": [
            Point(_d(2025, 12, 31), c(0)),
            Point(_d(2026, 6, 30), c(0)),
        ],
        "a dividend after everything was taken out": [
            Point(_d(2025, 12, 31), c(4000)),
            Point(_d(2026, 1, 31), c(0), outflow=c(4100)),
            Point(_d(2026, 2, 10), c("3.20")),
            Point(_d(2026, 6, 30), c("3.20")),
        ],
        "more than a year at work around an empty quarter": [
            Point(_d(2024, 6, 30), c(1000)),
            Point(_d(2025, 3, 31), c(0), outflow=c(1100)),
            Point(_d(2025, 6, 30), c(0)),
            Point(_d(2025, 7, 1), c(1000), inflow=c(1000)),
            Point(_d(2026, 3, 31), c(1050)),
        ],
    }
    for seed in range(12):
        months = 4 + seed * 3
        named[f"generated {seed}: {months} months"] = synthetic(
            seed, months, withdraw=seed % 2 == 1
        )
    return named


def fixture() -> str:
    """The cases with what the oracle says about them, as the JSON the C# tests read."""

    def number(value: float | None) -> str | None:
        return None if value is None else format(value, ".12f")

    out = []
    for name, points in cases().items():
        result = returns(points)
        exact = result.time_weighted
        out.append(
            {
                "name": name,
                "points": [
                    {
                        "date": p.day.isoformat(),
                        "value": str(p.value),
                        "inflow": str(p.inflow),
                        "outflow": str(p.outflow),
                    }
                    for p in points
                ],
                "expected": {
                    "invested_since": result.invested_since and result.invested_since.isoformat(),
                    "days_at_work": result.days_at_work,
                    "gained_on_nothing": str(result.gained_on_nothing),
                    # Exact as a fraction; the decimal is cut where a C# decimal still agrees.
                    "time_weighted": format(
                        Decimal(exact.numerator) / Decimal(exact.denominator), ".20f"
                    ),
                    "time_weighted_annual": number(result.time_weighted_annual),
                    "money_weighted": number(result.money_weighted),
                    "money_weighted_annual": number(result.money_weighted_annual),
                    "gain": str(result.gain),
                },
            }
        )
    return json.dumps({"days_in_a_year": DAYS_IN_A_YEAR, "cases": out}, indent=2) + "\n"


def fixture_path() -> Path:
    from itrade.config import project_root

    return project_root() / "tests" / "ITrade.UnitTests" / "Fixtures" / "returns-oracle.json"


__all__ = ["Point", "Returns", "cases", "fixture", "money_weighted_annual_rate", "returns"]
