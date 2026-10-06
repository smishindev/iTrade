"""Load project configuration from config/*.toml."""

from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass
from pathlib import Path


def project_root() -> Path:
    """Repo root: $ITRADE_ROOT if set, else the nearest parent holding pyproject.toml."""
    if env := os.environ.get("ITRADE_ROOT"):
        return Path(env)
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "pyproject.toml").exists():
            return parent
    return Path.cwd()


def data_dir() -> Path:
    return project_root() / "data"


def load_toml(name: str) -> dict:
    with (project_root() / "config" / name).open("rb") as f:
        return tomllib.load(f)


@dataclass(frozen=True)
class Instrument:
    ticker: str
    name: str
    asset_class: str
    half_spread_bps: float | None = None
    kind: str = "instrument"  # "instrument" | "fx"


@dataclass(frozen=True)
class Universe:
    base_currency: str
    trading_currency: str
    calendar: str
    start_date: str
    benchmark: str
    benchmark_fallback: str
    instruments: tuple[Instrument, ...]

    @property
    def tickers(self) -> list[str]:
        return [i.ticker for i in self.instruments]

    def get(self, ticker: str) -> Instrument:
        for inst in self.instruments:
            if inst.ticker == ticker:
                return inst
        raise KeyError(f"{ticker!r} is not in config/universe.toml")


def load_universe(raw: dict | None = None) -> Universe:
    raw = raw if raw is not None else load_toml("universe.toml")
    instruments = [
        Instrument(
            ticker=i["ticker"],
            name=i.get("name", i["ticker"]),
            asset_class=i.get("asset_class", "unknown"),
            half_spread_bps=i.get("half_spread_bps"),
        )
        for i in raw.get("instruments", [])
    ]
    instruments += [
        Instrument(
            ticker=fx["ticker"], name=fx.get("pair", fx["ticker"]), asset_class="fx", kind="fx"
        )
        for fx in raw.get("fx", [])
    ]
    project = raw["project"]
    return Universe(
        base_currency=project["base_currency"],
        trading_currency=project["trading_currency"],
        calendar=project["calendar"],
        start_date=project["start_date"],
        benchmark=raw["benchmark"]["ticker"],
        benchmark_fallback=raw["benchmark"]["fallback_ticker"],
        instruments=tuple(instruments),
    )
