"""Trade review (P1.A.26): re-derive each trade from the bars and the spec, independently of the
simulator's bookkeeping, and draw it. A failed check is a simulator or spec bug to fix and test.

Only single-position rules are checked (signal, levels, fills, exit path, P&L arithmetic).
Sizing and account limits are covered by test_risk / test_engine.
"""

from __future__ import annotations

from collections.abc import Callable, Mapping
from dataclasses import dataclass
from decimal import Decimal
from pathlib import Path

import numpy as np
import pandas as pd

from itrade.backtest.fills import RawBar, fill_stop, raw_bar
from itrade.strategies.etf_pullback_v1 import SignalParams, entry_levels, exit_signal

R_STEP = Decimal("0.0001")


@dataclass(frozen=True)
class Check:
    name: str
    ok: bool
    detail: str = ""


def _bar(market: pd.DataFrame, d: pd.Timestamp) -> RawBar:
    row = market.loc[d]
    return raw_bar(row["open"], row["high"], row["low"], row["close"], row["s"])


def _rescale(level: Decimal, market: pd.DataFrame, t: pd.Timestamp, d: pd.Timestamp) -> Decimal:
    """A level set on t in t's traded prices, expressed in d's traded prices (splits in between)."""
    k = Decimal(repr(float(market.loc[t, "s"]))) / Decimal(repr(float(market.loc[d, "s"])))
    return level if k == 1 else level / k


def check_trade(
    trade: Mapping,
    market: pd.DataFrame,
    prepared: pd.DataFrame,
    sessions: pd.DatetimeIndex,
    p: SignalParams,
) -> list[Check]:
    entry, exit_ = pd.Timestamp(trade["entry_date"]), pd.Timestamp(trade["exit_date"])
    reason = str(trade["exit_reason"])
    i_entry, i_exit = sessions.get_loc(entry), sessions.get_loc(exit_)
    t = sessions[i_entry - 1]  # decision session (spec §4.1)
    sig = prepared.loc[t]
    checks = [
        Check("member on t", bool(sig["member"]), f"t = {t.date()}"),
        Check(
            "trend: C* > SMA200",
            bool(sig["close_star"] > sig["sma_trend"]),
            f"{sig['close_star']:.4f} > {sig['sma_trend']:.4f}",
        ),
        Check(
            f"pullback: RSI2 <= {p.rsi_max:g}",
            bool(sig["rsi"] <= p.rsi_max),
            f"RSI2 = {sig['rsi']:.2f}",
        ),
        Check("t+1 is not an ex-date", not bool(sig["ex_next"])),
    ]

    limit, stop = entry_levels(sig["close"], sig["atr_star"], sig["m"], sig["s"], p)
    limit_e, stop_x = _rescale(limit, market, t, entry), _rescale(stop, market, t, exit_)
    checks += [
        # the trade row is in exit-day prices: a split while holding rescales limit and entry
        Check(
            "limit = C + 0.5 ATR",
            _rescale(limit, market, t, exit_) == trade["limit"],
            f"{_rescale(limit, market, t, exit_)} vs {trade['limit']}",
        ),
        Check("stop = C - 2 ATR", stop_x == trade["stop"], f"{stop_x} vs {trade['stop']}"),
    ]

    eb = _bar(market, entry)
    checks += [
        Check("LOO: open <= limit", eb.open <= limit_e, f"open {eb.open}, limit {limit_e}"),
        Check(
            "entry at the open",
            trade["entry_price"] == _rescale(eb.open, market, entry, exit_),
            f"{trade['entry_price']}",
        ),
    ]

    # Path: no stop touch and no exit signal before the exit, in the order the day runs
    # (open exit -> intraday stop -> close decisions).
    early: list[str] = []
    for i in range(i_entry, i_exit):
        d = sessions[i]
        if fill_stop(_rescale(stop, market, t, d), _bar(market, d)) is not None:
            early.append(f"stop touched {d.date()}")
        if i < i_exit - 1 and exit_signal(d, trade["ticker"], entry, prepared, sessions, p):
            early.append(f"exit signal {d.date()}")
    checks.append(Check("nothing missed before the exit", not early, "; ".join(early)))

    xb, prev = _bar(market, exit_), sessions[i_exit - 1]
    pending = (
        exit_signal(prev, trade["ticker"], entry, prepared, sessions, p) if prev >= entry else None
    )
    xp = trade["exit_price"]
    if reason in ("exit_sma", "max_hold"):
        ok = pending is not None and pending.reason == reason and xp == xb.open
        detail = f"signal on {prev.date()}: {pending.reason if pending else 'none'}; open {xb.open}"
    elif reason == "stop_gap":
        ok = xb.open <= stop_x and xp == xb.open
        detail = f"open {xb.open} <= stop {stop_x}"
    elif reason == "stop":
        ok = pending is None and xb.open > stop_x >= xb.low and xp == stop_x
        detail = f"open {xb.open}, low {xb.low}, stop {stop_x}"
    elif reason == "end_of_period":
        ok, detail = xp == xb.close, f"close {xb.close}"
    else:
        ok, detail = False, f"unknown reason {reason!r}"
    checks.append(Check(f"exit: {reason}", ok, detail))

    pnl = (
        trade["qty"] * (xp - trade["entry_price"])
        - trade["entry_costs"]
        - trade["exit_costs"]
        + trade["dividends"]
    )
    r = (pnl / trade["risk"]).quantize(R_STEP) if trade["risk"] > 0 else Decimal(0)
    checks += [
        Check("P&L = qty x move - costs + dividends", pnl == trade["pnl"], f"{pnl}"),
        Check("R = P&L / risk", r == trade["r"], f"{r}"),
        Check(
            "sessions held",
            int(trade["sessions_held"]) == i_exit - i_entry + 1,
            f"{trade['sessions_held']}",
        ),
    ]
    return checks


