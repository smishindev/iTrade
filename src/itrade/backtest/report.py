"""Run report (roadmap P1.A.23): Markdown + CSV + SVG equity curve in data/research/<run-id>/.

Everything needed to reproduce and judge a run: strategy version, data/costs/overrides hashes,
git commit, period, variant, all metrics with intervals, skip reasons, breakdowns, the random
control (when run), a market reference, and 20 random trades for manual review.
"""

from __future__ import annotations

import dataclasses
import hashlib
import json
import math
import subprocess
from dataclasses import dataclass
from decimal import Decimal
from pathlib import Path

import numpy as np
import pandas as pd

from itrade.backtest.benchmark import annualised, buy_and_hold, equal_weight
from itrade.backtest.control import ControlRun, percentile
from itrade.backtest.corporate_actions import overrides_sha256
from itrade.backtest.engine import market_frame
from itrade.backtest.metrics import Summary, by_group, by_year, skip_reasons, summarize
from itrade.backtest.runner import Run
from itrade.backtest.stats import bootstrap_mean, wilson
from itrade.config import project_root, strategy_version


@dataclass
class Report:
    run_id: str
    directory: Path
    summary: Summary
    expectancy_ci: tuple[float, float]
    win_ci: tuple[float, float]
    control_percentile: float | None
    meta: dict


def _sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else ""


def git_commit() -> str:
    def git(*args: str) -> str:
        out = subprocess.run(
            ["git", *args], cwd=project_root(), capture_output=True, text=True, timeout=10
        )
        return out.stdout.strip()

    try:
        dirty = git("status", "--porcelain", "--", "src", "config")
        return git("rev-parse", "--short", "HEAD") + ("+dirty" if dirty else "")
    except (OSError, subprocess.SubprocessError):
        return "unknown"


def provenance(run: Run) -> dict:
    root = project_root()
    params_json = json.dumps(run.inputs.params, sort_keys=True, default=str).encode()
    return {
        "strategy": run.strategy,
        "variant": run.variant,
        "diagnostic": run.diagnostic,
        "period": run.period,
        "start": run.start,
        "end": run.end,
        "strategy_version": strategy_version(run.strategy),
        "variant_params_sha256": hashlib.sha256(params_json).hexdigest(),
        "data_manifest_sha256": _sha(root / "data" / "curated" / "manifest.json"),
        "costs_sha256": _sha(root / "config" / "costs.toml"),
        "overrides_sha256": overrides_sha256(),
        "run_options": dataclasses.asdict(run.options),  # not covered by the params hash
        "git": git_commit(),
        "trades_sha256": run.result.trades_hash(),
    }


def write_report(
    run: Run, out_root: Path, control: list[ControlRun] | None = None, reference: str = "SPY"
) -> Report:
    res, params = run.result, run.inputs.params
    meta = provenance(run)
    run_id = f"{run.variant}_{run.period}_{meta['trades_sha256'][:8]}"
    out = out_root / run_id
    out.mkdir(parents=True, exist_ok=True)

    s = summarize(res.trades, res.skipped, res.equity, res.signals)
    r = res.trades["r"].astype(float).to_numpy()
    crit = params["criteria"]
    ci = bootstrap_mean(
        r,
        level=float(crit["expectancy_ci_level"]),
        samples=int(crit["bootstrap_samples"]),
        seed=int(params["control"]["base_seed"]),
    )
    w = wilson(int((r > 0).sum()), len(r))
    pct = percentile(s.expectancy_r, control) if control else None

    capital = Decimal(str(params["risk"]["initial_capital_usd"]))
    ref = buy_and_hold(market_frame(run.inputs.bars[reference]), run.start, run.end, capital)
    ref_cagr = annualised(ref["equity"].astype(float), ref["date"])
    ew = equal_weight(
        {t: market_frame(b) for t, b in run.inputs.bars.items()}, run.start, run.end, 1.0
    )
    ew_cagr = annualised(ew["equity"], ew["date"])

    groups = {i.ticker: i.group or "none" for i in run.inputs.universe.instruments}
    res.trades.to_csv(out / "trades.csv", index=False)
    res.skipped.to_csv(out / "skipped.csv", index=False)
    res.equity.to_csv(out / "equity.csv", index=False)
    if control:
        pd.DataFrame([c.__dict__ for c in control]).to_csv(out / "control.csv", index=False)
    (out / "equity.svg").write_text(equity_svg(res.equity, ref), encoding="utf-8")
    meta |= {
        "run_id": run_id, "summary": s.as_dict(), "expectancy_ci": [ci.low, ci.high],
        "win_rate_ci": [w.low, w.high], "control_percentile": pct,
        "control_runs": len(control) if control else 0, "reference": reference,
        "reference_cagr": ref_cagr, "equal_weight_cagr": ew_cagr,
    }  # fmt: skip
    (out / "meta.json").write_text(json.dumps(meta, indent=2, default=str), encoding="utf-8")
    sections = {
        "year": by_year(res.trades),
        "group": by_group(res.trades, groups),
        "reasons": skip_reasons(res.skipped).to_frame("count"),
        "sample": review_sample(res.trades),
    }
    (out / "report.md").write_text(render_markdown(run, meta, s, sections), encoding="utf-8")
    return Report(run_id, out, s, (ci.low, ci.high), (w.low, w.high), pct, meta)


