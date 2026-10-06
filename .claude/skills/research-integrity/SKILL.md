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
3. **Report in ILS, after tax.** Convert USD equity with the `ILS=X` close of the same day;
   apply `capital_gains_tax` per calendar year to realised gains; include FX conversion cost on deposits.
4. **Always beside the benchmark.** Buy-and-hold VT (SPY before 2008-06-26), same costs, same tax,
   same currency. Show the difference, not just the strategy.
5. **Out-of-sample is locked.** 2019-01-01 onward is the hold-out. Develop on data before it.
   Run on the hold-out once per strategy, at the end, and say so in the research log.
6. **Log every variant.** Before running variant N+1, record variant N in `docs/research-log.md`
   (use `/research-log`). Stop criterion: 20 variants without a pass → stop (docs/SCOPE.md).

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
