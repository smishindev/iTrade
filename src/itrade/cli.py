"""Command line: `uv run itrade <command>`."""

from __future__ import annotations

import argparse
import sys
from datetime import date, datetime
from zoneinfo import ZoneInfo

from itrade.config import Universe, load_named_universe, load_universe
from itrade.costs import CostConfig, annual_drag_pct, estimate_trade_cost, round_trip_bps
from itrade.costs.model import BUY, SELL, fx_usd
from itrade.data.quality import QualityReport, validate_bars
from itrade.data.store import Store


def market_today() -> date:
    """Today's date in New York — the exchange's calendar, not the machine's (Israel)."""
    return datetime.now(ZoneInfo("America/New_York")).date()


def _print_report(report: QualityReport, verbose: bool) -> None:
    status = "OK  " if report.ok else "FAIL"
    span = f"{report.first} .. {report.last}" if report.rows else "-"
    print(
        f"  {status} {report.ticker:<8} rows={report.rows:<6} {span}  "
        f"errors={len(report.errors)} warnings={len(report.warnings)}"
    )
    shown = report.issues if verbose else report.errors
    for issue in shown:
        print(f"         [{issue.severity}] {issue.check}: {issue.message}")


def _universe(args: argparse.Namespace) -> Universe:
    """--universe NAME → config/universes/NAME.toml; otherwise the default config/universe.toml."""
    name = getattr(args, "universe", None)
    return load_named_universe(name) if name else load_universe()


def cmd_ingest(args: argparse.Namespace) -> int:
    from itrade.data.ingest import ingest
    from itrade.data.sources import get_source

    universe = _universe(args)
    results = ingest(
        universe,
        get_source(args.source),
        Store(),
        tickers=args.tickers or None,
        start=args.start,
        today=market_today(),
    )
    print(f"Ingest from {args.source}:")
    for r in results:
        if r.error:
            print(f"  ERR  {r.ticker:<8} {r.error}")
        else:
            _print_report(r.report, args.verbose)
    failed = [r.ticker for r in results if not r.promoted]
    if failed:
        print(f"\nNot promoted (curated data unchanged): {', '.join(failed)}")
    return 1 if failed else 0


def cmd_quality(args: argparse.Namespace) -> int:
    universe = _universe(args)
    store = Store()
    print("Quality of curated data:")
    bad = 0
    for ticker in args.tickers or universe.tickers:
        inst = universe.get(ticker)
        try:
            df = store.read_bars(ticker).drop(columns="ticker")
        except FileNotFoundError as exc:
            print(f"  MISS {ticker:<8} {exc}")
            bad += 1
            continue
        report = validate_bars(
            df,
            ticker,
            kind=inst.kind,
            calendar=None if inst.kind == "fx" else universe.calendar,
            today=market_today(),
        )
        _print_report(report, args.verbose)
        bad += not report.ok
    return 1 if bad else 0


def cmd_costs(args: argparse.Namespace) -> int:
    cfg = CostConfig.load()
    hs = args.half_spread_bps
    if hs is None and args.ticker:
        hs = load_universe().get(args.ticker).half_spread_bps
    shares = args.notional / args.price
    print(f"Order: ${args.notional:,.2f} = {shares:.4f} shares @ ${args.price:,.2f}")
    print(f"{'':10}{'buy':>10}{'sell':>10}")
    buy = estimate_trade_cost(cfg, shares, args.price, BUY, half_spread_bps=hs)
    sell = estimate_trade_cost(cfg, shares, args.price, SELL, half_spread_bps=hs)
    for key in ("commission", "regulatory", "spread", "slippage", "total"):
        print(f"{key:10}{getattr(buy, key):>10.4f}{getattr(sell, key):>10.4f}")
    print(f"{'bps':10}{buy.bps:>10.2f}{sell.bps:>10.2f}")
    rt = round_trip_bps(cfg, args.notional, args.price, half_spread_bps=hs)
    fx = fx_usd(cfg, args.notional)
    print(f"\nRound trip: {rt:.2f} bps")
    print(f"One ILS->USD conversion of this amount: ${fx:.2f} ({fx / args.notional * 1e4:.2f} bps)")
    print("\nAnnual return lost to trading at different turnover (excl. tax):")
    for turnover in (0.5, 1, 2, 4, 12, 52):
        print(f"  turnover {turnover:>4}x/yr -> {annual_drag_pct(rt, turnover):5.2f}% per year")
    return 0


