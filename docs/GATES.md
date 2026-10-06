# Phase gates

A phase is finished when **every** exit criterion has evidence. Run `/gate-check <phase>` in
Claude Code; the `gate-keeper` agent checks each line and reports PASS / FAIL / NEEDS-OWNER.
Only the owner moves `phase` in `config/project.toml`. Hooks block broker and order code
while `phase < 4`.

## Gate 0 — Scope
- [ ] Owner has reviewed every ⚠ item in `docs/SCOPE.md` and accepted or changed it.
- [ ] Stop criteria are written down **before** any strategy results exist.

## Gate 1 — Data (current)
- [x] `uv run itrade ingest` downloads the whole universe and the USD/ILS rate.
- [x] Raw snapshots are immutable and timestamped; curated files trace back to a raw snapshot by SHA-256.
- [x] Quality checks: duplicates, ordering, missing values, non-positive prices, OHLC consistency,
      negative/zero volume, missing/extra exchange sessions, return outliers, staleness, split events.
- [x] Unit tests cover every quality check without network access.
- [ ] Every remaining **warning** from `uv run itrade quality -v` has been reviewed and explained
      in `docs/data-notes.md` (e.g. VWO 2008 split, USD/ILS OHLC noise).

## Gate 2 — Backtester and costs
- [x] Cost model: commission with min/cap, US regulatory fees, spread, slippage, FX, Israeli CGT with carry-forward.
- [x] `uv run itrade costs` prices any order and shows yearly drag by turnover.
- [ ] Backtester trades on the **next** bar after the signal (no same-bar fills).
- [ ] Every simulated trade goes through `itrade.costs.estimate_trade_cost`.
- [ ] Results are reported in ILS, after tax, next to the benchmark.
- [ ] Sanity tests: buy-and-hold SPY reproduces SPY's adjusted return within 0.1%/yr;
      a zero-signal strategy loses exactly its costs; a strategy that knows the future is caught by a test.
- [ ] Out-of-sample period (2019-01-01 onward) is locked: the code refuses to use it unless called in `final` mode.

## Gate 3 — Research
- [ ] Every variant tried is logged in `docs/research-log.md` (the multiple-testing ledger).
- [ ] At most 20 variants. The best one passes the stop criterion 1 in SCOPE.md on out-of-sample data, run once.
- [ ] Results survive costs × 2 and a ±1 month shift in the rebalance date.
- [ ] `quant-reviewer` agent finds no look-ahead, survivorship or cost-omission issues.
- **If this gate fails → stop, hold the benchmark. That is a success, not a failure.**

## Gate 4 — Paper trading
- [ ] Broker account opened; fees in `config/costs.toml` re-verified against the broker's current price list (date + source noted).
- [ ] Credentials only in environment variables / OS keychain, never in the repo.
- [ ] Hard limits outside the strategy: max order size, max position, daily loss stop, stale-data stop, kill switch.
- [ ] Reconciliation: broker positions == local positions every run, or the run halts.
- [ ] 8+ weeks of paper fills; measured slippage ≤ model assumption.

## Gate 5 — Small live
- [ ] 10–25% of capital for 3+ months; stop criterion 3 not triggered.

## Gate 6 — Scale
- [ ] Only with live evidence. Re-read SCOPE.md; update it before adding capital or complexity.
