"""Position sizing and portfolio risk limits (spec §5). All money and quantities are Decimal.

`size_entry` is a pure function of the candidate, the portfolio state and the policy. It returns
the quantity (possibly 0) and, when the signal is not taken, the reason in the spec's vocabulary.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass
from decimal import ROUND_FLOOR, Decimal

from itrade.costs import CostConfig, estimate_trade_cost
from itrade.costs.model import BUY, SELL

ZERO = Decimal("0")


@dataclass(frozen=True)
class RiskPolicy:
    """H1 (spec §5) uses every cap; ETF_TREND_V2 (§4) sizes by weight or risk, caps positions per
    group by count and drops the caps it does not have (None = no such cap)."""

    risk_per_trade: Decimal
    max_open_positions: int
    max_total_open_risk: Decimal | None
    max_position_fraction: Decimal
    max_invested_fraction: Decimal
    max_group_fraction: Decimal | None
    max_order_notional_usd: Decimal | None
    max_cost_in_r: Decimal
    granularity: Decimal  # 1 for whole shares, fractional_step otherwise
    min_per_order_usd: Decimal
    slippage_bps: Decimal
    sizing: str = "risk"  # "risk": q from the risk budget (§5.2) | "weight": q = weight x E / Lim
    weight: Decimal = ZERO
    max_positions_per_group: int | None = None

    @classmethod
    def from_config(cls, params: dict, min_per_order_usd: float, slippage_bps: float) -> RiskPolicy:
        r = params["risk"]
        whole = r["share_granularity"] == "whole"
        return cls(
            risk_per_trade=_d(r["risk_per_trade"]),
            max_open_positions=int(r["max_open_positions"]),
            max_total_open_risk=_d(r["max_total_open_risk"]),
            max_position_fraction=_d(r["max_position_fraction"]),
            max_invested_fraction=_d(r["max_invested_fraction"]),
            max_group_fraction=_d(r["max_group_fraction"]),
            max_order_notional_usd=_d(r["max_order_notional_usd"]),
            max_cost_in_r=_d(r["max_cost_in_r"]),
            granularity=Decimal(1) if whole else _d(r["fractional_step"]),
            min_per_order_usd=_d(min_per_order_usd),
            slippage_bps=_d(slippage_bps),
        )

    @classmethod
    def for_trend(
        cls, params: dict, rules: str, min_per_order_usd: float, slippage_bps: float
    ) -> RiskPolicy:
        """ETF_TREND_V2 §4: `rules` = "rotation" (equal weight) or "breakout" (risk budget)."""
        r = params["risk"]
        whole = r["share_granularity"] == "whole"
        rotation = rules == "rotation"
        return cls(
            risk_per_trade=ZERO if rotation else _d(params["breakout"]["risk_per_trade"]),
            max_open_positions=int(r["max_open_positions"]),
            max_total_open_risk=None,
            max_position_fraction=_d(r["max_position_fraction"]),
            max_invested_fraction=_d(r["max_invested_fraction"]),
            max_group_fraction=None,
            max_order_notional_usd=None,
            max_cost_in_r=_d(r["max_cost_in_r"]),
            granularity=Decimal(1) if whole else _d(r["fractional_step"]),
            min_per_order_usd=_d(min_per_order_usd),
            slippage_bps=_d(slippage_bps),
            sizing="weight" if rotation else "risk",
            weight=_d(params["rotation"]["weight"]) if rotation else ZERO,
            max_positions_per_group=int(r["max_positions_per_group"]),
        )

    @classmethod
    def for_tom(cls, params: dict, min_per_order_usd: float, slippage_bps: float) -> RiskPolicy:
        """ETF_TOM_V3 §2 p. 4: weight / N per ticker, whole shares, no group cap (all US equity)."""
        r, tom = params["risk"], params["tom"]
        whole = r["share_granularity"] == "whole"
        return cls(
            risk_per_trade=ZERO,
            max_open_positions=int(r["max_open_positions"]),
            max_total_open_risk=None,
            max_position_fraction=_d(r["max_position_fraction"]),
            max_invested_fraction=_d(r["max_invested_fraction"]),
            max_group_fraction=None,
            max_order_notional_usd=None,
            max_cost_in_r=_d(r["max_cost_in_r"]),
            granularity=Decimal(1) if whole else _d(r["fractional_step"]),
            min_per_order_usd=_d(min_per_order_usd),
            slippage_bps=_d(slippage_bps),
            sizing="weight",
            weight=_d(tom["weight"]) / len(tom["tickers"]),
            max_positions_per_group=None,
        )


def _d(x: float | int | str) -> Decimal:
    return Decimal(str(x))


@dataclass(frozen=True)
class Exposure:
    """An open position or a pending entry, as the risk limits see it."""

    ticker: str
    group: str
    qty: Decimal
    risk_price: Decimal  # entry price (position) or limit (pending)
    stop: Decimal
    value: Decimal  # market value at C_t (position) or notional at the limit (pending)

    @property
    def risk(self) -> Decimal:
        return self.qty * (self.risk_price - self.stop)


@dataclass(frozen=True)
class PortfolioState:
    equity: Decimal  # E_t
    available_cash: Decimal  # settled cash minus reservations
    exposures: Sequence[Exposure]  # open positions + pending entries


@dataclass(frozen=True)
class SizingResult:
    qty: Decimal
    reason: str | None  # None when taken
    round_trip_cost: Decimal
    planned_risk: Decimal  # q x (Lim - Stp), the trade's 1R
    reservation: Decimal  # q x Lim + RT/2

    @property
    def taken(self) -> bool:
        return self.reason is None


# Expected round-trip cost: buy qty at the limit + sell qty at the stop (spec §5.2 RT(q)).
RoundTripCost = Callable[[Decimal, Decimal, Decimal], Decimal]


def cost_model_round_trip(
    cfg: CostConfig, half_spread_bps: float, multiplier: float = 1.0
) -> RoundTripCost:
    """RT(q) from the cost model: buy q at the limit + sell q at the stop (spec §5.2, §6.3).
    `multiplier` serves the costs_x2 diagnostic variant."""

    def rt(qty: Decimal, limit: Decimal, stop: Decimal) -> Decimal:
        buy = estimate_trade_cost(
            cfg, float(qty), float(limit), BUY, half_spread_bps=half_spread_bps
        )
        sell = estimate_trade_cost(
            cfg, float(qty), float(stop), SELL, half_spread_bps=half_spread_bps
        )
        total = Decimal(repr(buy.total + sell.total)) * Decimal(repr(multiplier))
        return total.quantize(Decimal("0.0001"))

    return rt


def floor_to(x: Decimal, step: Decimal) -> Decimal:
    if x <= ZERO:
        return ZERO
    return (x / step).to_integral_value(rounding=ROUND_FLOOR) * step


def _skip(reason: str, rt: Decimal = ZERO) -> SizingResult:
    return SizingResult(ZERO, reason, rt, ZERO, ZERO)


def size_entry(
    limit: Decimal,
    stop: Decimal,
    group: str,
    half_spread_bps: Decimal,
    state: PortfolioState,
    policy: RiskPolicy,
    round_trip: RoundTripCost,
) -> SizingResult:
    """Spec §5.2–5.3. `round_trip(qty, limit, stop)` prices the full cycle with the cost model."""
    g = policy.granularity
    dist = limit - stop
    if dist <= ZERO:
        return _skip("invalid_levels")

    # #7 first: a full book rejects the whole signal.
    if len(state.exposures) >= policy.max_open_positions:
        return _skip("max_positions")
    if policy.max_positions_per_group is not None:
        same_group = sum(1 for e in state.exposures if e.group == group)
        if same_group >= policy.max_positions_per_group:
            return _skip("max_group")

    if policy.sizing == "weight":
        q = floor_to(policy.weight * state.equity / limit, g)
    else:
        # §5.2 base quantity from the risk budget, then shrink until risk + costs fit the budget.
        budget = policy.risk_per_trade * state.equity
        per_share = dist + limit * 2 * (half_spread_bps + policy.slippage_bps) / Decimal(10_000)
        q = floor_to((budget - 2 * policy.min_per_order_usd) / per_share, g)
        while q > ZERO and q * dist + round_trip(q, limit, stop) > budget:
            q -= g
    if q <= ZERO:
        return _skip("size_zero")

    # #1–#5: caps that only reduce q; the first one that zeroes it is the reason.
    open_risk = sum((e.risk for e in state.exposures), ZERO)
    invested = sum((e.value for e in state.exposures), ZERO)
    in_group = sum((e.value for e in state.exposures if e.group == group), ZERO)
    p = policy
    caps = [
        ("max_position", floor_to(p.max_position_fraction * state.equity / limit, g)),
        (
            "max_order_notional",
            None
            if p.max_order_notional_usd is None
            else floor_to(p.max_order_notional_usd / limit, g),
        ),
        (
            "max_total_risk",
            None
            if p.max_total_open_risk is None
            else floor_to((p.max_total_open_risk * state.equity - open_risk) / dist, g),
        ),
        ("max_invested", floor_to((p.max_invested_fraction * state.equity - invested) / limit, g)),
        (
            "max_group",
            None
            if p.max_group_fraction is None
            else floor_to((p.max_group_fraction * state.equity - in_group) / limit, g),
        ),
    ]
    for reason, cap in caps:
        if cap is None:
            continue
        q = min(q, cap)
        if q <= ZERO:
            return _skip(reason)

    # #6: purchase + half the round trip must fit the settled cash still available.
    wanted = q
    while q > ZERO and q * limit + round_trip(q, limit, stop) / 2 > state.available_cash:
        q -= g
    if q <= ZERO or (policy.sizing == "weight" and q < wanted):
        # weight sizing (ETF_TREND_V2): never a cut-down position — wait for settled cash
        return _skip("settled_cash")

    # #8: costs must not eat more than max_cost_in_r of the trade's planned risk.
    rt = round_trip(q, limit, stop)
    planned = q * dist
    if rt > policy.max_cost_in_r * planned:
        return _skip("cost_too_high", rt)

    return SizingResult(q, None, rt, planned, q * limit + rt / 2)