# --- chart -----------------------------------------------------------------------------------


def trade_svg(
    trade: Mapping,
    market: pd.DataFrame,
    prepared: pd.DataFrame,
    sessions: pd.DatetimeIndex,
    before: int = 25,
    after: int = 5,
    w: int = 760,
    h: int = 300,
) -> str:
    """Candles in traded prices with SMA(5)/SMA(200) brought to the same scale, limit and stop."""
    entry, exit_ = pd.Timestamp(trade["entry_date"]), pd.Timestamp(trade["exit_date"])
    i0 = max(0, sessions.get_loc(entry) - before)
    i1 = min(len(sessions) - 1, sessions.get_loc(exit_) + after)
    days = [d for d in sessions[i0 : i1 + 1] if d in market.index]
    bars = [_bar(market, d) for d in days]
    scale = [float(market.loc[d, "s"]) / float(prepared.loc[d, "m"]) for d in days]
    # H1: SMA(5) exit line; ETF_TREND_V2: the channel (prior high / low) instead
    candidates = (("sma_exit", "#e90"), ("low_n", "#e90"), ("high_n", "#36c"))
    lines = [(c, color) for c, color in candidates if c in prepared.columns]
    extra = [
        ([prepared.loc[d, c] * k for d, k in zip(days, scale, strict=True)], color)
        for c, color in lines
    ]
    has_trend = "sma_trend" in prepared.columns  # ETF_TOM_V3 has no trend filter
    sma200 = (
        [prepared.loc[d, "sma_trend"] * k for d, k in zip(days, scale, strict=True)]
        if has_trend
        else []
    )

    levels = [float(trade["limit"]), float(trade["stop"])]
    lo = min([float(b.low) for b in bars] + levels)
    hi = max([float(b.high) for b in bars] + levels)
    in_range = [v for v in sma200 if lo <= v <= hi]
    pad = (hi - lo) * 0.05 or 1.0
    lo, hi = lo - pad, hi + pad
    pl, pr, pt, pb = 50, 10, 10, 20
    step = (w - pl - pr) / len(days)

    def x(i: float) -> float:
        return pl + (i + 0.5) * step

    def y(v: float) -> float:
        return pt + (hi - v) / (hi - lo) * (h - pt - pb)

    out = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" font-size="10">',
        f'<rect width="{w}" height="{h}" fill="white"/>',
    ]
    for v in np.linspace(lo, hi, 5):
        out.append(f'<text x="2" y="{y(v) + 3:.1f}" fill="#555">{v:.2f}</text>')
    ie, ix = days.index(entry), days.index(exit_)
    out.append(
        f'<rect x="{x(ie) - step / 2:.1f}" y="{pt}" width="{(ix - ie + 1) * step:.1f}" '
        f'height="{h - pt - pb}" fill="#eef4ff"/>'
    )
    for i, b in enumerate(bars):
        up = b.close >= b.open
        color = "#2a7" if up else "#c33"
        top, bot = (b.close, b.open) if up else (b.open, b.close)
        out.append(
            f'<line x1="{x(i):.1f}" x2="{x(i):.1f}" y1="{y(float(b.high)):.1f}" '
            f'y2="{y(float(b.low)):.1f}" stroke="{color}"/>'
        )
        out.append(
            f'<rect x="{x(i) - step * 0.3:.1f}" y="{y(float(top)):.1f}" width="{step * 0.6:.1f}" '
            f'height="{max(1.0, y(float(bot)) - y(float(top))):.1f}" fill="{color}"/>'
        )

    def polyline(values: list[float], color: str) -> str:
        pts = " ".join(
            f"{x(i):.1f},{y(v):.1f}"
            for i, v in enumerate(values)
            if not np.isnan(v) and lo <= v <= hi
        )
        return f'<polyline points="{pts}" fill="none" stroke="{color}" stroke-width="1.2"/>'

    for values, color in extra:
        out.append(polyline(values, color))
    if in_range:
        out.append(polyline(sma200, "#888"))
    for v, color, label in ((levels[0], "#36c", "limit"), (levels[1], "#c33", "stop")):
        out.append(
            f'<line x1="{x(ie - 1):.1f}" x2="{x(ix):.1f}" y1="{y(v):.1f}" y2="{y(v):.1f}" '
            f'stroke="{color}" stroke-dasharray="4 3"/>'
        )
        out.append(f'<text x="{x(ix) + 4:.1f}" y="{y(v) + 3:.1f}" fill="{color}">{label}</text>')
    for i, price, color in (
        (ie, float(trade["entry_price"]), "#36c"),
        (ix, float(trade["exit_price"]), "#000"),
    ):
        out.append(f'<circle cx="{x(i):.1f}" cy="{y(price):.1f}" r="4" fill="{color}"/>')
    out.append(
        f'<text x="{pl}" y="{h - 5}" fill="#555">{days[0].date()} … {days[-1].date()} · '
        f"orange SMA5 or channel low, blue channel high, grey SMA200</text>"
    )
    out.append("</svg>")
    return "\n".join(out)


