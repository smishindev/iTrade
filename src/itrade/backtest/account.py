"""Simulated cash account (spec §5.1, §5.3 #6, §6.4, §6.5). All money and quantities are Decimal.

- Purchases are paid from **settled** cash only, minus reservations of pending entry orders.
- Sale proceeds settle after the historical US cycle (T+3 / T+2 / T+1) counted in XNYS sessions.
- One position per instrument (no averaging, spec §4.1 #4); one purchase lot per position.
- Cash can never go negative: every violation raises instead of being silently allowed.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from decimal import Decimal

import pandas as pd

from itrade.data.calendar import add_sessions

ZERO = Decimal("0")


class AccountError(RuntimeError):
    """An operation the cash account does not allow (insufficient settled cash, etc.)."""


@dataclass(frozen=True)
class SettlementRule:
    start: pd.Timestamp
    days: int


def settlement_rules(params: dict) -> list[SettlementRule]:
    rules = [
        SettlementRule(pd.Timestamp(r["from"]), int(r["days"]))
        for r in params["simulation"]["settlement"]
    ]
    return sorted(rules, key=lambda r: r.start)


def settlement_days(rules: Sequence[SettlementRule], trade_date: pd.Timestamp) -> int:
    applicable = [r for r in rules if r.start <= pd.Timestamp(trade_date)]
    if not applicable:
        raise AccountError(f"no settlement rule for {trade_date}")
    return applicable[-1].days


@dataclass
class Position:
    ticker: str
    qty: Decimal
    entry_date: pd.Timestamp
    entry_price: Decimal  # raw traded price
    stop: Decimal  # raw traded price
    entry_costs: Decimal
    dividends: Decimal = ZERO  # net cash dividends received while held


@dataclass(frozen=True)
class Closed:
    position: Position
    exit_date: pd.Timestamp
    exit_price: Decimal
    exit_costs: Decimal
    proceeds: Decimal  # net of exit costs, credited as unsettled cash


@dataclass
class Account:
    settled: Decimal
    sessions: pd.DatetimeIndex
    rules: list[SettlementRule]
    unsettled: list[tuple[pd.Timestamp, Decimal]] = field(default_factory=list)
    reservations: dict[str, Decimal] = field(default_factory=dict)
    positions: dict[str, Position] = field(default_factory=dict)

    # --- cash -------------------------------------------------------------------------------
    def settle(self, d: pd.Timestamp) -> None:
        """Move proceeds whose settlement date is <= d into settled cash."""
        d = pd.Timestamp(d)
        due = [a for when, a in self.unsettled if when <= d]
        self.unsettled = [(w, a) for w, a in self.unsettled if w > d]
        self.settled += sum(due, ZERO)

    @property
    def unsettled_total(self) -> Decimal:
        return sum((a for _, a in self.unsettled), ZERO)

    @property
    def reserved(self) -> Decimal:
        return sum(self.reservations.values(), ZERO)

    def available(self) -> Decimal:
        """Settled cash not yet promised to a pending entry order."""
        return self.settled - self.reserved

    def reserve(self, key: str, amount: Decimal) -> None:
        if key in self.reservations:
            raise AccountError(f"reservation {key!r} already exists")
        if amount <= ZERO:
            raise AccountError(f"reservation {key!r} must be positive")
        if amount > self.available():
            raise AccountError(f"reservation {key!r} {amount} exceeds available {self.available()}")
        self.reservations[key] = amount

    def release(self, key: str) -> Decimal:
        return self.reservations.pop(key, ZERO)

    def credit_dividend(self, ticker: str, amount: Decimal) -> None:
        """Net cash dividend (after withholding), treated as cash on the ex-date (spec §6.4)."""
        if amount < ZERO:
            raise AccountError("dividend cannot be negative")
        self.settled += amount
        if ticker in self.positions:
            self.positions[ticker].dividends += amount

    # --- trades -----------------------------------------------------------------------------
    def buy(
        self,
        ticker: str,
        qty: Decimal,
        price: Decimal,
        costs: Decimal,
        d: pd.Timestamp,
        stop: Decimal,
        reservation: str | None = None,
    ) -> Position:
        if ticker in self.positions:
            raise AccountError(f"{ticker}: already held (no averaging)")
        if qty <= ZERO or price <= ZERO or costs < ZERO:
            raise AccountError(f"{ticker}: invalid buy qty={qty} price={price} costs={costs}")
        if reservation is not None:
            self.release(reservation)
        debit = qty * price + costs
        if debit > self.available():
            raise AccountError(
                f"{ticker}: buy {debit} exceeds available settled cash {self.available()}"
            )
        self.settled -= debit
        pos = Position(ticker, qty, pd.Timestamp(d), price, stop, costs)
        self.positions[ticker] = pos
        return pos

    def sell_all(self, ticker: str, price: Decimal, costs: Decimal, d: pd.Timestamp) -> Closed:
        pos = self.positions.pop(ticker, None)
        if pos is None:
            raise AccountError(f"{ticker}: no position to sell")
        if price <= ZERO or costs < ZERO:
            raise AccountError(f"{ticker}: invalid sell price={price} costs={costs}")
        proceeds = pos.qty * price - costs
        try:
            settle_on = add_sessions(self.sessions, pd.Timestamp(d), settlement_days(self.rules, d))
        except ValueError:
            # Settlement falls after the calendar ends: counted in equity, never spendable.
            settle_on = pd.Timestamp.max
        self.unsettled.append((settle_on, proceeds))
        return Closed(pos, pd.Timestamp(d), price, costs, proceeds)

    def apply_split(self, ticker: str, ratio: Decimal) -> None:
        """Split (or vendor-encoded distribution) with ratio K: qty x K, prices / K (spec §6.4)."""
        pos = self.positions.get(ticker)
        if pos is None:
            return
        pos.qty *= ratio
        pos.entry_price /= ratio
        pos.stop /= ratio

    # --- valuation --------------------------------------------------------------------------
    def equity(self, prices: Mapping[str, Decimal]) -> Decimal:
        """E_t: all cash (settled + unsettled) + positions at the given raw prices (spec §5.1)."""
        held = sum((p.qty * prices[t] for t, p in self.positions.items()), ZERO)
        return self.settled + self.unsettled_total + held

    def invested(self, prices: Mapping[str, Decimal]) -> Decimal:
        return sum((p.qty * prices[t] for t, p in self.positions.items()), ZERO)
