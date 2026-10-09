# Research log — the multiple-testing ledger

> H1 (`ETF_PULLBACK_V1`) is closed — rejected 2026-10-07. Hypothesis H2 (`ETF_TREND_V2`) has its own ledger:
> [research-log-H2.md](research-log-H2.md); H3 (`ETF_TOM_V3`): [research-log-H3.md](research-log-H3.md).

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

## Decision P1.A.29 (2026-10-07, owner): H1 rejected on validation — final period not run

- Validation 2017–2020 (ledger #2–#17, report `docs/spikes/hypothesis-A-validation.md`): every one of the 14 variants
  is negative after costs, upper 90% bound < 0 for all; base −0.177R (−0.270 … −0.081), 283 trades, random-control
  percentile 35.9; gross (before costs) +0.015R — no edge even at zero cost. The selection rule gives `base`.
- Review P1.A.28 (quant-reviewer): no bug or look-ahead that changes the sign.
- **Decision:** H1 is rejected without the final run. The final period 2021-01-04 … 2026-09-30 stays **unseen** and is
  kept for the next hypothesis. This departs from the protocol (one final run per hypothesis) only in the direction of
  rejection; GATES G1 records it.
- Hypotheses used: **1 of 3** (SCOPE, stop criteria).

## Ledger

| # | Date | Variant | Period | Trades | Exp. R (90% CI) | Win % | Max DD | Control pct | Notes |
|---|------|---------|--------|--------|-----------------|-------|--------|-------------|-------|
| 1 | 2026-10-07 | base | development | 668 | -0.155 (-0.216 … -0.097) | 54.2% | 10.8% | 49.7 | run `base_development_eaae0591`, exec 59.9%, costs 0.206R, v`843f5684` |
| 2 | 2026-10-07 | base | validation | 283 | -0.177 (-0.270 … -0.081) | 49.1% | 8.2% | 35.9 | run `base_validation_17f57067`, exec 52.5%, costs 0.192R, v`843f5684` |
| 3 | 2026-10-07 | rsi5 | validation | 167 | -0.181 (-0.327 … -0.032) | 52.7% | 5.2% | 38.8 | run `rsi5_validation_11db6258`, exec 55.5%, costs 0.223R, v`843f5684` |
| 4 | 2026-10-07 | rsi15 | validation | 375 | -0.110 (-0.193 … -0.022) | 52.0% | 9.0% | 98.5 | run `rsi15_validation_aca4b3a8`, exec 50.2%, costs 0.193R, v`843f5684` |
| 5 | 2026-10-07 | sma150 | validation | 274 | -0.205 (-0.300 … -0.104) | 47.4% | 8.5% | 13.6 | run `sma150_validation_0b6b8127`, exec 52.7%, costs 0.202R, v`843f5684` |
| 6 | 2026-10-07 | sma250 | validation | 288 | -0.198 (-0.291 … -0.102) | 47.6% | 8.6% | 21.3 | run `sma250_validation_a3df7920`, exec 51.1%, costs 0.199R, v`843f5684` |
| 7 | 2026-10-07 | exit_sma3 | validation | 302 | -0.148 (-0.242 … -0.045) | 42.4% | 8.1% | 67.9 | run `exit_sma3_validation_08954362`, exec 51.7%, costs 0.195R, v`843f5684` |
| 8 | 2026-10-07 | exit_sma10 | validation | 260 | -0.175 (-0.281 … -0.068) | 50.0% | 7.2% | 31.4 | run `exit_sma10_validation_071991cf`, exec 52.3%, costs 0.194R, v`843f5684` |
| 9 | 2026-10-07 | hold5 | validation | 285 | -0.178 (-0.271 … -0.080) | 47.4% | 8.3% | 36.3 | run `hold5_validation_fc4e3961`, exec 52.3%, costs 0.193R, v`843f5684` |
| 10 | 2026-10-07 | hold15 | validation | 283 | -0.177 (-0.270 … -0.081) | 49.1% | 8.2% | 35.9 | run `hold15_validation_17f57067`, exec 52.5%, costs 0.192R, v`843f5684` |
| 11 | 2026-10-07 | fractional | validation | 341 | -0.145 (-0.240 … -0.048) | 51.0% | 9.4% | 62.8 | run `fractional_validation_488cb7cc`, exec 87.3%, costs 0.195R, v`843f5684` |
| 12 | 2026-10-07 | costs_x2 (diag) | validation | 0 | — (— … —) | — | 0.0% | nan | run `costs_x2_validation_9eb40896`, exec 0.0%, costs —R, v`843f5684` · **superseded: bug — ×2 also applied to the §5.3 #8 cost gate, every signal rejected; fixed and re-run (spec §9)** |
| 13 | 2026-10-07 | skip_one_day_per_week (diag) | validation | 240 | -0.237 (-0.343 … -0.124) | 45.8% | 7.8% | 1.6 | run `skip_one_day_per_week_validation_8f17ef30`, exec 53.8%, costs 0.208R, v`843f5684` |
| 14 | 2026-10-07 | no_stop (diag) | validation | 3 | -0.532 (-0.921 … -0.144) | 0.0% | 0.3% | 36.2 | run `no_stop_validation_1ca4983c`, exec 0.3%, costs 0.199R, v`843f5684` · **superseded: bug — stop_atr = 0 put the stop at the signal close; fixed and re-run (spec §9)** |
| 15 | 2026-10-07 | raw_close_indicators (diag) | validation | 286 | -0.146 (-0.243 … -0.042) | 51.4% | 7.4% | 72.4 | run `raw_close_indicators_validation_abef66b5`, exec 54.1%, costs 0.200R, v`843f5684` |
| 16 | 2026-10-07 | costs_x2 (diag) | validation | 255 | -0.355 (-0.464 … -0.242) | 34.9% | 12.0% | 49.1 | run `costs_x2_validation_a32a5010`, exec 40.0%, costs 0.413R, v`843f5684` |
| 17 | 2026-10-07 | no_stop (diag) | validation | 226 | -0.179 (-0.355 … -0.032) | 54.9% | 7.1% | 20.7 | run `no_stop_validation_abc839a3`, exec 53.5%, costs 0.191R, v`843f5684` |