def cmd_membership(args: argparse.Namespace) -> int:
    from itrade.backtest.universe import MembershipRules, members_by_year, membership
    from itrade.config import load_strategy, strategy_version

    params = load_strategy(args.strategy)
    universe = load_named_universe(params["strategy"]["universe"])
    rules = MembershipRules.from_strategy(params)
    store = Store()
    table = membership(store.read_all_bars(universe.tickers), rules)

    out = store.derived_dir / "membership" / f"{universe_name(params)}.parquet"
    out.parent.mkdir(parents=True, exist_ok=True)
    table.to_parquet(out, index=False)

    print(f"Strategy {args.strategy} version {strategy_version(args.strategy)[:12]}")
    print(
        f"Rules: >= {rules.min_history_sessions} bars, "
        f"ADV{rules.liquidity_window} >= ${rules.min_avg_dollar_volume_usd:,.0f}"
    )
    print(f"Written {out}\n")
    report = members_by_year(table)
    if args.since:
        report = report[report["year"] >= args.since]
    print(report.to_string(index=False))
    return 0


def cmd_dividends(args: argparse.Namespace) -> int:
    """Screen every instrument of a universe for suspicious gaps between ex-dividend dates."""
    from itrade.backtest.corporate_actions import (
        apply_dividend_overrides,
        ex_dividend_dates,
        load_dividend_overrides,
        suspicious_dividend_gaps,
    )

    universe = load_named_universe(args.universe)
    overrides = load_dividend_overrides()
    store = Store()
    print(
        f"Dividend gap screen for {args.universe} (overrides: {len(overrides)}), since {args.since}"
    )
    for ticker in universe.tickers:
        bars = apply_dividend_overrides(store.read_bars(ticker), ticker, overrides)
        ex = ex_dividend_dates(bars)
        ex = ex[ex.year >= args.since]
        gaps = suspicious_dividend_gaps(ex)
        flagged = ", ".join(f"{a.date()}→{b.date()} ({d}d)" for a, b, d in gaps) or "-"
        print(f"  {ticker:<6} ex-dates={len(ex):<4} suspicious: {flagged}")
    print("\nSuspicious gaps are screening hints; judge each in docs/data-notes.md.")
    return 0


def cmd_calendar(args: argparse.Namespace) -> int:
    from itrade.data.calendar import export_calendar, session_table

    path = export_calendar(Store().root, args.calendar, args.start, args.end)
    table = session_table(args.calendar, args.start, args.end)
    per_year = table.groupby(table["session"].dt.year).size()
    print(f"{args.calendar}: {len(table)} sessions {args.start} .. {args.end} -> {path}")
    print(
        f"early closes: {int(table['early_close'].sum())}; sessions per year: "
        f"min {per_year.min()} max {per_year.max()}"
    )
    return 0


