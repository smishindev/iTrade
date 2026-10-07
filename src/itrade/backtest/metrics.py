"""Trade-list and portfolio metrics of a backtest (spec §7, research-integrity skill).

Inputs are the engine's tables: trades (one row per closed trade, `r` in R after all costs and
dividends), skipped (date, ticker, reason) and the daily equity curve.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import asdict, dataclass

import numpy as np
import pandas as pd

# A pullback strategy with a 2xATR stop and costs at a small account size cannot plausibly win
# more often than this; such a result means a bug or look-ahead until proven otherwise.
MAX_PLAUSIBLE_WIN_RATE = 0.80
MAX_PLAUSIBLE_EXPECTANCY_R = 1.0
SMALL_ACCOUNT_REASONS = ("size_zero", "cost_too_high")


def implausibility_flags(trades: pd.DataFrame) -> list[str]:
    """Names of red flags raised by a trade list (empty = nothing implausible)."""
    if trades.empty:
        return []
    r = trades["r"].astype(float)
    flags = []
    if (r > 0).mean() > MAX_PLAUSIBLE_WIN_RATE:
        flags.append("win_rate")
    if r.mean() > MAX_PLAUSIBLE_EXPECTANCY_R:
        flags.append("expectancy")
    return flags


@dataclass(frozen=True)
class Summary:
    trades: int
    expectancy_r: float
    win_rate: float
    avg_win_r: float
    avg_loss_r: float
    costs_r: float  # mean (entry + exit costs) / planned risk
    total_pnl: float
    start_equity: float
    end_equity: float
    cagr: float
    max_drawdown: float  # fraction of the peak
    max_drawdown_sessions: int  # peak -> recovery (or -> end if not recovered)
    worst_losing_streak: int
    avg_sessions_held: float
    turnover_per_year: float  # bought notional / average equity, per year
    avg_invested: float  # average invested / equity
    signals: int
    executable_share: float
    flags: tuple[str, ...]

    def as_dict(self) -> dict:
        return asdict(self)


def r_values(trades: pd.DataFrame) -> np.ndarray:
    return trades["r"].astype(float).to_numpy()


def max_drawdown(equity: pd.Series) -> tuple[float, int]:
    """Largest peak-to-trough fall as a fraction of the peak, and its length in sessions from
    the peak until the curve regains the peak (or the end of the series)."""
    values = equity.astype(float).to_numpy()
    if len(values) == 0:
        return 0.0, 0
    peak_val, peak_i = values[0], 0
    worst, worst_len = 0.0, 0
    for i, v in enumerate(values):
        if v >= peak_val:
            peak_val, peak_i = v, i
            continue
        dd = (peak_val - v) / peak_val
        if dd > worst:
            worst = dd
            recovered = np.nonzero(values[i:] >= peak_val)[0]
            end = i + recovered[0] if len(recovered) else len(values) - 1
            worst_len = int(end - peak_i)
    return float(worst), worst_len


def worst_losing_streak(trades: pd.DataFrame) -> int:
    ordered = trades.sort_values(["exit_date", "entry_date", "ticker"])
    streak = worst = 0
    for r in ordered["r"].astype(float):
        streak = streak + 1 if r < 0 else 0
        worst = max(worst, streak)
    return worst


def executable_share(signals: int, skipped: pd.DataFrame) -> float:
    """Spec §7: among signals that reached sizing (not rejected for a full book), the share
    not rejected for small-account reasons (size_zero, cost_too_high)."""
    reasons = skipped["reason"].value_counts() if len(skipped) else pd.Series(dtype=int)
    sized = signals - int(reasons.get("max_positions", 0))
    if sized <= 0:
        return float("nan")
    small = sum(int(reasons.get(r, 0)) for r in SMALL_ACCOUNT_REASONS)
    return (sized - small) / sized


def summarize(
    trades: pd.DataFrame, skipped: pd.DataFrame, equity: pd.DataFrame, signals: int
) -> Summary:
    """Whole-period summary of a finished run (reporting only, never a trading input)."""
    eq = equity["equity"].astype(float)
    first_eq, last_eq = float(eq.iloc[0]), float(eq.iloc[-1])  # lookahead-ok: period summary
    last_day = equity["date"].iloc[-1]  # lookahead-ok: period summary
    years = max((last_day - equity["date"].iloc[0]).days / 365.25, 1e-9)
    r = r_values(trades)
    wins, losses = r[r > 0], r[r <= 0]
    risk = trades["risk"].astype(float) if len(trades) else pd.Series(dtype=float)
    costs = (
        (trades["entry_costs"].astype(float) + trades["exit_costs"].astype(float))
        if len(trades)
        else pd.Series(dtype=float)
    )
    bought = (trades["qty"].astype(float) * trades["entry_price"].astype(float)).sum()
    invested = (equity["invested"].astype(float) / eq).mean() if len(eq) else 0.0
    dd, dd_len = max_drawdown(eq)
    return Summary(
        trades=len(trades),
        expectancy_r=float(r.mean()) if len(r) else float("nan"),
        win_rate=float((r > 0).mean()) if len(r) else float("nan"),
        avg_win_r=float(wins.mean()) if len(wins) else float("nan"),
        avg_loss_r=float(losses.mean()) if len(losses) else float("nan"),
        costs_r=float((costs / risk).mean()) if len(r) else float("nan"),
        total_pnl=float(trades["pnl"].astype(float).sum()) if len(r) else 0.0,
        start_equity=first_eq,
        end_equity=last_eq,
        cagr=(last_eq / first_eq) ** (1 / years) - 1,
        max_drawdown=dd,
        max_drawdown_sessions=dd_len,
        worst_losing_streak=worst_losing_streak(trades) if len(r) else 0,
        avg_sessions_held=float(trades["sessions_held"].mean()) if len(r) else float("nan"),
        turnover_per_year=float(bought / eq.mean() / years),
        avg_invested=float(invested),
        signals=signals,
        executable_share=executable_share(signals, skipped),
        flags=tuple(implausibility_flags(trades)),
    )


def breakdown(trades: pd.DataFrame, by: str | pd.Series) -> pd.DataFrame:
    """Trades, expectancy, win rate and P&L per group (exit year, ticker or correlation group)."""
    if trades.empty:
        return pd.DataFrame(columns=["trades", "expectancy_r", "win_rate", "pnl"])
    t = trades.assign(r=trades["r"].astype(float), pnl=trades["pnl"].astype(float))
    key = t[by] if isinstance(by, str) else by
    g = t.groupby(key)
    return pd.DataFrame(
        {
            "trades": g.size(),
            "expectancy_r": g["r"].mean(),
            "win_rate": g["r"].apply(lambda s: (s > 0).mean()),
            "pnl": g["pnl"].sum(),
        }
    )


def by_year(trades: pd.DataFrame) -> pd.DataFrame:
    return breakdown(trades, pd.to_datetime(trades["exit_date"]).dt.year.rename("year"))


def by_group(trades: pd.DataFrame, groups: Mapping[str, str]) -> pd.DataFrame:
    return breakdown(trades, trades["ticker"].map(groups).rename("group"))


def skip_reasons(skipped: pd.DataFrame) -> pd.Series:
    return skipped["reason"].value_counts() if len(skipped) else pd.Series(dtype=int)
