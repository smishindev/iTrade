"""Simulated cash account (spec §5.1, §5.3 #6, §6.4, §6.5)."""

from __future__ import annotations

from decimal import Decimal as D

import pandas as pd
import pytest

from itrade.backtest.account import (
    Account,
    AccountError,
    settlement_days,
    settlement_rules,
)
from itrade.config import load_strategy
from itrade.data.calendar import session_table

RULES = settlement_rules(load_strategy("etf_pullback_v1"))


@pytest.fixture(scope="module")
def sessions() -> pd.DatetimeIndex:
    return pd.DatetimeIndex(session_table("XNYS", "2010-01-01", "2025-12-31")["session"])


def account(sessions, cash="1000") -> Account:
    return Account(settled=D(cash), sessions=sessions, rules=RULES)


def test_historical_settlement_cycles():
    assert settlement_days(RULES, pd.Timestamp("2010-06-01")) == 3
    assert settlement_days(RULES, pd.Timestamp("2017-09-01")) == 3
    assert settlement_days(RULES, pd.Timestamp("2017-09-05")) == 2
    assert settlement_days(RULES, pd.Timestamp("2024-05-24")) == 2
    assert settlement_days(RULES, pd.Timestamp("2024-05-28")) == 1


def test_reservations_are_not_double_counted(sessions):
    a = account(sessions)
    a.reserve("i1", D("400"))
    a.reserve("i2", D("500"))
    assert a.available() == D("100")
    with pytest.raises(AccountError):
        a.reserve("i3", D("100.01"))
    with pytest.raises(AccountError):
        a.reserve("i1", D("1"))  # same key twice
    assert a.release("i1") == D("400") and a.available() == D("500")
    assert a.release("missing") == D("0")


def test_buy_consumes_its_reservation_and_debits_settled_cash(sessions):
    a = account(sessions)
    a.reserve("i1", D("305"))
    pos = a.buy("SPY", D("3"), D("100"), D("1.05"), sessions[10], D("95"), reservation="i1")
    assert a.settled == D("698.95") and a.reserved == D("0") and a.available() == D("698.95")
    assert pos.entry_costs == D("1.05") and pos.qty == D("3")


def test_cannot_buy_beyond_available_or_twice(sessions):
    a = account(sessions)
    a.reserve("other", D("900"))
    with pytest.raises(AccountError):
        a.buy("SPY", D("2"), D("100"), D("1"), sessions[10], D("95"))  # only 100 available
    a.release("other")
    a.buy("SPY", D("2"), D("100"), D("1"), sessions[10], D("95"))
    with pytest.raises(AccountError):
        a.buy("SPY", D("1"), D("100"), D("1"), sessions[11], D("95"))  # no averaging


def test_sale_proceeds_are_unsettled_until_settlement_date(sessions):
    # 2025: T+1. Sell on Friday 2025-11-28 (day after Thanksgiving) -> settles Monday 2025-12-01.
    a = account(sessions, cash="500")
    a.buy("XLU", D("5"), D("80"), D("1"), pd.Timestamp("2025-11-24"), D("75"))
    closed = a.sell_all("XLU", D("82"), D("1.10"), pd.Timestamp("2025-11-28"))
    assert closed.proceeds == D("408.90")
    assert a.settled == D("99") and a.available() == D("99")  # not usable on the sale day
    assert a.equity({}) == D("507.90")  # but counted in equity
    a.settle(pd.Timestamp("2025-11-28"))
    assert a.available() == D("99")
    a.settle(pd.Timestamp("2025-12-01"))
    assert a.available() == D("507.90") and a.unsettled == []


def test_t_plus_three_in_2010_skips_weekend(sessions):
    a = account(sessions)
    a.buy("EWZ", D("2"), D("70"), D("1"), pd.Timestamp("2010-03-01"), D("65"))
    a.sell_all("EWZ", D("72"), D("1"), pd.Timestamp("2010-03-04"))  # Thursday
    a.settle(pd.Timestamp("2010-03-08"))  # Monday = T+2
    assert a.unsettled_total == D("143")
    a.settle(pd.Timestamp("2010-03-09"))  # Tuesday = T+3
    assert a.unsettled_total == D("0")


def test_equity_and_invested_mark_to_market(sessions):
    a = account(sessions)
    a.buy("SPY", D("2"), D("100"), D("1"), sessions[5], D("95"))
    prices = {"SPY": D("110")}
    assert a.invested(prices) == D("220")
    assert a.equity(prices) == D("799") + D("220")


def test_dividend_credit_and_split(sessions):
    a = account(sessions)
    a.buy("XLF", D("10"), D("24"), D("1"), sessions[5], D("22"))
    a.credit_dividend("XLF", D("0.75"))
    assert a.positions["XLF"].dividends == D("0.75") and a.settled == D("759.75")
    a.apply_split("XLF", D("2"))
    p = a.positions["XLF"]
    assert (p.qty, p.entry_price, p.stop) == (D("20"), D("12"), D("11"))
    with pytest.raises(AccountError):
        a.credit_dividend("XLF", D("-1"))


def test_invalid_operations_raise(sessions):
    a = account(sessions)
    with pytest.raises(AccountError):
        a.sell_all("NONE", D("1"), D("0"), sessions[3])
    with pytest.raises(AccountError):
        a.buy("X", D("0"), D("10"), D("0"), sessions[3], D("9"))
    with pytest.raises(AccountError):
        a.reserve("z", D("0"))
