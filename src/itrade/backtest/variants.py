"""Pre-registered variants (spec §9): apply a variant's overrides to the base parameters."""

from __future__ import annotations

import copy
from dataclasses import dataclass, replace


@dataclass(frozen=True)
class RunOptions:
    """Overrides that are not strategy parameters but simulation/operations settings."""

    cost_multiplier: float = 1.0  # realised costs only; sizing and the cost gate use the model
    skip_weekday: str | None = None  # e.g. "Wednesday": no decisions after that session's close
    protective_stop: bool = True  # False: no stop order; the 2 ATR level stays the sizing/R unit


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
            options = replace(options, cost_multiplier=float(value))
        elif section == "operations" and field == "skip_weekday":
            if value not in WEEKDAYS:
                raise ValueError(f"skip_weekday must be one of {WEEKDAYS}")
            options = replace(options, skip_weekday=value)
        elif key == "exit.stop_atr" and float(value) == 0.0:
            # Registered as `stop_atr = 0` (no_stop). Read as "no protective stop order", not
            # "stop at the close": sizing and R keep the base 2 ATR distance (spec §9).
            options = replace(options, protective_stop=False)
        else:
            if section not in out or field not in out[section]:
                raise KeyError(f"variant {name!r} overrides unknown key {key!r}")
            out[section][field] = value
    return out, options, bool(variant.get("diagnostic", False))
