"""Compare registered variants on one period and apply the pre-registered selection rule.

H1 (research-log): base is used unless a candidate beats it by >= +0.05R and the other members of
its parameter family (base included) have positive expectancy. ETF_TREND_V2 (spec §8): the same
rule inside each rule group (rotation, breakout), then the group pick with the higher 90% lower
bound, then early rejection (expectancy <= 0 or control percentile < 80 -> no final run).
Diagnostics never win. A variant's family is its first override key (within its rule group).
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

import pandas as pd

SELECTION_MARGIN_R = 0.05


@dataclass(frozen=True)
class RuleGroup:
    name: str  # "" for single-group strategies (H1)
    base: str
    costs_x2: str
    variants: tuple[dict, ...]  # every variant of the group, diagnostics included


def is_trend(params: dict) -> bool:
    return params.get("strategy", {}).get("id") == "ETF_TREND_V2"


def rule_groups(params: dict) -> list[RuleGroup]:
    variants = params.get("variants", [])
    if not is_trend(params):
        return [RuleGroup("", "base", "costs_x2", tuple(variants))]
    out = []
    for rules in ("rotation", "breakout"):
        vs = tuple(v for v in variants if v.get("rules") == rules)
        base = next(v["name"] for v in vs if not v.get("overrides"))
        costs = next(
            v["name"] for v in vs if v.get("diagnostic") and "costs.multiplier" in v["overrides"]
        )
        out.append(RuleGroup(rules, base, costs, vs))
    return out


def group_of(variant: str, params: dict) -> RuleGroup:
    return next(g for g in rule_groups(params) if any(v["name"] == variant for v in g.variants))


def families(params: dict) -> dict[str, list[str]]:
    """Candidates grouped by the first parameter they override, with their group's base;
    ordered by the value. Keys: "<key>" (H1) or "<rules>:<key>" (ETF_TREND_V2)."""
    result = {}
    for g in rule_groups(params):
        base_value: dict[str, object] = {}
        out: dict[str, list[tuple[object, str]]] = {}
        for v in g.variants:
            if v.get("diagnostic") or not v.get("overrides"):
                continue
            key, value = next(iter(v["overrides"].items()))
            section, _, fld = key.partition(".")
            base_value[key] = params.get(section, {}).get(fld)
            out.setdefault(key, []).append((value, v["name"]))
        for key, members in out.items():
            members.append((base_value[key], g.base))
            label = f"{g.name}:{key}" if g.name else key
            result[label] = [name for _, name in sorted(members, key=_value_order)]
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
                "equal_weight_cagr": meta.get("equal_weight_cagr"),
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
    reject: bool = False  # ETF_TREND_V2 §8 p. 3: rejected on validation, no final run
    group_picks: dict[str, str] = field(default_factory=dict)


def neighbours(variant: str, params: dict) -> list[str]:
    """§11 #4 (H1) / §9 #4 (H2): a group base's neighbours are all candidates of its group;
    a candidate's are its family members."""
    fams = families(params).values()
    g = group_of(variant, params)
    in_group = {v["name"] for v in g.variants if not v.get("diagnostic")}
    if variant == g.base:
        return sorted({m for fam in fams for m in fam if m in in_group} - {variant})
    return sorted({m for fam in fams if variant in fam for m in fam} - {variant})


def _pick_in_group(
    runs: pd.DataFrame, params: dict, g: RuleGroup, fractional_confirmed: bool
) -> tuple[str, list[str]]:
    if g.base not in runs.index:
        raise ValueError(f"{g.base} has not been run on this period")
    base = float(runs.loc[g.base, "expectancy_r"])
    reasons, winners = [], []
    for key, members in families(params).items():
        if g.base not in members:
            continue
        for name in members:
            if name == g.base:
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
            if name.endswith("fractional") and not fractional_confirmed:
                verdict = "not eligible until P1.B.10 confirms fractional shares with a stop"
            reasons.append(
                f"{name} ({key}): {e:+.3f}R vs {g.base} {base:+.3f}R, "
                f"{'beats' if beats else 'does not beat'} by {SELECTION_MARGIN_R}R; "
                f"family {others} {'all > 0' if positive else 'not all > 0'} → {verdict}"
            )
            if verdict == "selected":
                winners.append((e, name))
    return (max(winners)[1] if winners else g.base), reasons


def select(runs: pd.DataFrame, params: dict, fractional_confirmed: bool = False) -> Selection:
    """`fractional_confirmed`: spike B (P1.B.10) accepted fractional shares with a stop."""
    groups = rule_groups(params)
    picks, reasons = {}, []
    for g in groups:
        pick, why = _pick_in_group(runs, params, g, fractional_confirmed)
        picks[g.name], reasons = pick, reasons + why
    if not is_trend(params):
        return Selection(picks[""], reasons)
    chosen = max(picks.values(), key=lambda v: (float(runs.loc[v, "ci_low"]), v))
    reasons.append(
        "between groups (higher 90% lower bound): "
        + ", ".join(f"{v} {float(runs.loc[v, 'ci_low']):+.3f}" for v in picks.values())
        + f" → {chosen}"
    )
    s = params["selection"]
    e, pct = float(runs.loc[chosen, "expectancy_r"]), runs.loc[chosen, "control_percentile"]
    reject = e <= s["early_reject_max_expectancy_r"] or (
        pct is None or pct < s["early_reject_min_control_percentile"]
    )
    reasons.append(
        f"early rejection: expectancy {e:+.3f}R (must be > {s['early_reject_max_expectancy_r']}), "
        f"control {pct if pct is None else f'{pct:.1f}'} (must be >= "
        f"{s['early_reject_min_control_percentile']}) → "
        + ("**H2 rejected, final period not run**" if reject else "final run allowed")
    )
    return Selection(chosen, reasons, reject, picks)


def criteria_check(runs: pd.DataFrame, variant: str, params: dict) -> list[tuple[str, bool]]:
    """The final-period criteria evaluated on this period — information only."""
    c, r = params["criteria"], runs.loc[variant]
    near = [m for m in neighbours(variant, params) if m in runs.index]
    costs = group_of(variant, params).costs_x2
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
            f"{costs} expectancy > 0",
            costs in runs.index and runs.loc[costs, "expectancy_r"] > 0,
        ),
        (
            "most neighbours > 0",
            bool(near) and sum(runs.loc[m, "expectancy_r"] > 0 for m in near) > len(near) / 2,
        ),
        (
            f"executable >= {c['min_executable_signal_share']:.0%}",
            r["executable_share"] >= c["min_executable_signal_share"],
        ),
        (f"max drawdown <= {c['max_drawdown']:.0%}", r["max_drawdown"] <= c["max_drawdown"]),
    ]
    if is_trend(params):
        out.append(("CAGR after costs > 0", r["cagr"] > 0))
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
    ew = runs["equal_weight_cagr"].dropna() if "equal_weight_cagr" in runs else pd.Series()
    sel = select(runs, params)
    lines += [
        "",
        f"Market reference (SPY buy & hold, total return), {period} period: CAGR {ref:+.1%}."
        + (f" Equal-weight universe: CAGR {ew.iloc[0]:+.1%}." if len(ew) else ""),
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
