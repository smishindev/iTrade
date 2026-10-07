"""Run a registered variant of a strategy over a period on the curated data."""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal
from functools import partial

from itrade.backtest.account import settlement_rules
from itrade.backtest.control import (
    ControlRun,
    eligible_pairs,
    run_control,
    strategy_counts_per_year,
)
from itrade.backtest.engine import (
    BacktestResult,
    FastFrame,
    InstrumentInfo,
    market_frame,
    run_backtest,
)
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
    params, options, diagnostic = apply_variant(load_strategy(strategy), variant)
    if inputs is None or inputs.params != params:
        inputs = load_inputs(strategy, store, params)
    start, end = period_dates(params, period)
    kwargs = engine_kwargs(inputs, params, start, end, options)
    result = run_backtest(prepared=inputs.prepared, **kwargs)
    return Run(strategy, variant, period, start, end, diagnostic, options, inputs, result)


def engine_kwargs(
    inputs: StrategyInputs, params: dict, start: str, end: str, options: RunOptions
) -> dict:
    """Every run_backtest argument except `prepared` — shared by the strategy and its control."""
    costs = CostConfig.load()
    info = {
        i.ticker: InstrumentInfo(
            i.group or "none", i.half_spread_bps or costs.default_half_spread_bps
        )
        for i in inputs.universe.instruments
    }
    return {
        "market": {t: FastFrame.of(market_frame(b)) for t, b in inputs.bars.items()},
        "info": info,
        "sessions": inputs.sessions,
        "signal_params": inputs.signal_params,
        "policy": RiskPolicy.from_config(params, costs.min_per_order_usd, costs.slippage_bps),
        "cost_cfg": costs,
        "rules": settlement_rules(params),
        "initial_capital": Decimal(str(params["risk"]["initial_capital_usd"])),
        "start": start,
        "end": end,
        "withholding": Decimal(str(params["simulation"]["dividend_withholding"])),
        "options": options,
    }


def _run_with_prepared(prepared, **kwargs) -> BacktestResult:
    return run_backtest(prepared=prepared, **kwargs)


def random_control(run: Run, runs: int | None = None, workers: int = 1) -> list[ControlRun]:
    """Spec §10 random control for an existing run (same variant, period and settings)."""
    params = run.inputs.params
    kwargs = engine_kwargs(run.inputs, params, run.start, run.end, run.options)
    eligible = eligible_pairs(run.inputs.prepared, run.start, run.end)
    counts = strategy_counts_per_year(eligible, run.inputs.signal_params.rsi_max)
    return run_control(
        partial(_run_with_prepared, **kwargs),
        run.inputs.prepared,
        eligible,
        counts,
        runs=runs if runs is not None else int(params["control"]["runs"]),
        base_seed=int(params["control"]["base_seed"]),
        workers=workers,
    )
