"""Load project configuration from config/*.toml."""

from __future__ import annotations

import hashlib
import json
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


# Named universes are downloaded with their full history: membership rules count the bars that
# exist in the snapshot (spec §3), so a later start date would delay membership artificially.
FULL_HISTORY_START = "1990-01-01"


@dataclass(frozen=True)
class Instrument:
    ticker: str
    name: str
    asset_class: str
    half_spread_bps: float | None = None
    kind: str = "instrument"  # "instrument" | "fx"
    group: str | None = None  # correlation group for portfolio limits


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


def load_strategy(name: str) -> dict:
    """Parameters of a strategy: config/strategies/<name>.toml (e.g. etf_pullback_v1)."""
    return load_toml(f"strategies/{name}.toml")


def strategy_version(name: str) -> str:
    """SHA-256 of canonical JSON {"strategy": params, "universe": candidates} (spec, header)."""
    strategy = load_strategy(name)
    universe = load_toml(f"universes/{strategy['strategy']['universe']}.toml")
    canonical = json.dumps(
        {"strategy": strategy, "universe": universe},
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        default=str,
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def load_named_universe(name: str, raw: dict | None = None) -> Universe:
    """A strategy universe from config/universes/<name>.toml (e.g. etf_pullback_v1)."""
    raw = raw if raw is not None else load_toml(f"universes/{name}.toml")
    meta = raw["universe"]
    instruments = tuple(
        Instrument(
            ticker=c["ticker"],
            name=c.get("name", c["ticker"]),
            asset_class=c.get("category", "unknown"),
            half_spread_bps=c.get("half_spread_bps"),
            group=c.get("group"),
        )
        for c in raw["candidates"]
    )
    return Universe(
        base_currency="ILS",
        trading_currency=meta.get("currency", "USD"),
        calendar=meta.get("calendar", "XNYS"),
        start_date=FULL_HISTORY_START,
        benchmark="SPY",  # market reference of the strategy report (spec §7)
        benchmark_fallback="SPY",
        instruments=instruments,
    )
