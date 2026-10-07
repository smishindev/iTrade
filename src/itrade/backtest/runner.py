"""Run a registered variant of a strategy over a period on the curated data."""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal

from itrade.backtest.account import settlement_rules
from itrade.backtest.engine import BacktestResult, InstrumentInfo, market_frame, run_backtest
from itrade.backtest.inputs import StrategyInputs, load_inputs
from itrade.backtest.risk import RiskPolicy
from itrade.backtest.variants import RunOptions, apply_variant
from itrade.config import load_strategy
from itrade.costs import CostConfig
from itrade.data.store import Store


@dataclass
class Run:
    strategy: str
    variant: str
    period: str
    start: str
    end: str
    diagnostic: bool
    options: RunOptions
    inputs: StrategyInputs
    result: BacktestResult


def period_dates(params: dict, period: str) -> tuple[str, str]:
    p = params["periods"][period]
    return str(p["start"]), str(p["end"])


def run_variant(
    strategy: str = "etf_pullback_v1",
    variant: str = "base",
    period: str = "development",
    store: Store | None = None,
    inputs: StrategyInputs | None = None,
) -> Run:
    base = load_strategy(strategy)
    params, options, diagnostic = apply_variant(base, variant)
    if inputs is None or inputs.params != params:
        inputs = load_inputs(strategy, store, params)
    costs = CostConfig.load()
    start, end = period_dates(params, period)
    info = {
        i.ticker: InstrumentInfo(
            i.group or "none", i.half_spread_bps or costs.default_half_spread_bps
        )
        for i in inputs.universe.instruments
    }
    result = run_backtest(
        market={t: market_frame(b) for t, b in inputs.bars.items()},
        prepared=inputs.prepared,
        info=info,
        sessions=inputs.sessions,
        signal_params=inputs.signal_params,
        policy=RiskPolicy.from_config(params, costs.min_per_order_usd, costs.slippage_bps),
        cost_cfg=costs,
        rules=settlement_rules(params),
        initial_capital=Decimal(str(params["risk"]["initial_capital_usd"])),
        start=start,
        end=end,
        withholding=Decimal(str(params["simulation"]["dividend_withholding"])),
        options=options,
    )
    return Run(strategy, variant, period, start, end, diagnostic, options, inputs, result)
