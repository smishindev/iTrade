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


FINAL_FLAG = "i_understand_this_runs_once"


def ledger_path(strategy: str):
    """The hypothesis' research ledger: `[strategy].research_log` (default docs/research-log.md,
    H1). One ledger per hypothesis, so the once-only final lock is per hypothesis."""
    from itrade.config import load_strategy, project_root

    rel = load_strategy(strategy)["strategy"].get("research_log", "docs/research-log.md")
    return project_root() / rel


def cmd_backtest(args: argparse.Namespace) -> int:
    """Run a registered variant over a period, write the report, append the research ledger."""
    import os

    from itrade.backtest.inputs import is_trend
    from itrade.backtest.ledger import FinalAlreadyRun, LedgerRow, append, check_final_allowed
    from itrade.backtest.report import git_commit
    from itrade.config import load_strategy

    ledger = ledger_path(args.strategy)
    if not ledger.exists() or "## Ledger" not in ledger.read_text(encoding="utf-8"):
        # every run must be logged: no ledger (no pre-registration) -> no run
        print(f"REFUSED: no research ledger at {ledger} — pre-register the hypothesis first")
        return 3
    if args.period == "final":
        if not getattr(args, FINAL_FLAG):
            print(
                "The final period runs ONCE per hypothesis. Re-run with "
                "--i-understand-this-runs-once after recording the choice (P1.A.29)."
            )
            return 2
        try:
            check_final_allowed(ledger)
        except FinalAlreadyRun as exc:
            print(f"REFUSED: {exc}")
            return 3
        required = int(load_strategy(args.strategy)["control"]["runs"])
        if args.control < required:
            print(f"REFUSED: the final run needs its random control: --control {required}")
            return 3
        commit = git_commit()
        if commit.endswith("+dirty") or commit == "unknown":
            print(f"REFUSED: src/ or config/ has uncommitted changes ({commit}); commit first")
            return 3
        # Lock first: a crash after this row still counts as the one final run.
        append(ledger, LedgerRow(
            date=str(market_today()), variant=args.variant, period="final", trades=0,
            expectancy="started", win_rate="—", max_dd="—", control="—",
            notes=f"final run started at `{commit}`; result in the next row",
        ))  # fmt: skip

    workers = args.workers or max(1, (os.cpu_count() or 2) - 2)
    _run_and_log(args.strategy, args.variant, args.period, args.control, workers, ledger)
    params = load_strategy(args.strategy)
    if args.period == "final" and is_trend(params):
        # ETF_TREND_V2 §8 p. 4 / review S1: the chosen variant's costs_x2 runs under the same lock
        from itrade.backtest.compare import group_of

        costs = group_of(args.variant, params).costs_x2
        _run_and_log(args.strategy, costs, "final", 0, workers, ledger)
    return 0


def _run_and_log(strategy: str, variant: str, period: str, control_runs: int, workers: int, ledger):
    from pathlib import Path

    from itrade.backtest.ledger import LedgerRow, append
    from itrade.backtest.report import fmt, write_report
    from itrade.backtest.runner import random_control, run_variant

    run = run_variant(strategy, variant, period)
    control = random_control(run, control_runs, workers) if control_runs else None
    report = write_report(run, Store().root / "research", control)
    s = report.summary
    lo, hi = report.expectancy_ci
    pct = report.control_percentile
    number = append(ledger, LedgerRow(
        date=str(market_today()), variant=run.variant + (" (diag)" if run.diagnostic else ""),
        period=run.period, trades=s.trades,
        expectancy=f"{fmt(s.expectancy_r)} ({fmt(lo)} … {fmt(hi)})",
        win_rate=fmt(s.win_rate, True), max_dd=fmt(s.max_drawdown, True),
        control=f"{pct:.1f}" if pct is not None else "—",
        notes=f"run `{report.run_id}`, exec {fmt(s.executable_share, True)}, "
              f"costs {fmt(s.costs_r)}R, v`{report.meta['strategy_version'][:8]}`",
    ))  # fmt: skip
    print(
        f"ledger row #{number}: {run.variant} / {run.period}: {s.trades} trades, expectancy "
        f"{fmt(s.expectancy_r)} R ({fmt(lo)} … {fmt(hi)}), control "
        f"{f'{pct:.1f}' if pct is not None else 'not run'}"
    )
    print(f"report: {Path(report.directory) / 'report.md'}")
    return number


