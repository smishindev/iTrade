# Data notes

Explanations for quality-check warnings that were reviewed and accepted. Gate G1 requires every
warning from `uv run itrade quality --universe etf_pullback_v1 -v` and `uv run itrade quality -v`
to have an entry here.

**Review of 2026-10-07 (task P1.A.06, agent `data-auditor`):** both runs — **0 errors, 32 warnings**
(split_event 14, return_outlier 7, zero_volume 9, ohlc_inconsistent 1, gap 1); no missing sessions,
non-session dates, stale series or missing values. Manifest: all 33 curated files match their raw
snapshot (SHA-256 and row count). **Nothing blocks the USD price data used by the strategy.**
USD/ILS (`ILS=X`) is not reliable enough for ILS results — see "Blocking for ILS results".

## Splits (all consistent: `close` and `volume` split-adjusted, no jump vs adj_close)

| Ticker | Check | Dates | Reviewed | Explanation / action |
|--------|-------|-------|----------|----------------------|
| QQQ | split_event | 2000-03-20 | 2026-10-07 | 2:1. close −2.8% = adj_close move (Nasdaq down day). Before 2006: warm-up only. |
| IWM, EFA, EEM | split_event | 2005-06-09 | 2026-10-07 | 2:1 / 3:1 / 3:1. Day moves +1.0/+0.4/+0.5% = adj_close; volume continuous. Warm-up only. |
| EEM, FXI | split_event | 2008-07-24 | 2026-10-07 | 3:1. −3.7% / −4.3% = adj_close, shared with the market (SPY −2.1%). Pre-split volume spike 3–4× does not affect the $50M ADV rule. |
| VWO | split_event | 2008-06-18 | 2026-10-07 | 2:1. +0.9% = adj_close; volume adjusted. (Default universe.) |
| XLF | split_event | 2016-09-19 | 2026-10-07 | Ratio 1.231 is Yahoo's encoding of the **XLRE spin-off** distribution, not a split. Price continuous; raw = adjusted × 1.231 matches the real level. Spec S(d) treats it as a value-equivalent adjustment — accepted. |
| EWJ, EWU | split_event | 2016-11-07 | 2026-10-07 | Reverse splits 1:4 / 1:2. +0.7% / +1.5% = adj_close. |
| XLK, XLE, XLY, XLU, XLB | split_event | 2025-12-05 | 2026-10-07 | 2:1 splits of the Select Sector SPDRs. −0.9…+0.7% = adj_close; implied pre-split prices match late-2025 levels. Broker-side order adjustment to be confirmed in P1.B. |

## Return outliers (all real market events, shared by related ETFs, not reversed)

| Ticker | Check | Dates | Reviewed | Explanation / action |
|--------|-------|-------|----------|----------------------|
| EEM, EWA, EWY, EWZ, FXI, VWO | return_outlier | 2008-10-13 | 2026-10-07 | Global rally after the G7/EU bank rescue: +20…+26%; SPY +14.5%, EFA +15.9%, VT +13.5%. Keep. |
| EEM, EWY, FXI | return_outlier | 2008-10-28 | 2026-10-07 | +20…+21%; SPY +11.7%, EWG +19.8%, VWO +17.0%. Keep. |
| EWY | return_outlier | 2008-10-30 | 2026-10-07 | +22.1% after the Fed–Bank of Korea swap line; EM peers +13…+14%. Keep. |
| XLE | return_outlier | 2020-03-09 | 2026-10-07 | −20.1% oil price-war crash; EWZ −15.1%, SPY −7.8%. Keep. |
| EWZ | return_outlier | 2020-03-16 | 2026-10-07 | −23.1% COVID crash + weaker BRL; SPY −10.9%, EWA −16.1%. Keep. |
| FXI | return_outlier | 2022-03-16 | 2026-10-07 | +21.2% after Beijing's market-support pledge; EEM +8.1%. Keep. |