def review_sample(trades: pd.DataFrame, n: int = 20, seed: int = 20261007) -> pd.DataFrame:
    if len(trades) <= n:
        return trades
    idx = np.sort(np.random.default_rng(seed).choice(len(trades), n, replace=False))
    return trades.iloc[idx]


def fmt(x, pct: bool = False, digits: int = 3) -> str:
    if x is None or (isinstance(x, float) and math.isnan(x)):
        return "—"
    return f"{x:.1%}" if pct else f"{x:.{digits}f}"


def md_table(df: pd.DataFrame, index: bool = True) -> str:
    """Minimal Markdown table (no tabulate dependency)."""
    if df.empty:
        return "_none_"
    frame = df.reset_index() if index else df

    def cell(v) -> str:
        if isinstance(v, float):
            return f"{v:.3f}"
        if isinstance(v, pd.Timestamp):
            return str(v.date())
        return str(v)

    head = "| " + " | ".join(str(c) for c in frame.columns) + " |"
    sep = "|" + "---|" * len(frame.columns)
    rows = ["| " + " | ".join(cell(v) for v in row) + " |" for row in frame.itertuples(index=False)]
    return "\n".join([head, sep, *rows])


def render_markdown(run: Run, meta: dict, s: Summary, sections: dict) -> str:
    lo, hi = meta["expectancy_ci"]
    wlo, whi = meta["win_rate_ci"]
    pct = meta["control_percentile"]
    control = f"{pct:.1f} ({meta['control_runs']} runs)" if pct is not None else "not run"
    note = (
        (
            "_Development-period results are for debugging only (pre-registration, "
            "docs/research-log.md)._"
        )
        if run.period == "development"
        else ""
    )
    sample_cols = [
        "ticker",
        "entry_date",
        "exit_date",
        "qty",
        "entry_price",
        "exit_price",
        "stop",
        "exit_reason",
        "r",
    ]
    sample = sections["sample"]
    rows = [
        ("Trades", f"{s.trades}"),
        ("Expectancy", f"**{fmt(s.expectancy_r)} R** (90% bootstrap {fmt(lo)} … {fmt(hi)})"),
        ("Win rate", f"{fmt(s.win_rate, True)} (95% Wilson {fmt(wlo, True)} … {fmt(whi, True)})"),
        ("Avg win / avg loss", f"{fmt(s.avg_win_r)} R / {fmt(s.avg_loss_r)} R"),
        ("Costs per trade", f"{fmt(s.costs_r)} R"),
        ("Equity", f"${s.start_equity:,.2f} → ${s.end_equity:,.2f} (CAGR {fmt(s.cagr, True)})"),
        (
            f"Market reference ({meta['reference']} buy & hold, total return)",
            f"CAGR {fmt(meta['reference_cagr'], True)}",
        ),
        (
            "Equal-weight universe (yearly rebalance, total return, no costs)",
            f"CAGR {fmt(meta['equal_weight_cagr'], True)}",
        ),
        ("Time with a position", fmt(s.time_in_market, True)),
        ("Max drawdown", f"{fmt(s.max_drawdown, True)} over {s.max_drawdown_sessions} sessions"),
        ("Worst losing streak", f"{s.worst_losing_streak} trades"),
        ("Avg holding", f"{fmt(s.avg_sessions_held, digits=1)} sessions"),
        (
            "Turnover / avg invested",
            f"{fmt(s.turnover_per_year, digits=1)}× per year / {fmt(s.avg_invested, True)}",
        ),
        ("Signals / executable share", f"{s.signals} / {fmt(s.executable_share, True)}"),
        ("Random-control percentile", control),
        ("Red flags", ", ".join(s.flags) or "none"),
    ]
    provenance_rows = [
        ("Period", f"{run.start} … {run.end}"),
        ("Variant", f"`{run.variant}`" + (" (diagnostic)" if run.diagnostic else "")),
        ("Strategy version", f"`{meta['strategy_version'][:16]}…`"),
        ("Data manifest", f"`{meta['data_manifest_sha256'][:16]}…`"),
        (
            "Costs / overrides",
            f"`{meta['costs_sha256'][:12]}…` / `{meta['overrides_sha256'][:12]}…`",
        ),
        ("Git", f"`{meta['git']}`"),
        ("Trades hash", f"`{meta['trades_sha256'][:16]}…`"),
    ]
    table = pd.DataFrame
    return "\n".join([
        f"# Backtest report — {run.strategy} / `{run.variant}` / {run.period}",
        "", note, "",
        "## Provenance", "", md_table(table(provenance_rows, columns=["", "value"]), False), "",
        "## Results (after all costs)", "",
        md_table(table(rows, columns=["metric", "value"]), False), "",
        "Trades are treated as independent in the bootstrap; overlapping trades in correlated "
        "ETFs make the true interval wider.",
        "", "## Skipped signals", "", md_table(sections["reasons"]),
        "", "## By year (exit year)", "", md_table(sections["year"]),
        "", "## By correlation group", "", md_table(sections["group"]),
        "", "## 20 random trades for manual review", "",
        md_table(sample[sample_cols], False) if len(sample) else "_no trades_",
        "", "![equity](equity.svg)", "",
    ])  # fmt: skip


