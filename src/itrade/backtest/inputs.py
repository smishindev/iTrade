"""Load everything a strategy run needs: parameters, universe, bars (with corporate-action
overrides), membership by date, sessions — and the prepared per-instrument frames."""

from __future__ import annotations

from dataclasses import dataclass

import pandas as pd

from itrade.backtest.corporate_actions import apply_dividend_overrides, load_dividend_overrides
from itrade.backtest.universe import MembershipRules, membership
from itrade.config import Universe, load_named_universe, load_strategy
from itrade.data.calendar import load_sessions
from itrade.data.store import Store
from itrade.strategies.etf_pullback_v1 import SignalParams, prepare_instrument


@dataclass
class StrategyInputs:
    params: dict
    signal_params: SignalParams
    universe: Universe
    bars: dict[str, pd.DataFrame]
    membership: pd.DataFrame
    sessions: pd.DatetimeIndex
    prepared: dict[str, pd.DataFrame]


def load_inputs(strategy: str = "etf_pullback_v1", store: Store | None = None) -> StrategyInputs:
    store = store or Store()
    params = load_strategy(strategy)
    universe = load_named_universe(params["strategy"]["universe"])
    overrides = load_dividend_overrides()
    bars = {t: apply_dividend_overrides(store.read_bars(t), t, overrides) for t in universe.tickers}
    table = membership(bars, MembershipRules.from_strategy(params))
    sessions = load_sessions(store.root, universe.calendar)
    sp = SignalParams.from_strategy(params)
    prepared = {}
    for ticker, df in bars.items():
        member = table[table["ticker"] == ticker].set_index("date")["is_member"]
        prepared[ticker] = prepare_instrument(df, member, sessions, sp)
    return StrategyInputs(params, sp, universe, bars, table, sessions, prepared)