def write_review(
    trades: pd.DataFrame,
    market: Mapping[str, pd.DataFrame],
    prepared: Mapping[str, pd.DataFrame],
    sessions: pd.DatetimeIndex,
    p: SignalParams,
    out: Path,
    check: Callable[[Mapping], list[Check]] | None = None,
) -> tuple[int, int]:
    """review.md + one SVG per trade. Returns (trades reviewed, trades with a failed check).
    `check(trade)` replaces the H1 checks (ETF_TREND_V2: review_trend.check_trend_trade)."""
    out.mkdir(parents=True, exist_ok=True)
    lines = ["# Trade review", "", "Auto-checks re-derive each trade from bars and the spec.", ""]
    failed = 0
    for n, (_, tr) in enumerate(trades.iterrows(), start=1):
        m, pr = market[tr["ticker"]], prepared[tr["ticker"]]
        checks = check(tr) if check else check_trade(tr, m, pr, sessions, p)
        bad = [c for c in checks if not c.ok]
        failed += bool(bad)
        svg = f"{n:02d}_{tr['ticker']}_{pd.Timestamp(tr['entry_date']).date()}.svg"
        (out / svg).write_text(trade_svg(tr, m, pr, sessions), encoding="utf-8")
        costs = tr["entry_costs"] + tr["exit_costs"]
        lines += [
            f"## {n}. {tr['ticker']} {pd.Timestamp(tr['entry_date']).date()} → "
            f"{pd.Timestamp(tr['exit_date']).date()} · {tr['exit_reason']} · {tr['r']} R"
            + (" · **CHECK FAILED**" if bad else ""),
            "",
            f"qty {tr['qty']} · entry {tr['entry_price']} · limit {tr['limit']} · "
            f"stop {tr['stop']} · exit {tr['exit_price']} · "
            f"costs {costs} · dividends {tr['dividends']} · P&L {tr['pnl']}",
            "",
            f"![{svg}]({svg})",
            "",
            "| Check | OK | Detail |",
            "|---|---|---|",
        ]
        lines += [f"| {c.name} | {'✓' if c.ok else '✗'} | {c.detail} |" for c in checks]
        lines.append("")
    (out / "review.md").write_text("\n".join(lines), encoding="utf-8")
    return len(trades), failed
