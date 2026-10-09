# Research log — hypothesis H3 (`ETF_TOM_V3`), the last of three

Every run of H3 is recorded here by `itrade backtest` (one ledger per hypothesis). Earlier hypotheses:
H1 [research-log.md](research-log.md) (rejected 2026-10-07), H2 [research-log-H2.md](research-log-H2.md) (rejected 2026-10-08).

## Pre-registration: H3 = ETF_TOM_V3 (2026-10-09, before any H3 run)

| | |
|---|---|
| Registered | 2026-10-09, git `b51fae4`, before the first H3 backtest run (task P1.H3.04) |
| Strategy version | `f6926b8b3c2ed62de6a15641839efd505f36ce9ef37e5a879fac8e8bf5ed3ee6` (parameters + universe) |
| Specification | [docs/STRATEGY_ETF_TOM_V3.md](STRATEGY_ETF_TOM_V3.md) |
| Data | curated manifest sha256 `b22d5e9b0e5de5ab…` (Yahoo); corporate-action overrides `c85934b121bc1adc…` |
| Costs | `config/costs.toml` sha256 `ec3d5ed43ac981ee…` (IBKR Pro Tiered, account $3,280) |
| Owner decisions | 2026-10-09: candidate A (turn of the month); 80% position in whole shares; base SPY; after the code review: discovery on 2006–2020 + replication on the final (option B), variants are diagnostics only |
| Reviews before registration | quant-reviewer P1.H3.03 (blockers: control calibration, power) — fixed; control null-calibrated (200 synthetic no-edge markets × 100: ≥ 95 in 4.0%, ≥ 80 in 21.5%) |

**Hypothesis.** Returns of the broad US market concentrate around the turn of the month. Holding SPY from the open of
the month's last session to the open of the 4th session of the next month (80% of the account, whole shares, LOO entry
with a 1-ATR limit, 3-ATR emergency stop) makes **more money after costs** than equal-length windows in randomly chosen
neighbouring blocks of sessions with the same size, stop and costs.

**What is already known (disclosed).** The effect is published (Ariel 1987; Lakonishok, Smidt 1988; McConnell, Xu 2008
with data to 2005) and may have weakened. 2006–2020 data were seen on H1/H2 (ETF returns under other rules), never as
turn-of-month windows. 2021–2026 has not been run for any hypothesis. Expected size, if real: ~+3% a year at best
(about 20% of the time invested) — well below holding the index.

**Measurement.** Control: 1 000 runs, one random window per strategy window among non-overlapping blocks of the same
length ending at the strategy exit (own block included), ≥ 4 sessions after the previous exit; percentile of the run's
**total P&L**. Only windows whose scheduled exit lies inside the period count.

**Discovery — 2006-01-03 … 2020-12-31 (spec §7).** `tom_spy` with its control. Confirmed if control percentile **≥ 95**
and CAGR after costs **> 0**; otherwise H3 is rejected **without the final run**. Variants `tom_iwm`, `tom_qqq`,
`tom_basket`, `tom_entry2`, `tom_exit2`, `tom_exit4`, `tom_costs_x2` are run for the picture only (diagnostics, never
selected).

**Replication — final 2021-01-04 … 2026-09-30, once (spec §8), all must hold:** control percentile **≥ 50** · mean R > 0 ·
CAGR after costs > 0 · max drawdown ≤ 20%. Reported, not criteria: 90% block interval, trades, executable share,
`tom_costs_x2` (run under the same lock).

**Stop rule.** If H3 fails at either stage, 3 of 3 hypotheses are used — the project is reconsidered (SCOPE).

---

## Ledger

| # | Date | Variant | Period | Trades | Exp. R (90% CI) | Win % | Max DD | Control pct | Notes |
|---|------|---------|--------|--------|-----------------|-------|--------|-------------|-------|
