# Research log — hypothesis H2 (`ETF_TREND_V2`)

Every run of H2 is recorded here by `itrade backtest` (one ledger per hypothesis, so the once-only final lock
applies to H2 alone). H1 and its rejection: [research-log.md](research-log.md), [spikes/hypothesis-A.md](spikes/hypothesis-A.md).

## Pre-registration: H2 = ETF_TREND_V2 (2026-10-08, before any H2 run)

| | |
|---|---|
| Registered | 2026-10-08, git `ce06188`, before the first H2 backtest run (task P1.H2.10) |
| Strategy version | `22b3a82e9bacaa6a6c4fc5b9558e010f56bfff70993f31a182641fc3ceb507ee` (parameters + universe) |
| Specification | [docs/STRATEGY_ETF_TREND_V2.md](STRATEGY_ETF_TREND_V2.md) — rules A and B, sizing, control, selection, criteria |
| Data | curated manifest sha256 `b22d5e9b0e5de5ab…` (29 ETFs, Yahoo); corporate-action overrides `c85934b121bc1adc…` |
| Costs | `config/costs.toml` sha256 `ec3d5ed43ac981ee…` (IBKR Pro Tiered, real account size $3,280) |
| Owner decisions | 2026-10-08: A + B as one hypothesis; A 20% per position, B 0.5% risk, position ≤ 20%, costs ≤ 0.10R, drawdown ≤ 20%, early rejection; after the code review (quant-reviewer, before any run): measured on the account's money, unbuyable ETFs replaced by the next by rank |

**Hypothesis.** For liquid US-listed ETFs, a medium-term trend persists more often than it reverses. Holding the
strongest ETFs (rules A — monthly rotation by 6-month momentum above SMA200, top 3, one per correlation group) or
buying a close above the 55-session closing high (rules B — channel breakout, exit below the 20-session low), held
for weeks to months with a broker-side protective stop, has a positive expectancy in R **after costs at the real
account size**, and beats random choices with the same filters, sizing and exits.

**What is already known (disclosed).** The development and validation periods were already seen on H1 (short-term
pullbacks, rejected). H2 rules were written before any H2 run, but not blind to those years. The final period
2021-01-04 … 2026-09-30 has not been run for any hypothesis.

**Periods:** development 2006-01-03 … 2016-12-30 (debugging) · validation 2017-01-03 … 2020-12-31 (variant comparison) ·
**final 2021-01-04 … 2026-09-30 — one run of one variant + its `costs_x2` + random control, only if not rejected early**.

**Variants (18 of the 20 allowed; spec §7):** rotation — `rot_base`, `rot_mom63`, `rot_mom252`, `rot_top2`, `rot_top4`,
`rot_no_trend`, `rot_stop2`, `rot_stop4`, `rot_fractional`*, diagnostic `rot_costs_x2`; breakout — `brk_base`,
`brk_20_10`, `brk_100_40`, `brk_trend`, `brk_stop3`, `brk_risk1`, `brk_fractional`*, diagnostic `brk_costs_x2`.
(*candidate only if spike B confirms fractional shares with a stop, P1.B.10.)

**R** = trade result after all costs and dividends / planned risk `q · (limit − unit)`; unit = the stop for B and
`C − 3·ATR20` for every rotation variant (lessons from H1 and the review). 90% intervals: block bootstrap by exit month.
**Random control** (1 000 runs per variant): percentile of the run's **total P&L** (same capital, sizing, costs); the
rotation control copies the strategy's persistence (calibrated on a no-edge synthetic market: 20% ≥ 80, 5% ≥ 95).

**Selection rule (spec §8).** In each group the base is used unless a family variant's validation control percentile
is ≥ 10 points higher than the base's and every member of its family (base included) has CAGR > 0; between the two
group picks, the higher validation control percentile wins; **early rejection**: if the pick's validation CAGR ≤ 0 or its control percentile
< 80, H2 is rejected without the final run. Diagnostics are never selected. Every validation candidate runs with its
1 000-run control.

**Success criteria on the final period — all must hold (spec §9):** mean R > 0 with 90% block-bootstrap lower bound
> 0 · ≥ 60 trades · random-control percentile (total P&L) ≥ 95 · `costs_x2` of the same group CAGR > 0 on the final
and most neighbours CAGR > 0 on validation · executable signals ≥ 70% · max drawdown ≤ 20% · CAGR after costs > 0
(reported next to SPY and the equal-weight universe). The final runs the chosen variant, its control and its group's
`costs_x2` in one locked command.

**Stop rule.** If H2 fails (early or on the final period), 2 of 3 hypotheses are used (SCOPE, stop criteria).

---

## Ledger

| # | Date | Variant | Period | Trades | Exp. R (90% CI) | Win % | Max DD | Control pct | Notes |
|---|------|---------|--------|--------|-----------------|-------|--------|-------------|-------|
| 1 | 2026-10-07 | rot_base | development | 168 | 0.048 (-0.067 … 0.168) | 41.7% | 15.1% | 77.4 | run `rot_base_development_bf04ac09`, exec 99.4%, costs 0.033R, v`22b3a82e` · **superseded by #3: identical trades and numbers; the row order of the trades table was not deterministic (fixed in c38ff8e)** |
| 2 | 2026-10-07 | brk_base | development | 193 | 0.223 (0.050 … 0.406) | 37.8% | 8.6% | 98.5 | run `brk_base_development_c8582914`, exec 96.7%, costs 0.066R, v`22b3a82e` |
| 3 | 2026-10-07 | rot_base | development | 168 | 0.048 (-0.067 … 0.168) | 41.7% | 15.1% | 77.4 | run `rot_base_development_5db4ddf5`, exec 99.4%, costs 0.033R, v`22b3a82e` |
