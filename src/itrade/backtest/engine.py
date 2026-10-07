"""Day-by-day portfolio simulation of ETF_PULLBACK_V1 (spec §6). Deterministic.

Per session d (spec §6.1): settle cash; splits and dividends of ex-date d; open — exits (MOO) then
entries (LOO); intraday stops; close — equity, exit signals, entry signals sized by the risk policy
and turned into orders for d+1. Every skipped signal and unfilled order is recorded with a reason.
"""

from __future__ import annotations

import hashlib
from collections.abc import Mapping
from dataclasses import dataclass, field
from decimal import Decimal

import pandas as pd

from itrade.backtest.account import Account, Closed, SettlementRule
from itrade.backtest.fills import (
    RawBar,
    dividend_cash,
    fill_entry_loo,
    fill_open_exit,
    fill_stop,
    order_cost,
    raw_bar,
    to_price,
)
from itrade.backtest.risk import (
    Exposure,
    PortfolioState,
    RiskPolicy,
    cost_model_round_trip,
    size_entry,
)
from itrade.backtest.variants import RunOptions
from itrade.costs import CostConfig
from itrade.costs.model import BUY, SELL
from itrade.strategies.etf_pullback_v1 import SignalParams, entry_signals, exit_signal

ZERO = Decimal("0")
DEFAULT_OPTIONS = RunOptions()


@dataclass(frozen=True)
class InstrumentInfo:
    group: str
    half_spread_bps: float


@dataclass
class PendingEntry:
    key: str
    ticker: str
    signal_date: pd.Timestamp
    qty: Decimal
    limit: Decimal
    stop: Decimal
    planned_risk: Decimal


class FastFrame:
    """Read-only, date-keyed view of a DataFrame with the two operations the simulation uses:
    `d in frame.index` and `frame.loc[d][column]`. Same values, ~20x faster than pandas
    row lookups in the day loop."""

    __slots__ = ("loc",)

    def __init__(self, rows: dict[pd.Timestamp, dict]):
        self.loc = rows

    @property
    def index(self):  # dict keys: O(1) membership, picklable via `loc`
        return self.loc.keys()

    @classmethod
    def of(cls, frame: pd.DataFrame | FastFrame) -> FastFrame:
        if isinstance(frame, FastFrame):
            return frame
        return cls({pd.Timestamp(k): v for k, v in frame.to_dict("index").items()})


@dataclass
class BacktestResult:
    trades: pd.DataFrame
    skipped: pd.DataFrame
    equity: pd.DataFrame
    signals: int  # entry candidates produced by the strategy (before risk limits)

    def trades_hash(self) -> str:
        csv = self.trades.to_csv(index=False, float_format="%.10g").encode("utf-8")
        return hashlib.sha256(csv).hexdigest()


@dataclass
class _State:
    account: Account
    pending_entries: list[PendingEntry] = field(default_factory=list)
    pending_exits: dict[str, str] = field(default_factory=dict)  # ticker -> reason
    planned: dict[str, tuple[Decimal, Decimal]] = field(
        default_factory=dict
    )  # ticker -> (limit, risk)
    last_close: dict[str, Decimal] = field(default_factory=dict)
    trades: list[dict] = field(default_factory=list)
    skipped: list[dict] = field(default_factory=list)
    equity: list[dict] = field(default_factory=list)
    signals: int = 0


