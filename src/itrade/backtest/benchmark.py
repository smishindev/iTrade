"""Buy-and-hold of one instrument (market reference of the report, spec §7, and sanity checks).

Buys at the first open of the period with fractional shares for the whole capital, holds to the
last close, and reinvests each dividend at that day's close (so the result is a total return).
Costs and withholding are optional so the same code serves the zero-cost sanity check.
"""

from __future__ import annotations

from decimal import Decimal

import pandas as pd

from itrade.backtest.fills import order_cost, raw_bar, to_price
from itrade.costs import CostConfig
from itrade.costs.model import BUY

STEP = Decimal("0.000001")


def buy_and_hold(
    market: pd.DataFrame,
    start: str | pd.Timestamp,
    end: str | pd.Timestamp,
    capital: Decimal,
    cost_cfg: CostConfig | None = None,
    half_spread_bps: float = 0.0,
    withholding: Decimal = Decimal("0"),
) -> pd.DataFrame:
    """Daily equity of a buy-and-hold position. `market`: output of engine.market_frame."""
    days = market.loc[pd.Timestamp(start) : pd.Timestamp(end)]
    if days.empty:
        raise ValueError("no bars in the period")
    first = days.iloc[0]
    entry = raw_bar(first["open"], first["high"], first["low"], first["close"], first["s"]).open
    costs = Decimal(0)
    if cost_cfg is not None:
        costs = order_cost(cost_cfg, capital / entry, entry, BUY, half_spread_bps)
    qty = ((capital - costs) / entry).quantize(STEP)
    cash = capital - costs - qty * entry
    rows = []
    for i, (d, row) in enumerate(days.iterrows()):
        if i > 0 and row["splits"]:
            qty *= Decimal(repr(float(row["splits"])))
        close = to_price(row["close"] * row["s"])
        if i > 0 and row["dividends"]:
            per_share = to_price(row["dividends"] * row["s"])
            net = qty * per_share * (Decimal(1) - withholding)
            qty += (net / close).quantize(STEP)  # reinvest at the close
        rows.append({"date": d, "equity": cash + qty * close})
    return pd.DataFrame(rows)


def equal_weight(
    markets: dict[str, pd.DataFrame],
    start: str | pd.Timestamp,
    end: str | pd.Timestamp,
    capital: float,
) -> pd.DataFrame:
    """ETF_TREND_V2 §5 reference: equal weights across the instruments trading on the first
    session of each calendar year, rebalanced then, drifting in between; total return
    (C_t + D_t) / C_{t-1} on split-adjusted vendor prices; no costs, no tax. `markets`:
    outputs of engine.market_frame."""
    s, e = pd.Timestamp(start), pd.Timestamp(end)
    tr = {}
    for t, m in markets.items():
        c = m["close"]
        tr[t] = ((c + m["dividends"].fillna(0.0)) / c.shift(1)).loc[s:e]  # past close only
    returns = pd.DataFrame(tr).sort_index()
    first_day = returns.index[0]
    equity, rows = float(capital), []
    for _, chunk in returns.groupby(returns.index.year):
        d0 = chunk.index[0]
        if d0 == first_day:  # bought at the first close: instruments with a bar that day
            live = [c for c in chunk.columns if pd.notna(markets[c]["close"].get(d0))]
        else:  # rebalanced at the previous close: instruments with a return on d0
            live = [c for c in chunk.columns if pd.notna(chunk.loc[d0, c])]
        value = pd.Series(equity / len(live), index=live)
        for d, row in chunk[live].iterrows():
            if d != first_day:
                value = value * row.fillna(1.0)  # no bar that day: unchanged
            rows.append({"date": d, "equity": float(value.sum())})
        equity = float(value.sum())
    return pd.DataFrame(rows)


def annualised(equity: pd.Series, dates: pd.Series) -> float:
    """Compound annual growth over a finished period (reporting, not a trading decision)."""
    last, first = equity.iloc[-1], equity.iloc[0]  # lookahead-ok: whole-period summary
    span = pd.Timestamp(dates.iloc[-1]) - pd.Timestamp(dates.iloc[0])  # lookahead-ok: summary
    return float(last / first) ** (365.25 / span.days) - 1
