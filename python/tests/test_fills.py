"""Fill model (spec §6.2): LOO entry, stop with gaps, MOO exit in OCA with the stop."""

from __future__ import annotations

from decimal import Decimal as D

import pytest

from itrade.backtest.fills import (
    Fill,
    RawBar,
    fill_entry_loo,
    fill_open_exit,
    fill_stop,
    raw_bar,
    to_price,
)


def bar(o, h, l, c) -> RawBar:  # noqa: E741
    return RawBar(D(o), D(h), D(l), D(c))


# --- entry (limit-on-open) -----------------------------------------------------------------------
def test_loo_fills_at_open_below_limit():
    assert fill_entry_loo(D("101.00"), bar("100.50", "102", "99", "101")) == Fill(
        D("100.50"), "entry"
    )


def test_loo_fills_at_open_equal_to_limit():
    assert fill_entry_loo(D("101.00"), bar("101.00", "102", "99", "101")) == Fill(
        D("101.00"), "entry"
    )


def test_loo_not_filled_when_open_gaps_above_limit_even_if_price_returns():
    # opened above the limit, then traded down through it: an opening-auction order is gone
    assert fill_entry_loo(D("101.00"), bar("101.01", "101.5", "99", "100")) is None


# --- protective stop ------------------------------------------------------------------------------
def test_stop_not_touched():
    assert fill_stop(D("95.00"), bar("100", "101", "95.01", "100")) is None


def test_stop_touched_intraday_fills_at_stop():
    assert fill_stop(D("95.00"), bar("100", "101", "94.00", "96")) == Fill(D("95.00"), "stop")


def test_stop_low_exactly_at_stop_fills():
    assert fill_stop(D("95.00"), bar("100", "101", "95.00", "96")) == Fill(D("95.00"), "stop")


def test_gap_through_stop_fills_at_the_worse_open():
    assert fill_stop(D("95.00"), bar("93.10", "94", "92", "93.5")) == Fill(D("93.10"), "stop_gap")


def test_entry_day_order_entry_first_then_stop():
    """Spec §6.1: buy at the open, then the day's low can hit the stop the same day."""
    day = bar("100.00", "100.20", "94.00", "95.50")
    entry = fill_entry_loo(D("101.00"), day)
    assert entry == Fill(D("100.00"), "entry")
    assert fill_stop(D("95.00"), day) == Fill(D("95.00"), "stop")


# --- exit at the open (OCA with the stop) ---------------------------------------------------------
@pytest.mark.parametrize("reason", ["exit_sma", "max_hold", "end_of_period"])
def test_open_exit_fills_at_open_with_its_reason(reason):
    assert fill_open_exit(D("95"), bar("102", "103", "101", "102.5"), reason) == Fill(
        D("102"), reason
    )


def test_open_exit_below_stop_is_reported_as_stop_gap_single_sale():
    assert fill_open_exit(D("95"), bar("94", "96", "93", "95"), "exit_sma") == Fill(
        D("94"), "stop_gap"
    )


# --- raw prices ----------------------------------------------------------------------------------
def test_raw_bar_applies_split_scale_and_rounds():
    b = raw_bar(15.355, 15.5, 15.2, 15.4049, scale=2.0)
    assert b == RawBar(D("30.7100"), D("31.0000"), D("30.4000"), D("30.8098"))
    assert to_price(30.709999999999997) == D("30.7100")