## Zero-volume bars (no-trade days in early ETF history, before membership)

| Ticker | Check | Dates | Reviewed | Explanation / action |
|--------|-------|-------|----------|----------------------|
| EWG, EWU, EWC, EWA | zero_volume | 1996–2003 (9 / 59 / 104 / 20 rows) | 2026-10-07 | Flat no-trade bars after launch. Before 2005: warm-up and 756-bar count only. Accept. |
| XLI | zero_volume | 1999-01-26 | 2026-10-07 | One no-trade bar in the launch year. Accept. |
| EWZ, EWY | zero_volume | EWZ 33 rows 2000-07…2001-10; EWY 3 rows 2000–2001 | 2026-10-07 | No-trade bars in the first year. Accept. |
| INDA | zero_volume | 25 rows in 2012 | 2026-10-07 | New ETF (ADV ≈ $0.2M). Cannot be a member before ~2015-02 (756 bars) and the $50M rule; Wilder RSI/ATR effect decays long before. Accept. |
| XLRE | zero_volume | 5 rows 2015-10…2016-01 | 2026-10-07 | New ETF (ADV ≈ $0.17M); membership not before ~2018-10. Accept. |

## Blocking for ILS results (not for USD signals or R)

| Ticker | Check | Dates | Reviewed | Explanation / action |
|--------|-------|-------|----------|----------------------|
| ILS=X | bad_tick (manual) | 2011-03-01, 2016-08-01, 2016-12-26 | 2026-10-07 | **Isolated wrong closes** reversed next day: 3.280 vs 3.641/3.634 (−9.9% / +10.8%); 3.697 vs 3.823/3.812; 3.739 vs 3.812/3.809. Not flagged by the checks. **Decision: ILS conversion uses the Bank of Israel representative rate** (also the rate for Israeli tax), not ILS=X. 2020-03-24 (+2.9%/−3.0%) plausible; verify against BoI. |
| ILS=X | gap | 2008-08-01…2008-08-25 (17 business days) | 2026-10-07 | Vendor gap inside the development period, longer than any acceptable fill. Covered by the same decision (BoI rate). |
| ILS=X | ohlc_inconsistent | 211 rows, 2005–2026 | 2026-10-07 | Quote snapshots: close outside high/low by a median 0.07%; in 2020–2025 close = open (start-of-day London quote), i.e. not a US-close rate. Another reason to use the BoI rate. |

## Dividends (manual checks — inputs for P1.A.08)

| Ticker | Check | Dates | Reviewed | Explanation / action |
|--------|-------|-------|----------|----------------------|
| QQQ; TLT, SHY; VWO; INDA | dividends missing (manual) | QQQ ~2020-09-21; TLT/SHY ~2012-11-01; VWO ~2026-03-20; INDA irregular | 2026-10-07 | Probable missing ex-dividend rows in Yahoo. Effect: the "no entry if t+1 is ex-date" filter misses those days; ~0.1–0.25% of dividend cash not credited. Cross-check with issuer/IBKR in **P1.A.08**. Not blocking. |
| EWC, EWG, EWZ, EWA, INDA, EWU … | large distributions (manual) | e.g. EWZ 2022-12-13 (6.9%), 2021-12-13 (6.5%), EWA 2008-12-23 (6.5%), INDA 2021-12-13 (5.9%); 117 ex-dates ≥ 2% overall | 2026-10-07 | Not a data error: `close` is not dividend-adjusted, so an ex-date appears as a price drop. **Strategy design question** (RSI(2) can fire on a mechanical ex-dividend drop) — recorded in the P1 inbox for a decision before pre-registration (P1.A.25). |

## Other observations

- **VT** traded thinly in Oct 2008 (52k–390k shares/day); daily closes out of step with the market. Irrelevant for signals (fails the $50M rule then) — matters only if VT were a benchmark (spec §7 uses SPY).
