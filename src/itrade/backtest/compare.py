"""Compare registered variants on one period (P1.A.27) and apply the pre-registered selection rule
(docs/research-log.md, H1): base is used unless a candidate beats it by >= +0.05R and the other
members of its parameter family (base included) have positive expectancy. Diagnostics never win.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import pandas as pd

SELECTION_MARGIN_R = 0.05


def families(params: dict) -> dict[str, list[str]]:
    """Variants grouped by the parameter they override, with base; ordered by the value."""
    base_value: dict[str, object] = {}
    out: dict[str, list[tuple[object, str]]] = {}
    for v in params.get("variants", []):
        if v.get("diagnostic"):
            continue
        for key, value in v.get("overrides", {}).items():
            section, _, field = key.partition(".")
            base_value[key] = params.get(section, {}).get(field)
            out.setdefault(key, []).append((value, v["name"]))
    result = {}
    for key, members in out.items():
        members.append((base_value[key], "base"))
        result[key] = [name for _, name in sorted(members, key=_value_order)]
    return result


def _value_order(member: tuple[object, str]) -> tuple[int, float, str]:
    value = member[0]
    if isinstance(value, int | float):
        return (0, float(value), "")
    return (1, 0.0, str(value))


def load_runs(
    research_root: Path, period: str, strategy_version: str, run_ids: set[str] | None = None
) -> pd.DataFrame:
    """One row per variant from the run reports of `period` (current strategy version only).
    `run_ids` (latest ledger row per variant) drops runs superseded after a bug fix."""
    rows = []
    for meta_path in sorted(research_root.glob(f"*_{period}_*/meta.json")):
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        if meta.get("period") != period or meta.get("strategy_version") != strategy_version:
            continue
        if run_ids is not None and meta.get("run_id") not in run_ids:
            continue
        s = meta["summary"]
        rows.append(
            {
                "variant": meta["variant"],
                "diagnostic": bool(meta["diagnostic"]),
                "trades": s["trades"],
                "expectancy_r": s["expectancy_r"],
                "ci_low": meta["expectancy_ci"][0],
                "ci_high": meta["expectancy_ci"][1],
                "win_rate": s["win_rate"],
                "costs_r": s["costs_r"],
                "max_drawdown": s["max_drawdown"],
                "executable_share": s["executable_share"],
                "control_percentile": meta["control_percentile"],
                "cagr": s["cagr"],
                "reference_cagr": meta["reference_cagr"],
                "run_id": meta["run_id"],
            }
        )
    df = pd.DataFrame(rows)
    if df.empty:
        return df
    dupes = df[df.duplicated("variant", keep=False)]
    if not dupes.empty:
        raise ValueError(f"several runs per variant: {sorted(dupes['run_id'])}")
    return df.set_index("variant")


@dataclass(frozen=True)
class Selection:
    variant: str
    reasons: list[str]


def select(runs: pd.DataFrame, params: dict) -> Selection:
    if "base" not in runs.index:
        raise ValueError("base has not been run on this period")
    base = float(runs.loc["base", "expectancy_r"])
    reasons, winners = [], []
    for key, members in families(params).items():
        for name in members:
            if name == "base":
                continue
            if name not in runs.index:
                reasons.append(f"{name}: not run")
                continue
            e = float(runs.loc[name, "expectancy_r"])
            others = [m for m in members if m != name]
            missing = [m for m in others if m not in runs.index]
            positive = not missing and all(runs.loc[m, "expectancy_r"] > 0 for m in others)
            beats = e >= base + SELECTION_MARGIN_R
            verdict = "selected" if beats and positive else "no"
            reasons.append(
                f"{name} ({key}): {e:+.3f}R vs base {base:+.3f}R, "
                f"{'beats' if beats else 'does not beat'} by {SELECTION_MARGIN_R}R; "
                f"family {others} {'all > 0' if positive else 'not all > 0'} → {verdict}"
            )
            if verdict == "selected":
                winners.append((e, name))
    chosen = max(winners)[1] if winners else "base"
    return Selection(chosen, reasons)


def criteria_check(runs: pd.DataFrame, variant: str, params: dict) -> list[tuple[str, bool]]:
    """The final-period criteria (spec §11) evaluated on this period — information only."""
    c, r = params["criteria"], runs.loc[variant]
    fam = next((m for m in families(params).values() if variant in m), [variant])
    neighbours = [m for m in fam if m != variant and m in runs.index]
    out = [
        (f"expectancy >= +{c['min_expectancy_r']}R", r["expectancy_r"] >= c["min_expectancy_r"]),
        ("90% lower bound > 0", r["ci_low"] > 0),
        (f"trades >= {c['min_trades']}", r["trades"] >= c["min_trades"]),
        (
            f"control percentile >= {c['min_control_percentile']}",
            r["control_percentile"] is not None
            and r["control_percentile"] >= c["min_control_percentile"],
        ),
        (
            "costs_x2 expectancy > 0",
            "costs_x2" in runs.index and runs.loc["costs_x2", "expectancy_r"] > 0,
        ),
        (
            "most neighbours > 0",
            bool(neighbours)
            and sum(runs.loc[m, "expectancy_r"] > 0 for m in neighbours) > len(neighbours) / 2,
        ),
        (
            f"executable >= {c['min_executable_signal_share']:.0%}",
            r["executable_share"] >= c["min_executable_signal_share"],
        ),
        (f"max drawdown <= {c['max_drawdown']:.0%}", r["max_drawdown"] <= c["max_drawdown"]),
    ]
    return [(name, bool(ok)) for name, ok in out]


def comparison_markdown(runs: pd.DataFrame, params: dict, period: str) -> str:
    order = [v["name"] for v in params.get("variants", []) if v["name"] in runs.index]
    header = (
        "Variant | Type | Trades | Exp. R (90% CI) | Win % | Costs R | Max DD | Exec. | Control pct"
    )
    lines = [
        f"| {header} | CAGR |",
        "|---|---|---|---|---|---|---|---|---|---|",
    ]
    for name in order:
        r = runs.loc[name]
        pct = "—" if r["control_percentile"] is None else f"{r['control_percentile']:.1f}"
        lines.append(
            f"| {name} | {'diagnostic' if r['diagnostic'] else 'candidate'} | {r['trades']} | "
            f"{r['expectancy_r']:+.3f} ({r['ci_low']:+.3f} … {r['ci_high']:+.3f}) | "
            f"{r['win_rate']:.1%} | {r['costs_r']:.3f} | {r['max_drawdown']:.1%} | "
            f"{r['executable_share']:.0%} | {pct} | {r['cagr']:+.1%} |"
        )
    ref = runs["reference_cagr"].iloc[0]
    sel = select(runs, params)
    lines += [
        "",
        f"Market reference (SPY buy & hold, total return), {period} period: CAGR {ref:+.1%}.",
        "",
        f"**Selection rule → `{sel.variant}`**",
        "",
        *[f"- {reason}" for reason in sel.reasons],
        "",
        f"**Final-period criteria evaluated on {period} for `{sel.variant}` (information only):**",
        "",
        *[
            f"- {'✓' if ok else '✗'} {name}"
            for name, ok in criteria_check(runs, sel.variant, params)
        ],
    ]
    return "\n".join(lines)
