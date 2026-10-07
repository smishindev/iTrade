---
name: research-integrity
description: Rules for any backtest, strategy, signal, indicator or performance-metric work in iTrade (Python spike A in src/itrade/strategies and src/itrade/backtest, or the C# core in ITrade.Strategies / ITrade.Simulation). Load before writing or changing that code, before running a backtest, and before reporting any strategy result to the owner.
---

# Research integrity

A backtest that looks great is usually wrong. These rules catch the usual ways it happens:
look-ahead, survivorship, missing costs, wrong fills, and multiple testing.
The strategy spec is `docs/STRATEGY_ETF_PULLBACK_V1.md` (PLAN §5); it wins over this summary.

## Hard rules

1. **No look-ahead.** Decision after the close of session *t* using only bars ≤ *t*; execution at
   the **open of t+1** (LOO entry, MOO exit). Stops act intraday from t+1 on; entry at the open
   always comes before that day's low.
   - Forbidden in signal code: `shift(-n)`, `bfill`, `rolling(center=True)`, whole-sample
     normalisation or fitting. Hook flags these; silence only with `# lookahead-ok: <reason>`.
   - Tests must prove: changing data after *t* does not change signals at *t*; a strategy that peeks is caught.
2. **Realistic fills.** LOO fills only if open ≤ limit; stop gap → fill at the open; slippage on every fill;
   whole vs fractional shares as configured — an unfillable size is a **skipped signal with a reason**.
3. **Costs always on**, priced at the **real account size** (IBKR minimum commissions) via the cost model.
   Report costs in R. A zero-cost run is a labelled diagnostic, never a result.
4. **One account.** Limited, settled (T+1) cash; max positions and risk limits (PLAN §6); signals
   compete in the pre-registered order.
5. **Prices:** indicators on the **dividend-neutral** series O*,H*,L*,C* = split-adjusted price × M_t,
   where M_t accumulates only dividends with ex-date ≤ t (spec §1) — never Yahoo adj_close (it is
   back-adjusted with future dividends). Fills, quantities and costs on raw prices; dividends as
   separate cash events (25% US withholding). No new entry when t+1 is an ex-date.
6. **Universe by date.** An instrument joins only when it met the rules on that date (history, liquidity).
   Document closed/merged ETFs that free data cannot include.
7. **Periods are fixed:** development 2006–2016, validation 2017–2020, **final 2021-01 … 2026-09 runs
   once** per hypothesis (the CLI enforces it). Once the final period was seen and rules changed, it is
   no longer out-of-sample — say so.
8. **Pre-register, then log every run.** Hypothesis, variants (max 20) and success criteria go into
   `docs/research-log.md` before the first run; every run is logged, failures included.

## What every result must show
Number of trades · expectancy in R after costs with 90% bootstrap interval · win rate with Wilson
interval · average win / loss in R · costs in R · max drawdown and its duration · worst losing streak ·
share of executable signals and reasons for skipped ones · results by year, instrument and group ·
**random-control percentile** (same filters, frequency, exits) · costs × 2 and neighbouring parameters ·
a simple market reference (buy-and-hold of a broad ETF) for context.

## Wording to the owner
State uncertainty and sample size: "+0.12R per trade after costs on 2017–2020, 90% CI −0.02…+0.25,
143 trades, 7 of 20 variants used, above 93% of random entries". Never "this makes X% a year",
never "profitable" before the final period and live evidence.

After writing strategy/backtest code, run `/review-research` (agent `quant-reviewer`).
