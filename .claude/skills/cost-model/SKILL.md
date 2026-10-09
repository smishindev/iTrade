---
name: cost-model
description: How iTrade prices trades — commission (IBKR Pro Tiered minimums), US regulatory fees, spread, slippage, FX conversion — and how costs look in R for this small account. Load when simulating fills, changing config/costs.toml, sizing positions, or answering "how much does a trade cost".
---

# Cost model

Code: `python/src/itrade/costs/model.py` (C# port in phase 4: `ITrade.Simulation/Costs`, must match to the cent).
Assumptions: `config/costs.toml` (IBKR Pro **Tiered**, Israeli resident).

## API
```python
from itrade.costs import CostConfig, estimate_trade_cost, round_trip_bps
cfg = CostConfig.load()
tc = estimate_trade_cost(cfg, shares=3, price=60.61, side="buy", half_spread_bps=2.0)
tc.total, tc.bps, tc.as_dict()
```
- `shares` may be fractional; `side` is `"buy"` or `"sell"` (sell adds SEC + FINRA TAF).
- `convert_fx=True` adds one ILS→USD conversion (min ≈ $2) — charge it on **deposits**, not per trade.
- `capital_gains_tax(...)` is a placeholder; real tax lots and the Israeli FX rule come in phase 7 (P7.4).

## CLI
`uv run --directory python itrade costs --notional 300 --price 60.61 --ticker VWO` — per-order breakdown and round trip.

## What it means for this account (10,000 ILS ≈ $3,280; risk 0.25% ≈ $8.2 = 1R)
- A typical position is $150–650, so the **minimum commission dominates**: round trip ≈ $0.8–1.0.
- That is **≈ 0.10–0.13R per trade** before slippage on a $300 position — a big share of any realistic edge.
- Rule (PLAN §6): skip a signal when expected round-trip costs exceed **0.15R**. Never "fix" costs by raising risk.

## Changing assumptions
Every change to `config/costs.toml` needs a comment with **source and date**
(e.g. `# IBKR Pro Tiered pricing page, checked 2026-11-02`). Gate G7 re-verifies all fees against the
current IBKR price list and against real commissions from paper/Flex. Never lower costs to make a backtest pass.
