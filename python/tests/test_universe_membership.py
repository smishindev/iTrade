"""Universe membership by date (spec §3): no instrument joins before it qualifies, no look-ahead."""

from __future__ import annotations

import numpy as np
import pandas as pd

from itrade.backtest.universe import MembershipRules, members_by_year, membership, membership_for
from itrade.config import load_strategy, strategy_version

RULES = MembershipRules(
    min_history_sessions=30, min_avg_dollar_volume_usd=1_000_000, liquidity_window=5
)


def bars(n: int, price: float = 10.0, volume=100_000, start="2020-01-02") -> pd.DataFrame:
    dates = pd.bdate_range(start, periods=n)
    vol = np.full(n, volume, dtype=float) if np.isscalar(volume) else np.asarray(volume, float)
    return pd.DataFrame({"date": dates, "close": np.full(n, price), "volume": vol})


def test_history_rule_delays_membership():
    t = membership_for(bars(40), "X", RULES)  # $1M a day, liquid from bar 5
    first = t.loc[t["is_member"], "bars"].min()
    assert first == RULES.min_history_sessions
    assert not t.loc[t["bars"] < RULES.min_history_sessions, "is_member"].any()


def test_liquidity_rule_uses_trailing_window_only():
    volume = [50_000] * 35 + [200_000] * 15  # $0.5M/day, then $2M/day from bar 36
    t = membership_for(bars(50, volume=volume), "X", RULES)
    first_member_bar = t.loc[t["is_member"], "bars"].min()
    # ADV over bars 33..37 = (0.5+0.5+0.5+2+2)/5 = $1.1M -> first qualifying bar is 37.
    assert first_member_bar == 37


def test_future_data_does_not_change_past_membership():
    base = bars(60, volume=[50_000] * 45 + [200_000] * 15)
    t_full = membership_for(base, "X", RULES)
    cut = base.iloc[:40].copy()
    t_cut = membership_for(cut, "X", RULES)
    pd.testing.assert_frame_equal(t_full.iloc[:40].reset_index(drop=True), t_cut)

    mutated = base.copy()
    mutated.loc[45:, "volume"] = 1e12  # wildly different future
    t_mut = membership_for(mutated, "X", RULES)
    pd.testing.assert_frame_equal(t_full.iloc[:45], t_mut.iloc[:45])


def test_dates_without_bars_are_not_members():
    a = membership_for(bars(40), "A", RULES)
    b = membership_for(bars(10, start="2020-03-02"), "B", RULES)
    table = membership({"A": bars(40), "B": bars(10, start="2020-03-02")}, RULES)
    assert set(table["ticker"]) == {"A", "B"}
    assert len(table) == len(a) + len(b)
    assert not table.loc[table["ticker"] == "B", "is_member"].any()  # only 10 bars of history


def test_members_by_year_report():
    table = membership({"A": bars(40, start="2020-12-01")}, RULES)
    report = members_by_year(table)
    assert list(report["year"]) == [2020, 2021]
    assert report.loc[report["year"] == 2021, "joined"].item() == "A"


def test_rules_and_version_from_config():
    params = load_strategy("etf_pullback_v1")
    rules = MembershipRules.from_strategy(params)
    assert rules == MembershipRules(756, 50_000_000.0, 20)
    # Pinned on purpose: changing parameters or universe must be a conscious new version.
    assert strategy_version("etf_pullback_v1") == (
        "843f5684df30daa673a77cc5d3e2c5da1bc9baba32ac72290a3aba465577eba9"
    )


def test_empty_input_gives_empty_table():
    assert membership({}, RULES).empty