def cmd_compare(args: argparse.Namespace) -> int:
    """Markdown comparison of every variant run on a period (current strategy version only)."""
    from itrade.backtest.compare import comparison_markdown, load_runs
    from itrade.backtest.ledger import latest_run_ids
    from itrade.config import load_strategy, strategy_version

    params = load_strategy(args.strategy)
    version = strategy_version(args.strategy)
    latest = set(latest_run_ids(ledger_path(args.strategy), args.period).values())
    runs = load_runs(Store().root / "research", args.period, version, latest)
    if runs.empty:
        print(f"no {args.period} runs of strategy version {version[:8]}")
        return 1
    print(comparison_markdown(runs, params, args.period))
    return 0


def cmd_review(args: argparse.Namespace) -> int:
    """Re-derive sampled trades from bars and the spec; charts + review.md next to the report.
    Does not touch the research ledger (the run itself is logged by `backtest`)."""
    from itrade.backtest.engine import market_frame
    from itrade.backtest.inputs import is_trend
    from itrade.backtest.report import provenance, review_sample
    from itrade.backtest.review import write_review
    from itrade.backtest.runner import run_variant

    run = run_variant(args.strategy, args.variant, args.period)
    trades = run.result.trades
    sample = trades if args.n == 0 else review_sample(trades, args.n)
    run_id = f"{run.variant}_{run.period}_{provenance(run)['trades_sha256'][:8]}"
    out = Store().root / "research" / run_id / "review"
    market = {t: market_frame(b) for t, b in run.inputs.bars.items()}
    check = None
    if is_trend(run.inputs.params):  # ETF_TREND_V2: independent re-derivation of A / B
        from functools import partial

        from itrade.backtest.review_trend import check_trend_trade

        groups = {i.ticker: i.group or "none" for i in run.inputs.universe.instruments}
        check = partial(
            check_trend_trade, market=market, prepared=run.inputs.prepared,
            sessions=run.inputs.sessions, p=run.inputs.signal_params, groups=groups,
            equity=dict(zip(run.result.equity["date"], run.result.equity["equity"], strict=True)),
        )  # fmt: skip
    n, failed = write_review(
        sample,
        market,
        run.inputs.prepared,
        run.inputs.sessions,
        run.inputs.signal_params,
        out,
        check,
    )
    print(f"{n} trades reviewed, {failed} with a failed check: {out / 'review.md'}")
    return 1 if failed else 0


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

    bt = sub.add_parser("backtest", help="run a registered variant; report + research ledger")
    bt.add_argument("--strategy", default="etf_pullback_v1")
    bt.add_argument("--variant", default="base")
    bt.add_argument("--period", choices=["development", "validation", "final"], required=True)
    bt.add_argument("--control", type=int, default=0, help="random-control runs (spec §10)")
    bt.add_argument("--workers", type=int, default=0, help="processes for the control")
    bt.add_argument(f"--{FINAL_FLAG.replace('_', '-')}", dest=FINAL_FLAG, action="store_true")
    bt.set_defaults(func=cmd_backtest)

    cp = sub.add_parser("compare", help="variant comparison + pre-registered selection rule")
    cp.add_argument("--strategy", default="etf_pullback_v1")
    cp.add_argument("--period", choices=["development", "validation"], default="validation")
    cp.set_defaults(func=cmd_compare)

    rv = sub.add_parser("review", help="re-check sampled trades against the spec + charts")
    rv.add_argument("--strategy", default="etf_pullback_v1")
    rv.add_argument("--variant", default="base")
    rv.add_argument("--period", choices=["development", "validation"], default="development")
    rv.add_argument("--n", type=int, default=20, help="trades to sample (0 = all)")
    rv.set_defaults(func=cmd_review)

    s = sub.add_parser("sql", help="query curated data with DuckDB (view: bars)")
    s.add_argument("query")
    s.set_defaults(func=cmd_sql)
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")  # reports print → … ✓ on a cp1252 console
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
