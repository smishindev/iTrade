"""Pre-registered variants (spec §9): apply a variant's overrides to the base parameters."""

from __future__ import annotations

import copy
from dataclasses import dataclass


@dataclass(frozen=True)
class RunOptions:
    """Overrides that are not strategy parameters but simulation/operations settings."""

    cost_multiplier: float = 1.0
    skip_weekday: str | None = None  # e.g. "Wednesday": no decisions after that session's close


WEEKDAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]


def variant_names(params: dict) -> list[str]:
    return [v["name"] for v in params.get("variants", [])]


def apply_variant(params: dict, name: str) -> tuple[dict, RunOptions, bool]:
    """(parameters with overrides applied, run options, is_diagnostic) for variant `name`."""
    matches = [v for v in params.get("variants", []) if v["name"] == name]
    if not matches:
        raise KeyError(f"unknown variant {name!r}; registered: {variant_names(params)}")
    variant = matches[0]
    out = copy.deepcopy(params)
    options = RunOptions()
    for key, value in variant.get("overrides", {}).items():
        section, _, field = key.partition(".")
        if section == "costs" and field == "multiplier":
            options = RunOptions(float(value), options.skip_weekday)
        elif section == "operations" and field == "skip_weekday":
            if value not in WEEKDAYS:
                raise ValueError(f"skip_weekday must be one of {WEEKDAYS}")
            options = RunOptions(options.cost_multiplier, value)
        else:
            if section not in out or field not in out[section]:
                raise KeyError(f"variant {name!r} overrides unknown key {key!r}")
            out[section][field] = value
    return out, options, bool(variant.get("diagnostic", False))