def cmd_signals(args: argparse.Namespace) -> int:
    """Raw entry signals (before risk limits and portfolio state) for manual review."""
    import pandas as pd

    from itrade.backtest.inputs import load_inputs
    from itrade.strategies.etf_pullback_v1 import entry_signals

    inputs = load_inputs(args.strategy)
    days = inputs.sessions[(inputs.sessions >= args.start) & (inputs.sessions <= args.end)]
    rows = []
    for t in days:
        cands, skipped = entry_signals(t, inputs.prepared, set(), inputs.signal_params)
        for rank, c in enumerate(cands, 1):
            rows.append(
                {
                    "date": t.date(),
                    "rank": rank,
                    "ticker": c.ticker,
                    "rsi": round(c.rsi, 2),
                    "adv_musd": round(c.adv / 1e6, 1),
                    "close": c.close_raw,
                    "limit": c.limit,
                    "stop": c.stop,
                    "skip": "",
                }
            )
        rows += [{"date": t.date(), "ticker": s.ticker, "skip": s.reason} for s in skipped]
    table = pd.DataFrame(rows)
    if "rank" in table:
        table["rank"] = table["rank"].astype("Int64")
    out = Store().derived_dir / "signals" / f"{args.strategy}_{args.start}_{args.end}.csv"
    out.parent.mkdir(parents=True, exist_ok=True)
    table.to_csv(out, index=False)
    taken = table[table["skip"] == ""] if len(table) else table
    print(
        f"{len(days)} sessions, {len(taken)} entry signals, "
        f"{len(table) - len(taken)} skipped -> {out}"
    )
    if len(table):
        print(table["skip"].replace("", "signal").value_counts().to_string())
    return 0


def universe_name(params: dict) -> str:
    return params["strategy"]["universe"]


def cmd_sql(args: argparse.Namespace) -> int:
    con = Store().connect()
    print(con.sql(args.query))
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="itrade", description=__doc__)
    sub = p.add_subparsers(dest="command", required=True)

    ing = sub.add_parser("ingest", help="download daily bars, snapshot, validate, promote")
    ing.add_argument("tickers", nargs="*", help="default: whole universe")
    ing.add_argument("--universe", help="config/universes/<NAME>.toml (full history)")
    ing.add_argument("--source", default="yfinance")
    ing.add_argument("--start", help="override config start_date (YYYY-MM-DD)")
    ing.add_argument("-v", "--verbose", action="store_true", help="show warnings too")
    ing.set_defaults(func=cmd_ingest)

    q = sub.add_parser("quality", help="re-run quality checks on curated data")
    q.add_argument("tickers", nargs="*")
    q.add_argument("--universe", help="config/universes/<NAME>.toml")
    q.add_argument("-v", "--verbose", action="store_true")
    q.set_defaults(func=cmd_quality)

    c = sub.add_parser("costs", help="price a hypothetical order with the cost model")
    c.add_argument("--notional", type=float, required=True, help="order size in USD")
    c.add_argument("--price", type=float, required=True, help="share price in USD")
    c.add_argument("--ticker", help="use this instrument's half-spread from universe.toml")
    c.add_argument("--half-spread-bps", type=float)
    c.set_defaults(func=cmd_costs)

    m = sub.add_parser("membership", help="universe membership by date for a strategy (spec §3)")
    m.add_argument("--strategy", default="etf_pullback_v1")
    m.add_argument("--since", type=int, default=2005, help="first year shown in the report")
    m.set_defaults(func=cmd_membership)

    dv = sub.add_parser("dividends", help="screen ex-dividend dates for suspicious gaps")
    dv.add_argument("--universe", default="etf_pullback_v1")
    dv.add_argument("--since", type=int, default=2005)
    dv.set_defaults(func=cmd_dividends)

    cal = sub.add_parser("calendar", help="export the exchange session calendar to Parquet")
    cal.add_argument("--calendar", default="XNYS")
    cal.add_argument("--start", default="2005-01-01")
    cal.add_argument("--end", default="2030-12-31")
    cal.set_defaults(func=cmd_calendar)

    sg = sub.add_parser("signals", help="raw entry signals for manual review (no risk limits)")
    sg.add_argument("--strategy", default="etf_pullback_v1")
    sg.add_argument("--start", default="2010-01-01")
    sg.add_argument("--end", default="2010-12-31")
    sg.set_defaults(func=cmd_signals)

    s = sub.add_parser("sql", help="query curated data with DuckDB (view: bars)")
    s.add_argument("query")
    s.set_defaults(func=cmd_sql)
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