def run_backtest(
    market: Mapping[str, pd.DataFrame],
    prepared: Mapping[str, pd.DataFrame],
    info: Mapping[str, InstrumentInfo],
    sessions: pd.DatetimeIndex,
    signal_params: SignalParams,
    policy: RiskPolicy,
    cost_cfg: CostConfig,
    rules: list[SettlementRule],
    initial_capital: Decimal,
    start: str | pd.Timestamp,
    end: str | pd.Timestamp,
    withholding: Decimal = Decimal("0.25"),
    options: RunOptions = DEFAULT_OPTIONS,
) -> BacktestResult:
    """`market[t]`: date-indexed open/high/low/close/dividends/splits and `s` (split scale S(d)).
    `prepared[t]`: output of prepare_instrument. Signals are generated only within [start, end]."""
    days = sessions[(sessions >= pd.Timestamp(start)) & (sessions <= pd.Timestamp(end))]
    st = _State(Account(initial_capital, sessions, rules))
    tickers = sorted(market)
    mult = options.cost_multiplier
    market = {t: FastFrame.of(f) for t, f in market.items()}
    prepared = {t: FastFrame.of(f) for t, f in prepared.items()}

    def cost(ticker: str, qty: Decimal, price: Decimal, side: str) -> Decimal:
        return order_cost(cost_cfg, qty, price, side, info[ticker].half_spread_bps, mult)

    def bar(ticker: str, d: pd.Timestamp) -> RawBar | None:
        frame = market[ticker]
        if d not in frame.index:
            return None
        r = frame.loc[d]
        return raw_bar(r["open"], r["high"], r["low"], r["close"], r["s"])

    def close_position(ticker: str, d: pd.Timestamp, price: Decimal, reason: str) -> None:
        closed = st.account.sell_all(ticker, price, cost(ticker, _qty(st, ticker), price, SELL), d)
        st.trades.append(_trade_row(closed, reason, st.planned.pop(ticker), sessions))

    for d in days:
        acct = st.account
        acct.settle(d)
        _corporate_actions(st, market, tickers, d, withholding)

        # 1. Open: exits first, then entries in signal order.
        for ticker, reason in list(st.pending_exits.items()):
            b = bar(ticker, d)
            if b is None or ticker not in acct.positions:
                continue  # no bar: the exit waits for the next session
            fill = fill_open_exit(acct.positions[ticker].stop, b, reason)
            close_position(ticker, d, fill.price, fill.reason)
            del st.pending_exits[ticker]
        for pe in st.pending_entries:
            acct.release(pe.key)
            b = bar(pe.ticker, d)
            fill = fill_entry_loo(pe.limit, b) if b is not None else None
            if fill is None:
                st.skipped.append(
                    _skip(pe.signal_date, pe.ticker, "limit_not_reached" if b else "no_bar")
                )
                continue
            entry_cost = cost(pe.ticker, pe.qty, fill.price, BUY)
            if pe.qty * fill.price + entry_cost > acct.available():
                st.skipped.append(_skip(pe.signal_date, pe.ticker, "settled_cash_at_fill"))
                continue
            acct.buy(pe.ticker, pe.qty, fill.price, entry_cost, d, pe.stop)
            st.planned[pe.ticker] = (pe.limit, pe.planned_risk)
        st.pending_entries = []

        # 2. Intraday stops (including positions opened at today's open).
        for ticker in list(acct.positions):
            b = bar(ticker, d)
            if b is None:
                continue
            fill = fill_stop(acct.positions[ticker].stop, b)
            if fill is not None:
                close_position(ticker, d, fill.price, fill.reason)
                st.pending_exits.pop(ticker, None)

        # 3. Close: valuation.
        for ticker in tickers:
            b = bar(ticker, d)
            if b is not None:
                st.last_close[ticker] = b.close
        equity = acct.equity(st.last_close)
        st.equity.append(
            {
                "date": d,
                "equity": equity,
                "cash": acct.settled + acct.unsettled_total,
                "invested": acct.invested(st.last_close),
                "positions": len(acct.positions),
            }
        )

        if d == days[-1]:
            for ticker in list(acct.positions):
                close_position(ticker, d, st.last_close[ticker], "end_of_period")
            st.pending_exits.clear()
            break
        if options.skip_weekday and d.day_name() == options.skip_weekday:
            continue  # owner absent: no decisions after this close (broker-side stops stay active)

        # 3a. Exit decisions for tomorrow's open.
        for ticker, pos in acct.positions.items():
            if ticker in st.pending_exits:
                continue
            sig = exit_signal(d, ticker, pos.entry_date, prepared[ticker], sessions, signal_params)
            if sig is not None:
                st.pending_exits[ticker] = sig.reason

        # 3b. Entry decisions, sized against the whole book (positions + pending entries).
        candidates, skipped = entry_signals(d, prepared, set(acct.positions), signal_params)
        st.signals += len(candidates)
        st.skipped += [_skip(s.date, s.ticker, s.reason) for s in skipped]
        for c in candidates:
            state = PortfolioState(equity, acct.available(), _exposures(st, info))
            rt = cost_model_round_trip(cost_cfg, info[c.ticker].half_spread_bps, mult)
            res = size_entry(
                c.limit,
                c.stop,
                info[c.ticker].group,
                Decimal(repr(info[c.ticker].half_spread_bps)),
                state,
                policy,
                rt,
            )
            if not res.taken:
                st.skipped.append(_skip(d, c.ticker, res.reason))
                continue
            key = f"{d.date()}:{c.ticker}"
            acct.reserve(key, res.reservation)
            st.pending_entries.append(
                PendingEntry(key, c.ticker, d, res.qty, c.limit, c.stop, res.planned_risk)
            )

    for pe in st.pending_entries:  # orders for the session after the period are dropped
        st.account.release(pe.key)
    return BacktestResult(
        trades=pd.DataFrame(st.trades, columns=TRADE_COLUMNS),
        skipped=pd.DataFrame(st.skipped, columns=["date", "ticker", "reason"]),
        equity=pd.DataFrame(st.equity),
        signals=st.signals,
    )