def equity_svg(equity: pd.DataFrame, reference: pd.DataFrame, w: int = 900, h: int = 300) -> str:
    """Two polylines (strategy, reference buy & hold) on a shared scale. No plotting dependency."""
    a = equity["equity"].astype(float).to_numpy()
    b = reference["equity"].astype(float).to_numpy()
    lo, hi = min(a.min(), b.min()), max(a.max(), b.max())
    span = (hi - lo) or 1.0

    def path(values: np.ndarray) -> str:
        xs = np.linspace(40, w - 10, len(values))
        ys = h - 30 - (values - lo) / span * (h - 50)
        return " ".join(f"{x:.1f},{y:.1f}" for x, y in zip(xs, ys, strict=True))

    dates = equity["date"].astype(str)
    first, last = dates.iloc[0][:10], dates.iloc[-1][:10]  # lookahead-ok: report axis labels
    return "".join([
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" ',
        'font-family="sans-serif" font-size="11"><rect width="100%" height="100%" fill="white"/>',
        f'<polyline fill="none" stroke="#999" stroke-width="1" points="{path(b)}"/>',
        f'<polyline fill="none" stroke="#1f6feb" stroke-width="1.5" points="{path(a)}"/>',
        f'<text x="40" y="14">strategy (blue) vs buy &amp; hold reference (grey), '
        f"${lo:,.0f} … ${hi:,.0f}</text>",
        f'<text x="40" y="{h - 8}">{first}</text><text x="{w - 80}" y="{h - 8}">{last}</text>',
        "</svg>",
    ])  # fmt: skip
