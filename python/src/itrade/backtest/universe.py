"""Universe membership by date (spec §3).

An instrument is a member on session t when, using only bars dated <= t:
  1. the snapshot holds at least `min_history_sessions` bars of it, and
  2. its average dollar volume over the last `liquidity_window` bars >= `min_avg_dollar_volume_usd`.
Condition 3 of the spec (no failed quality check / no stop after a conflicting corporate action)
is enforced upstream: only curated bars reach this module; corporate-action stops arrive in P1.A.08.
Dates on which an instrument has no bar are not memberships.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass

import pandas as pd

MEMBERSHIP_COLUMNS = ["date", "ticker", "bars", "adv", "is_member"]


@dataclass(frozen=True)
class MembershipRules:
    min_history_sessions: int
    min_avg_dollar_volume_usd: float
    liquidity_window: int

    @classmethod
    def from_strategy(cls, params: dict) -> MembershipRules:
        return cls(
            min_history_sessions=int(params["universe_rules"]["min_history_sessions"]),
            min_avg_dollar_volume_usd=float(params["universe_rules"]["min_avg_dollar_volume_usd"]),
            liquidity_window=int(params["indicators"]["liquidity_window"]),
        )


def membership_for(bars: pd.DataFrame, ticker: str, rules: MembershipRules) -> pd.DataFrame:
    """Membership of one instrument on every date it has a bar. `bars` needs date, close, volume."""
    df = bars.sort_values("date").reset_index(drop=True)
    history = pd.Series(range(1, len(df) + 1), index=df.index)  # bars dated <= t
    dollar_volume = df["close"] * df["volume"]  # split-adjusted price x volume = raw dollars
    adv = dollar_volume.rolling(rules.liquidity_window, min_periods=rules.liquidity_window).mean()
    is_member = (history >= rules.min_history_sessions) & (adv >= rules.min_avg_dollar_volume_usd)
    return pd.DataFrame(
        {
            "date": pd.to_datetime(df["date"]),
            "ticker": ticker,
            "bars": history,
            "adv": adv,
            "is_member": is_member.fillna(False).astype(bool),
        }
    )[MEMBERSHIP_COLUMNS]


def membership(bars_by_ticker: Mapping[str, pd.DataFrame], rules: MembershipRules) -> pd.DataFrame:
    frames = [membership_for(df, ticker, rules) for ticker, df in bars_by_ticker.items()]
    if not frames:
        return pd.DataFrame(columns=MEMBERSHIP_COLUMNS)
    return pd.concat(frames, ignore_index=True).sort_values(["date", "ticker"], ignore_index=True)


def members_by_year(table: pd.DataFrame) -> pd.DataFrame:
    """Per calendar year: members on the first session, max daily count, tickers that joined."""
    members = table[table["is_member"]]
    first_join = members.groupby("ticker")["date"].min()
    rows = []
    for year, chunk in table.groupby(table["date"].dt.year):
        first_day = chunk["date"].min()
        daily = chunk[chunk["is_member"]].groupby("date")["ticker"].nunique()
        joined = sorted(t for t, d in first_join.items() if d.year == year)
        rows.append(
            {
                "year": int(year),
                "members_first_session": int(
                    chunk[(chunk["date"] == first_day) & chunk["is_member"]]["ticker"].nunique()
                ),
                "max_members": int(daily.max()) if len(daily) else 0,
                "joined": ", ".join(joined),
            }
        )
    return pd.DataFrame(rows)
