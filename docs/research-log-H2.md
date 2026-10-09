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

## Decision P1.H2.14 (2026-10-08, owner): H2 rejected on validation — final period not run

- The pre-registered rule (spec §8) picks `rot_mom63` (validation CAGR +2.7%, control percentile on total P&L **43.1**
  < 80) → early rejection. No candidate above the 59th percentile; every variant −0.8…+2.7% a year vs SPY +15.7%.
- Review P1.H2.13 (quant-reviewer): trustworthy; 43.1 reproduces; the control matches the strategy; other random draws
  37–57. Report: [spikes/hypothesis-H2-validation.md](spikes/hypothesis-H2-validation.md).
- **Decision:** H2 is rejected by its own rule. The final period 2021-01-04 … 2026-09-30 stays **unseen**.
- Hypotheses used: **2 of 3** (SCOPE, stop criteria).

## Ledger

| # | Date | Variant | Period | Trades | Exp. R (90% CI) | Win % | Max DD | Control pct | Notes |
|---|------|---------|--------|--------|-----------------|-------|--------|-------------|-------|
| 1 | 2026-10-07 | rot_base | development | 168 | 0.048 (-0.067 … 0.168) | 41.7% | 15.1% | 77.4 | run `rot_base_development_bf04ac09`, exec 99.4%, costs 0.033R, v`22b3a82e` · **superseded by #3: identical trades and numbers; the row order of the trades table was not deterministic (fixed in c38ff8e)** |
| 2 | 2026-10-07 | brk_base | development | 193 | 0.223 (0.050 … 0.406) | 37.8% | 8.6% | 98.5 | run `brk_base_development_c8582914`, exec 96.7%, costs 0.066R, v`22b3a82e` |
| 3 | 2026-10-07 | rot_base | development | 168 | 0.048 (-0.067 … 0.168) | 41.7% | 15.1% | 77.4 | run `rot_base_development_5db4ddf5`, exec 99.4%, costs 0.033R, v`22b3a82e` |
| 4 | 2026-10-07 | rot_base | validation | 81 | -0.007 (-0.240 … 0.247) | 38.3% | 18.6% | 4.6 | run `rot_base_validation_9a2d169f`, exec 97.6%, costs 0.044R, v`22b3a82e` |
| 5 | 2026-10-07 | rot_mom63 | validation | 91 | 0.149 (-0.062 … 0.384) | 51.6% | 13.2% | 43.1 | run `rot_mom63_validation_9b085b6a`, exec 100.0%, costs 0.043R, v`22b3a82e` |
| 6 | 2026-10-07 | rot_mom252 | validation | 67 | 0.023 (-0.233 … 0.316) | 35.8% | 14.4% | 15.8 | run `rot_mom252_validation_877a6d72`, exec 95.7%, costs 0.042R, v`22b3a82e` |
| 7 | 2026-10-07 | rot_top2 | validation | 54 | 0.045 (-0.179 … 0.285) | 44.4% | 12.3% | 12.2 | run `rot_top2_validation_60fa718b`, exec 100.0%, costs 0.040R, v`22b3a82e` |
| 8 | 2026-10-07 | rot_top4 | validation | 98 | 0.080 (-0.154 … 0.332) | 37.8% | 20.2% | 12.8 | run `rot_top4_validation_6e568107`, exec 96.1%, costs 0.045R, v`22b3a82e` |
| 9 | 2026-10-07 | rot_no_trend | validation | 81 | -0.008 (-0.245 … 0.249) | 38.3% | 19.9% | 2.0 | run `rot_no_trend_validation_776d9952`, exec 96.4%, costs 0.044R, v`22b3a82e` |
| 10 | 2026-10-07 | rot_stop2 | validation | 91 | -0.016 (-0.215 … 0.206) | 31.9% | 19.3% | 4.2 | run `rot_stop2_validation_c724b6c5`, exec 97.8%, costs 0.045R, v`22b3a82e` |
| 11 | 2026-10-07 | rot_stop4 | validation | 75 | 0.051 (-0.216 … 0.339) | 42.7% | 16.3% | 11.6 | run `rot_stop4_validation_c1c8229e`, exec 97.4%, costs 0.044R, v`22b3a82e` |
| 12 | 2026-10-07 | brk_base | validation | 93 | -0.059 (-0.338 … 0.267) | 31.2% | 12.4% | 7.6 | run `brk_base_validation_ef91c23f`, exec 84.9%, costs 0.083R, v`22b3a82e` |
| 13 | 2026-10-07 | brk_20_10 | validation | 146 | 0.103 (-0.080 … 0.308) | 39.0% | 7.6% | 49.1 | run `brk_20_10_validation_4793702d`, exec 83.9%, costs 0.080R, v`22b3a82e` |
| 14 | 2026-10-07 | brk_100_40 | validation | 64 | -0.114 (-0.521 … 0.425) | 18.8% | 14.1% | 1.3 | run `brk_100_40_validation_80b27bef`, exec 83.7%, costs 0.083R, v`22b3a82e` |
| 15 | 2026-10-07 | brk_trend | validation | 93 | 0.004 (-0.267 … 0.312) | 32.3% | 10.0% | 23.3 | run `brk_trend_validation_f5db5124`, exec 84.6%, costs 0.082R, v`22b3a82e` |
| 16 | 2026-10-07 | brk_stop3 | validation | 74 | 0.120 (-0.154 … 0.409) | 43.2% | 7.6% | 59.3 | run `brk_stop3_validation_ecd3f6c7`, exec 94.5%, costs 0.072R, v`22b3a82e` |
| 17 | 2026-10-07 | brk_risk1 | validation | 93 | -0.048 (-0.328 … 0.281) | 31.2% | 15.3% | 5.7 | run `brk_risk1_validation_6bcf4237`, exec 85.3%, costs 0.073R, v`22b3a82e` |
| 18 | 2026-10-07 | rot_fractional | validation | 80 | 0.044 (-0.186 … 0.291) | 40.0% | 17.6% | 15.6 | run `rot_fractional_validation_0c9f3331`, exec 97.6%, costs 0.042R, v`22b3a82e` |
| 19 | 2026-10-07 | brk_fractional | validation | 93 | 0.012 (-0.304 … 0.375) | 31.2% | 10.8% | 13.5 | run `brk_fractional_validation_f32f2f55`, exec 87.0%, costs 0.079R, v`22b3a82e` |
| 20 | 2026-10-07 | rot_costs_x2 (diag) | validation | 81 | -0.052 (-0.285 … 0.203) | 37.0% | 20.0% | 4.4 | run `rot_costs_x2_validation_07b5d703`, exec 97.6%, costs 0.089R, v`22b3a82e` |
| 21 | 2026-10-07 | brk_costs_x2 (diag) | validation | 94 | -0.182 (-0.442 … 0.125) | 28.7% | 15.2% | 8.2 | run `brk_costs_x2_validation_026eed8c`, exec 84.3%, costs 0.168R, v`22b3a82e` |
| 22 | 2026-10-09 | — | closed | 0 | — | — | — | — | H2 closed: rejected by its early-rejection rule (P1.H2.14, 2026-10-08); final period unseen |
