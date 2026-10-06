# iTrade — Scope (one page)

_Version 1 · 2026-10-06 · Items marked ⚠ were defaulted by Claude and need the owner's confirmation._

## The question
Can a simple, rule-based strategy beat **buy-and-hold of a global index ETF (VT)** after
commissions, spreads, slippage, FX conversion and Israeli tax, **measured in shekels**?
If no strategy clears that bar, the answer is "hold the index" — a valid, money-saving result.

## Money and who it serves
| | |
|---|---|
| Capital | 10,000 ILS (≈ $2,700). Own money only. |
| Users | The owner only. No clients, no signals sold, no copy trading. |
| Licensing | Trading your own account needs no Israel Securities Authority licence. Managing or advising **others** would — out of scope. |
| Leverage / shorting / options / crypto | None. ⚠ |

Expectation check: a very good 10%/yr on 10,000 ILS is ~1,000 ILS before 25% tax. This project
is about learning and validation; the money result is secondary.

## Jurisdiction and tax (Israel — verify with a tax adviser, this is not tax advice)
- 25% tax on realised **real** capital gains; losses offset gains in the same year and carry forward.
- With a **foreign broker** you must report and pay yourself (annual report). An **Israeli broker**
  usually withholds tax at source — simpler.
- US-domiciled ETFs: 25% US withholding on dividends (credited in Israel). Irish-domiciled UCITS
  ETFs: lower withholding inside the fund, and no US estate-tax exposure (irrelevant at this size).
- Tax is part of the cost model: active strategies realise gains every year; buy-and-hold defers them.

## Broker ⚠
**Interactive Brokers (IBKR Pro)** as the default: open to Israeli residents, has an API,
fractional shares, low minimums (~$0.35/order). Costs assumed in `config/costs.toml`.
Alternative: an Israeli broker (tax handled for you, trade in ILS) — usually higher minimum
fees and no public API. **No account is needed until phase 4.**

## Instruments ⚠
- **Research** on long-history US ETFs: SPY, VT, VEA, VWO, IEF, TLT, SHY, GLD + USD/ILS rate.
- **Holding** (if we ever go live): likely the UCITS equivalents (e.g. VWRA, CSPX) — decided at phase 4.
- Daily bars only. Rebalance **monthly or slower**; turnover target ≤ 4× per year.

## Benchmarks every strategy must beat
1. Buy-and-hold VT in ILS (SPY before 2008).
2. Honest alternatives outside this project: an Israeli index fund (קרן מחקה), and an S&P 500
   track in a keren hishtalmut — tax-exempt gains can beat any strategy here. Check these first.

## Budget
- Money: $0 (free data, local machine). Any paid data or server needs a scope update.
- Time: ~4–6 hours/week ⚠. Time box: **3 months to the phase 3 decision**.

## Stop criteria (decided in advance) ⚠
Stop and just hold the index if any of these happens:
1. **Phase 3:** no strategy beats the benchmark on the locked out-of-sample period
   (2019-01-01 onward) by ≥ 1%/yr net of costs and tax, with max drawdown no worse than the benchmark.
2. More than **20 strategy variants** tried without passing (1) — further search is curve-fitting.
3. **Live:** drawdown > 15% below the benchmark, or real slippage > 2× the model, for 3 months.
4. The 3-month time box runs out before phase 3 is complete.

## Out of scope
HFT and intraday, any client-facing feature, copy trading, leverage, paid data, cloud
infrastructure, microservices, message queues. Revisit only after live evidence (phase 6).
