---
name: research-integrity
description: Rules for any backtest, strategy, signal or performance-metric code in iTrade. Load before writing or changing anything under src/itrade/backtest, src/itrade/strategies or src/itrade/signals, before running a backtest, and before reporting any strategy result to the user.
---

# Research integrity

A backtest that looks great is usually wrong. These rules exist to catch the four ways it
happens: look-ahead, survivorship, missing costs, and multiple testing.

## Hard rules

1. **No look-ahead.** A decision made at the close of day *t* trades at the **open or close of
   day t+1**, never on day *t*. Signals use only rows `<= t`.
   - Forbidden in signal code: `shift(-n)`, `bfill`, `rolling(center=True)`, full-sample
     normalisation (z-scores, min/max over the whole series), fitting on the whole period.
   - A post-edit hook flags these; silence it only with `# lookahead-ok: <reason>`.
2. **Costs always on.** Every simulated fill goes through
   `itrade.costs.estimate_trade_cost(...)` with the instrument's `half_spread_bps` from
   `config/universe.toml`. A "zero-cost" run may exist only as a labelled diagnostic, never as a result.
3. **Report in ILS, after tax.** Simulate on raw close + explicit dividends (never adj_close plus
   dividends — double counting). Tax per Israeli rules via the tax engine (lots, USD gain × exit
   rate, dividends with withholding, loss carry-forward). Report wealth "taxes paid" and "if
   liquidated today". Include FX conversion cost on deposits.
4. **Always beside both benchmarks.** (a) Buy-and-hold VT (SPY before 2008-06-26); (b) a static
   portfolio with the strategy's average asset mix. Same engine, costs, tax, currency.
   Beating VT by holding fewer stocks is not skill.
5. **Out-of-sample is locked.** 2019-01-01 onward is the hold-out. Develop on data before it.
   Run on the hold-out once per strategy, at the end, and say so in the research log.
6. **Pre-register, then log every variant.** Write the hypothesis, parameters and success
   criterion in `docs/research-log.md` *before* the first run (use `/research-log`). Stop
   criterion: 20 variants without a pass → stop (docs/SCOPE.md). Once the hold-out has been
   looked at and the strategy changed, it is no longer out-of-sample — say so.
7. **Time-stamp availability, not just dates.** News, LLM scores and macro data are usable only
   from the moment they were available (fetched / scored / published vintage — ALFRED, not FRED).

## Metrics to report (always all of them)
Net CAGR (ILS, after tax) · benchmark CAGR · difference · max drawdown and its dates ·
annualised volatility · Sharpe (rf = SHY) · turnover per year · cost drag %/yr · tax drag %/yr ·
number of trades · worst calendar year.

## Robustness checks before calling anything "good"
- Costs × 2 still beats the benchmark?
- Shift the rebalance day by ±5 trading days — similar result?
- Each sub-period (2005–2010, 2011–2015, 2016–2018) — does it work in most, or only one?
- Is the edge mostly one asset or one year? Then it is luck until shown otherwise.

## Wording when reporting to the user
State uncertainty. "Beat the benchmark by 1.2%/yr in-sample over 14 years, 3 of 3 sub-periods"
— never "this strategy makes 12% a year". Mention how many variants were tried.

After writing strategy/backtest code, ask the `quant-reviewer` agent to review it.
