---
name: cost-model
description: How iTrade prices trades — commission, regulatory fees, spread, slippage, FX conversion and Israeli capital-gains tax. Load when simulating fills, changing config/costs.toml, comparing strategies by turnover, or answering "how much does trading cost me".
---

# Cost model

Code: `src/itrade/costs/model.py`. Assumptions: `config/costs.toml` (IBKR Pro tiered, Israeli resident).

## API
```python
from itrade.costs import CostConfig, estimate_trade_cost, round_trip_bps, annual_drag_pct, capital_gains_tax
cfg = CostConfig.load()
tc = estimate_trade_cost(cfg, shares=4.15, price=650.0, side="buy", half_spread_bps=0.5)
tc.total, tc.bps, tc.as_dict()
```
- `shares` may be fractional. `side` is `"buy"` or `"sell"` (sell adds SEC + FINRA TAF).
- `convert_fx=True` adds one ILS→USD conversion (min $2) — charge it on **deposits**, not on every trade.
- `capital_gains_tax({year: realised_gain_ils}, rate)` → tax per year with loss carry-forward.
  It taxes nominal gains (Israel taxes real gains) — slightly conservative, keep it that way.

## CLI
`uv run itrade costs --notional 2700 --price 650 --ticker SPY` — per-order breakdown,
round trip in bps, and yearly drag at turnover 0.5× … 52×.

## What the numbers say for this account (~$2,700)
- Round trip on a liquid ETF ≈ 8 bps. At ≤ 4× turnover/yr that is ≤ 0.3%/yr — acceptable.
- Weekly rebalancing ≈ 4%/yr drag — kills any realistic edge.
- The FX minimum ($2 ≈ 7 bps on $2,700) and tax drag from realising gains yearly can matter
  more than commissions. Buy-and-hold pays tax once, at the end.

## Changing assumptions
Every change to `config/costs.toml` needs a comment with **source and date**
(e.g. `# IBKR pricing page, checked 2026-11-02`). Gate 4 requires re-verifying all fees
against the broker's current price list. Never lower costs to make a backtest pass.
