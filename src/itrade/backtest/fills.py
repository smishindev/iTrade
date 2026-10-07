"""Fill model of the backtest (spec §6.1–§6.2), on raw (traded) prices.

Spread and slippage are NOT built into fill prices: they are charged by the cost model (§6.3),
so each is counted exactly once. Within a session the order is: open (exits, then entries),
then intraday stops — an entry at the open always happens before that day's low.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import ROUND_HALF_EVEN, Decimal

PRICE_STEP = Decimal("0.0001")


@dataclass(frozen=True)
class RawBar:
    open: Decimal
    high: Decimal
    low: Decimal
    close: Decimal


def to_price(value: float) -> Decimal:
    """Float vendor price -> Decimal rounded to 1/100 cent (half-even)."""
    return Decimal(repr(float(value))).quantize(PRICE_STEP, rounding=ROUND_HALF_EVEN)


def raw_bar(open_: float, high: float, low: float, close: float, scale: float) -> RawBar:
    """Split-adjusted vendor bar x S(d) -> the prices that actually traded on d (spec §1)."""
    return RawBar(*(to_price(v * scale) for v in (open_, high, low, close)))


@dataclass(frozen=True)
class Fill:
    price: Decimal
    reason: str


def fill_entry_loo(limit: Decimal, bar: RawBar) -> Fill | None:
    """Limit-on-open buy: filled at the open if the open is not above the limit."""
    if bar.open <= limit:
        return Fill(bar.open, "entry")
    return None  # limit_not_reached


def fill_stop(stop: Decimal, bar: RawBar) -> Fill | None:
    """Stop-market sell: a gap through the stop fills at the open, otherwise at the stop when the
    low reaches it. Also valid on the entry day, after the entry at the open."""
    if bar.open <= stop:
        return Fill(bar.open, "stop_gap")
    if bar.low <= stop:
        return Fill(stop, "stop")
    return None


def fill_open_exit(stop: Decimal, bar: RawBar, exit_reason: str) -> Fill:
    """A market-on-open exit and the protective stop share an OCA group: exactly one sale at the
    open. If the open is at or below the stop, the stop is the reason (spec §6.2)."""
    if bar.open <= stop:
        return Fill(bar.open, "stop_gap")
    return Fill(bar.open, exit_reason)
