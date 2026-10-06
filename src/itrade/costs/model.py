"""All-in friction: commission, regulatory fees, spread, slippage, FX conversion and tax.

Every backtest must price trades through `estimate_trade_cost` — never assume zero cost.
All money amounts are in USD unless the name says otherwise; tax works in ILS.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, fields

from itrade.config import load_toml

BUY = "buy"
SELL = "sell"


@dataclass(frozen=True)
class CostConfig:
    per_share_usd: float
    min_per_order_usd: float
    max_pct_of_value: float
    passthrough_per_share_usd: float
    sec_fee_per_million_usd: float
    finra_taf_per_share_usd: float
    finra_taf_max_usd: float
    fx_conversion_bps: float
    fx_min_per_conversion_usd: float
    default_half_spread_bps: float
    slippage_bps: float
    capital_gains_rate: float
    dividend_rate: float
    loss_carryforward: bool

    @classmethod
    def from_dict(cls, raw: dict) -> CostConfig:
        c, r, fx, ex, tax = (
            raw["commission"],
            raw["regulatory"],
            raw["fx"],
            raw["execution"],
            raw["tax"],
        )
        return cls(
            per_share_usd=c["per_share_usd"],
            min_per_order_usd=c["min_per_order_usd"],
            max_pct_of_value=c["max_pct_of_value"],
            passthrough_per_share_usd=c["passthrough_per_share_usd"],
            sec_fee_per_million_usd=r["sec_fee_per_million_usd"],
            finra_taf_per_share_usd=r["finra_taf_per_share_usd"],
            finra_taf_max_usd=r["finra_taf_max_usd"],
            fx_conversion_bps=fx["conversion_bps"],
            fx_min_per_conversion_usd=fx["min_per_conversion_usd"],
            default_half_spread_bps=ex["default_half_spread_bps"],
            slippage_bps=ex["slippage_bps"],
            capital_gains_rate=tax["capital_gains_rate"],
            dividend_rate=tax["dividend_rate"],
            loss_carryforward=tax["loss_carryforward"],
        )

    @classmethod
    def load(cls) -> CostConfig:
        return cls.from_dict(load_toml("costs.toml"))


@dataclass(frozen=True)
class TradeCost:
    notional: float
    commission: float
    regulatory: float
    spread: float
    slippage: float
    fx: float

    @property
    def total(self) -> float:
        return self.commission + self.regulatory + self.spread + self.slippage + self.fx

    @property
    def bps(self) -> float:
        return self.total / self.notional * 1e4 if self.notional else 0.0

    def as_dict(self) -> dict[str, float]:
        out = {f.name: getattr(self, f.name) for f in fields(self)}
        out |= {"total": self.total, "bps": self.bps}
        return out


def commission_usd(cfg: CostConfig, shares: float, price: float) -> float:
    notional = abs(shares) * price
    base = max(abs(shares) * cfg.per_share_usd, cfg.min_per_order_usd)
    base = min(base, cfg.max_pct_of_value * notional)
    return base + abs(shares) * cfg.passthrough_per_share_usd


def regulatory_usd(cfg: CostConfig, shares: float, price: float, side: str) -> float:
    if side != SELL:
        return 0.0
    sec = abs(shares) * price * cfg.sec_fee_per_million_usd / 1e6
    taf = min(abs(shares) * cfg.finra_taf_per_share_usd, cfg.finra_taf_max_usd)
    return sec + taf


def fx_usd(cfg: CostConfig, amount_usd: float) -> float:
    return max(abs(amount_usd) * cfg.fx_conversion_bps / 1e4, cfg.fx_min_per_conversion_usd)


def estimate_trade_cost(
    cfg: CostConfig,
    shares: float,
    price: float,
    side: str,
    *,
    half_spread_bps: float | None = None,
    convert_fx: bool = False,
) -> TradeCost:
    """Expected all-in cost of one order. `shares` may be fractional (IBKR allows it)."""
    if side not in (BUY, SELL):
        raise ValueError(f"side must be {BUY!r} or {SELL!r}, got {side!r}")
    if price <= 0:
        raise ValueError("price must be positive")
    notional = abs(shares) * price
    hs = cfg.default_half_spread_bps if half_spread_bps is None else half_spread_bps
    return TradeCost(
        notional=notional,
        commission=commission_usd(cfg, shares, price) if shares else 0.0,
        regulatory=regulatory_usd(cfg, shares, price, side),
        spread=notional * hs / 1e4,
        slippage=notional * cfg.slippage_bps / 1e4,
        fx=fx_usd(cfg, notional) if convert_fx else 0.0,
    )


def round_trip_bps(
    cfg: CostConfig, notional: float, price: float, *, half_spread_bps: float | None = None
) -> float:
    """Cost of buying and later selling `notional` USD, in basis points of notional."""
    shares = notional / price
    buy = estimate_trade_cost(cfg, shares, price, BUY, half_spread_bps=half_spread_bps)
    sell = estimate_trade_cost(cfg, shares, price, SELL, half_spread_bps=half_spread_bps)
    return (buy.total + sell.total) / notional * 1e4


def annual_drag_pct(round_trip_cost_bps: float, turnover_per_year: float) -> float:
    """Yearly return lost to trading. Turnover 1.0 = the whole portfolio bought and sold once."""
    return round_trip_cost_bps / 1e4 * turnover_per_year * 100


def capital_gains_tax(
    realized_gains_by_year: Mapping[int, float],
    rate: float,
    *,
    carryforward: bool = True,
) -> dict[int, float]:
    """Tax due per calendar year on net realised gains (ILS), with optional loss carry-forward.

    Simplification: taxes nominal ILS gains. Israeli law taxes *real* gains (inflation-
    adjusted), so this slightly overstates tax in inflationary years — conservative.
    """
    tax: dict[int, float] = {}
    carried_loss = 0.0
    for year in sorted(realized_gains_by_year):
        net = realized_gains_by_year[year] - carried_loss
        if net > 0:
            tax[year] = net * rate
            carried_loss = 0.0
        else:
            tax[year] = 0.0
            carried_loss = -net if carryforward else 0.0
    return tax
