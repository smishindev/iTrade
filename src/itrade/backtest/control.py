"""Random-entry control (spec §10): same filters, frequency, sizing, exits, costs; random entries.

Each run k (seed base_seed + k) replaces the RSI condition: among (session, instrument) pairs that
pass every other entry filter, it picks — per calendar year — as many pairs as the strategy has
signals, uniformly without replacement. Picked pairs get a random RSI in [0, 10] (random order),
others RSI 100. Everything else runs through the same engine.
"""

from __future__ import annotations

from collections.abc import Callable, Mapping
from concurrent.futures import ProcessPoolExecutor
from dataclasses import dataclass

import numpy as np
import pandas as pd

from itrade.backtest.engine import BacktestResult

RunFn = Callable[[Mapping[str, pd.DataFrame]], BacktestResult]


def eligible_pairs(
    prepared: Mapping[str, pd.DataFrame], start: str | pd.Timestamp, end: str | pd.Timestamp
) -> pd.DataFrame:
    """(date, ticker, rsi) of pairs passing every §4.1 filter except the RSI condition."""
    frames = []
    for ticker, f in prepared.items():
        ok = (
            f["member"]
            & f["sma_trend"].notna()
            & (f["close_star"] > f["sma_trend"])
            & ~f["ex_next"]
            & f["atr_star"].notna()
            & f["rsi"].notna()
        )
        sel = f.loc[ok & (f.index >= pd.Timestamp(start)) & (f.index <= pd.Timestamp(end))]
        frames.append(
            pd.DataFrame({"date": sel.index, "ticker": ticker, "rsi": sel["rsi"].to_numpy()})
        )
    out = pd.concat(frames, ignore_index=True)
    return out.sort_values(["date", "ticker"], ignore_index=True)


def strategy_counts_per_year(eligible: pd.DataFrame, rsi_max: float) -> pd.Series:
    """Signals of the real strategy per calendar year (eligible pairs with RSI <= rsi_max)."""
    signals = eligible[eligible["rsi"] <= rsi_max]
    return signals.groupby(signals["date"].dt.year).size()


def random_picks(eligible: pd.DataFrame, counts: pd.Series, seed: int) -> pd.DataFrame:
    """Per year, `counts[year]` pairs drawn uniformly without replacement, with random RSI."""
    rng = np.random.default_rng(seed)
    year = eligible["date"].dt.year
    parts = []
    for y, n in counts.items():
        pool = eligible.index[year == y]
        chosen = rng.choice(pool, size=min(int(n), len(pool)), replace=False)
        parts.append(eligible.loc[np.sort(chosen), ["date", "ticker"]])
    picks = pd.concat(parts, ignore_index=True) if parts else eligible.iloc[0:0][["date", "ticker"]]
    return picks.assign(rsi=rng.uniform(0.0, 10.0, len(picks)))


def control_prepared(
    prepared: Mapping[str, pd.DataFrame], picks: pd.DataFrame
) -> dict[str, pd.DataFrame]:
    """Copies of the prepared frames whose RSI is 100 except at the picked pairs."""
    out = {}
    for ticker, f in prepared.items():
        p = picks[picks["ticker"] == ticker].set_index("date")["rsi"]
        rsi = pd.Series(100.0, index=f.index)
        rsi.loc[p.index] = p.to_numpy()
        out[ticker] = f.assign(rsi=rsi)
    return out


@dataclass(frozen=True)
class ControlRun:
    run: int
    trades: int
    expectancy_r: float


def percentile(strategy_expectancy: float, runs: list[ControlRun]) -> float:
    """Share of control runs (in %) whose expectancy is below the strategy's."""
    values = np.array([r.expectancy_r for r in runs if not np.isnan(r.expectancy_r)])
    if len(values) == 0:
        return float("nan")
    return float((values < strategy_expectancy).mean() * 100)


# --- execution (serial or parallel) ------------------------------------------------------------

_WORKER: dict = {}


def _init_worker(payload: dict) -> None:
    _WORKER.clear()
    _WORKER.update(payload)


def _one_run(k: int) -> ControlRun:
    w = _WORKER
    picks = random_picks(w["eligible"], w["counts"], w["base_seed"] + k)
    result = w["run_fn"](control_prepared(w["prepared"], picks))
    r = result.trades["r"].astype(float)
    return ControlRun(k, len(r), float(r.mean()) if len(r) else float("nan"))


def run_control(
    run_fn: RunFn,
    prepared: Mapping[str, pd.DataFrame],
    eligible: pd.DataFrame,
    counts: pd.Series,
    runs: int,
    base_seed: int,
    workers: int = 1,
) -> list[ControlRun]:
    """`run_fn(prepared) -> BacktestResult` must be picklable (a module-level callable or
    functools.partial of one) when workers > 1."""
    payload = {
        "run_fn": run_fn, "prepared": dict(prepared), "eligible": eligible,
        "counts": counts, "base_seed": base_seed,
    }  # fmt: skip
    if workers <= 1:
        _init_worker(payload)
        return [_one_run(k) for k in range(runs)]
    with ProcessPoolExecutor(workers, initializer=_init_worker, initargs=(payload,)) as pool:
        return list(pool.map(_one_run, range(runs), chunksize=max(1, runs // (workers * 4))))