TRADE_COLUMNS = [
    "ticker", "entry_date", "exit_date", "qty", "entry_price", "exit_price", "stop", "limit",
    "entry_costs", "exit_costs", "dividends", "pnl", "risk", "r", "exit_reason", "sessions_held",
]  # fmt: skip


def _qty(st: _State, ticker: str) -> Decimal:
    return st.account.positions[ticker].qty


def _skip(d: pd.Timestamp, ticker: str, reason: str) -> dict:
    return {"date": pd.Timestamp(d), "ticker": ticker, "reason": reason}


def _exposures(st: _State, info: Mapping[str, InstrumentInfo]) -> list[Exposure]:
    out = [
        Exposure(t, info[t].group, p.qty, p.entry_price, p.stop, p.qty * st.last_close[t])
        for t, p in st.account.positions.items()
    ]
    out += [
        Exposure(pe.ticker, info[pe.ticker].group, pe.qty, pe.limit, pe.stop, pe.qty * pe.limit)
        for pe in st.pending_entries
    ]
    return out


def _corporate_actions(
    st: _State,
    market: Mapping[str, pd.DataFrame],
    tickers: list[str],
    d: pd.Timestamp,
    withholding: Decimal,
) -> None:
    """Spec §6.1 step 0: splits on positions and pending orders, then dividends of ex-date d
    for positions held at the previous close."""
    acct = st.account
    for ticker in tickers:
        frame = market[ticker]
        if d not in frame.index:
            continue
        row = frame.loc[d]
        ratio = float(row["splits"] or 0.0)
        if ratio:
            k = Decimal(repr(ratio))
            acct.apply_split(ticker, k)
            if ticker in st.last_close:
                st.last_close[ticker] = st.last_close[ticker] / k
            if ticker in st.planned:
                limit, risk = st.planned[ticker]
                st.planned[ticker] = (limit / k, risk)
            for pe in st.pending_entries:
                if pe.ticker == ticker:
                    pe.qty, pe.limit, pe.stop = pe.qty * k, pe.limit / k, pe.stop / k
        dividend = float(row["dividends"] or 0.0)
        pos = acct.positions.get(ticker)
        if dividend > 0 and pos is not None and pos.entry_date < d:
            acct.credit_dividend(
                ticker, dividend_cash(pos.qty, dividend, float(row["s"]), withholding)
            )


def _trade_row(
    closed: Closed,
    reason: str,
    planned: tuple[Decimal, Decimal],
    sessions: pd.DatetimeIndex,
) -> dict:
    p = closed.position
    limit, planned_risk = planned
    pnl = (
        p.qty * closed.exit_price
        - p.qty * p.entry_price
        - p.entry_costs
        - closed.exit_costs
        + p.dividends
    )
    risk = p.qty * (p.entry_price - p.stop) if p.entry_price > p.stop else planned_risk
    held = int(sessions.get_loc(closed.exit_date) - sessions.get_loc(p.entry_date)) + 1
    return {
        "ticker": p.ticker, "entry_date": p.entry_date, "exit_date": closed.exit_date,
        "qty": p.qty, "entry_price": p.entry_price, "exit_price": closed.exit_price,
        "stop": p.stop, "limit": limit, "entry_costs": p.entry_costs,
        "exit_costs": closed.exit_costs, "dividends": p.dividends, "pnl": pnl,
        "risk": risk, "r": (pnl / risk).quantize(Decimal("0.0001")) if risk > 0 else Decimal(0),
        "exit_reason": reason, "sessions_held": held,
    }  # fmt: skip


def market_frame(bars: pd.DataFrame) -> pd.DataFrame:
    """Date-indexed vendor bars with the split scale S(d) attached (input for run_backtest)."""
    from itrade.backtest.corporate_actions import split_scale

    df = bars.sort_values("date").reset_index(drop=True)
    out = df[["open", "high", "low", "close", "dividends", "splits"]].copy()
    out["s"] = split_scale(df).to_numpy()
    out.index = pd.to_datetime(df["date"])
    return out


__all__ = ["BacktestResult", "InstrumentInfo", "market_frame", "run_backtest", "to_price"]
