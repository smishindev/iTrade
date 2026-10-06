"""Command line: `uv run itrade <command>`."""

from __future__ import annotations

import argparse
import sys
from datetime import date, datetime
from zoneinfo import ZoneInfo

from itrade.config import load_universe
from itrade.costs import CostConfig, annual_drag_pct, estimate_trade_cost, round_trip_bps
from itrade.costs.model import BUY, SELL, fx_usd
from itrade.data.quality import QualityReport, validate_bars
from itrade.data.store import Store


def market_today() -> date:
    """Today's date in New York â€” the exchange's calendar, not the machine's (Israel)."""
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


def cmd_ingest(args: argparse.Namespace) -> int:
    from itrade.data.ingest import ingest
    from itrade.data.sources import get_source

    universe = load_universe()
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
    universe = load_universe()
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


def cmd_sql(args: argparse.Namespace) -> int:
    con = Store().connect()
    print(con.sql(args.query))
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="itrade", description=__doc__)
    sub = p.add_subparsers(dest="command", required=True)

    ing = sub.add_parser("ingest", help="download daily bars, snapshot, validate, promote")
    ing.add_argument("tickers", nargs="*", help="default: whole universe")
    ing.add_argument("--source", default="yfinance")
    ing.add_argument("--start", help="override config start_date (YYYY-MM-DD)")
    ing.add_argument("-v", "--verbose", action="store_true", help="show warnings too")
    ing.set_defaults(func=cmd_ingest)

    q = sub.add_parser("quality", help="re-run quality checks on curated data")
    q.add_argument("tickers", nargs="*")
    q.add_argument("-v", "--verbose", action="store_true")
    q.set_defaults(func=cmd_quality)

    c = sub.add_parser("costs", help="price a hypothetical order with the cost model")
    c.add_argument("--notional", type=float, required=True, help="order size in USD")
    c.add_argument("--price", type=float, required=True, help="share price in USD")
    c.add_argument("--ticker", help="use this instrument's half-spread from universe.toml")
    c.add_argument("--half-spread-bps", type=float)
    c.set_defaults(func=cmd_costs)

    s = sub.add_parser("sql", help="query curated data with DuckDB (view: bars)")
    s.add_argument("query")
    s.set_defaults(func=cmd_sql)
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
