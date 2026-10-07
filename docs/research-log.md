# Research log — the multiple-testing ledger

Every run of a strategy variant gets one row in the ledger below, **including failures**, and the
hypothesis, variants and success criteria are registered **before** the first run. Runs are appended
by `itrade backtest` (P1.A.24); manual notes via `/research-log`.

---

## Pre-registration: H1 = ETF_PULLBACK_V1 (2026-10-07, before any backtest run)

| | |
|---|---|
| Registered | 2026-10-07, git `da4f9ca`, before the first backtest run (task P1.A.25) |
| Strategy version | `843f5684df30daa673a77cc5d3e2c5da1bc9baba32ac72290a3aba465577eba9` (parameters + universe) |
| Specification | [docs/STRATEGY_ETF_PULLBACK_V1.md](STRATEGY_ETF_PULLBACK_V1.md) — rules, fills, costs, limits |
| Data | curated manifest sha256 `b22d5e9b0e5de5ab…` (29 ETFs, Yahoo, to 2026-10-06); corporate-action overrides `c85934b121bc1adc…` |
| Costs | `config/costs.toml` sha256 `ec3d5ed43ac981ee…` (IBKR Pro Tiered, real account size $3,280) |

**Hypothesis.** For liquid US-listed ETFs in an uptrend (close* > SMA200), a sharp short-term pullback
(RSI(2) ≤ 10 on the dividend-neutral series) bought at the next open (LOO) and sold at the next open after
close* > SMA5 (max 10 sessions, 2×ATR(14) stop) has a positive expectancy in R **after costs at the real
account size**, and beats random entries with the same filters, sizing and exits.

**Periods** (spec §8): development 2006-01-03 … 2016-12-30 · validation 2017-01-03 … 2020-12-31 ·
**final 2021-01-04 … 2026-09-30 — one run of one variant + its random control**.

**Variants (14 of the 20 allowed; spec §9, `[[variants]]` in the strategy TOML):**

| Variant | Change vs base | Type |
|---|---|---|
| base | — (whole shares) | candidate — **primary hypothesis** |
| rsi5 / rsi15 | RSI threshold 5 / 15 | candidate |
| sma150 / sma250 | trend SMA 150 / 250 | candidate |
| exit_sma3 / exit_sma10 | exit SMA 3 / 10 | candidate |
| hold5 / hold15 | max holding 5 / 15 sessions | candidate |
| fractional | fractional shares | candidate only if spike B confirms fractional shares with a stop (P1.B.10) |
| costs_x2 | costs × 2 | diagnostic |
| skip_one_day_per_week | no session on Wednesdays (owner absent) | diagnostic |
| no_stop | no protective stop | diagnostic |
| raw_close_indicators | indicators on raw close (no dividend neutralisation) | diagnostic |

**Success criteria on the final period — all must hold** (spec §11): expectancy ≥ +0.10R per trade with the
90% bootstrap lower bound > 0 · ≥ 100 trades · random-control percentile ≥ 95 · `costs_x2` expectancy > 0 and
most neighbouring variants > 0 on validation · executable signals ≥ 70% · max drawdown ≤ 5%.

**Selection rule for the final run (fixed now, owner confirms in P1.A.29).** The final run uses **base**, unless
a candidate beats base on the validation period by ≥ +0.05R expectancy **and** both its neighbours in the same
parameter family have positive validation expectancy. Diagnostics are never selected. Development-period results
are used only to find bugs, never to choose parameters.

**Stop rule.** If the final period fails any criterion, H1 is rejected (SCOPE «Критерии остановки»; at most
three hypotheses before the project is reconsidered).

---

## Ledger

| # | Date | Variant | Period | Trades | Exp. R (90% CI) | Win % | Max DD | Control pct | Notes |
|---|------|---------|--------|--------|-----------------|-------|--------|-------------|-------|
| 1 | 2026-10-07 | base | development | 668 | -0.155 (-0.216 … -0.097) | 54.2% | 10.8% | 49.7 | run `base_development_eaae0591`, exec 59.9%, costs 0.206R, v`843f5684` |
