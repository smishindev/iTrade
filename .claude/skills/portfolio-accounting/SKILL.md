---
name: portfolio-accounting
description: How iTrade keeps the owner's books — the append-only ledger of transactions (signed effects, kinds, linked entries, reversal, repeatable import), instruments and their ticker history, holding places — and the rules for code that derives lots, positions, cash, valuation, returns and reconciliation from it. Load before writing or reviewing anything under Portfolio in src/ITrade.*, a statement import (Flex, CSV, manual entry), FX rates or prices used for valuation, or before telling the owner a balance, a position or a return.
---

# Portfolio accounting

`docs/PLAN.md` §3–§4 and the cards in `docs/roadmap/P3-accounting.md` win over this summary. Keep this file
current: when a task of phase 3 lands, move its rule from "what comes next" to "what exists".
C# style, money types and layers: the `dotnet-conventions` skill. Tax amounts: the `israeli-tax` skill.

## What exists (P3.1.01, P3.1.02)
| Thing | Domain / Application | Table |
|---|---|---|
| What can be held | `Instrument`, `Ticker`, `Isin`; `InstrumentCatalogue` (repeatable import, renames) | `instruments`, `instrument_former_tickers` |
| Where it is held | `HoldingPlace` (broker, study fund, other; read automatically or by hand). In use: IBKR only — the owner decided on 2026-10-12 not to keep keren hishtalmut in the platform; the `StudyFund` kind stays in the code unused | `holding_places` |
| What happened | `Transaction`; `TransactionLedger` (`RecordAsync`, `ReverseAsync`) | `transactions` |

## The ledger
- **One entry = one effect**: a change of the shares of one instrument and/or of the cash in one currency
  at one holding place. Signs are effects — a buy is `+shares, −cash` — so a balance on any date is a sum.
- **A kind fixes the signs** (`Transaction` refuses anything else; for a reversal the rule is applied
  to the entry it cancels):

  | Kind | Shares | Cash | Instrument | Price |
  |---|---|---|---|---|
  | Buy | + | − | required | required |
  | Sell | − | + | required | required |
  | Dividend | 0 | + | required | — |
  | TaxWithheld, Fee | 0 | − | optional | — |
  | Interest, FxConversion | 0 | ≠ 0 | none | — |
  | Deposit | 0 | + | none | — |
  | Withdrawal | 0 | − | none | — |
  | CorporateAction | any | any, not both zero | required | — |

- **Composite events are linked entries** (`RelatedTo`): a trade and its commission; a dividend and the
  tax withheld from it; the two legs of a currency conversion (one entry per currency). The entry that
  hangs on another is given after it.
- **Exact numbers**: `decimal`, six places (`numeric(18,6)`). A value with more places is refused, never
  rounded. The price of a trade is in the currency of its cash.
- **Dates**: the trade date is the calendar date of the place (New York for IBKR), the settlement date is
  when cash and shares move. `RecordedAt` is when the platform wrote the entry, nothing more.
- **Order**: trade date, then the order of recording (`sequence`, numbered by the database). Within a day
  it is the order of the statement — do not sort by `RecordedAt` or by id.
- **Append-only**: the database rejects UPDATE, DELETE and TRUNCATE on `transactions`. A mistake is
  corrected by `ReverseAsync` (the entry and what hangs on it get opposite entries) and then the right
  entry. An entry is reversed once; a reversal is never reversed.
- **Repeatable import**: an entry with an external id is known by (holding place, source, external id)
  and recorded once. A line that now states other facts is a **conflict**: reported, not applied.
  Entries typed in by hand have no external id — the same line entered twice is two entries.
- **Sources**: `Flex` is the broker's own books and the source of truth for money; `ApiExecution` is
  the same day's report from the API; `Manual` is the owner or a statement file of a place without
  integration. How an API execution and the Flex line of the same trade are matched is not decided yet
  (phase 5) — do not record both as two trades.
- All writes go through `TransactionLedger`. Nothing else inserts into `transactions`, and nothing —
  no code, no migration, no SQL by hand — changes or removes a row.

## Rules for what comes next (lots, positions, FX, prices, valuation, reconciliation)
- **Derived, not stored as truth.** Lots, positions and cash balances are computed from the ledger by
  pure functions in `ITrade.Domain` (entries in, result out; the date is a parameter). A cached or
  stored result must be reproducible from the ledger and is never corrected by hand.
- **Reversals cancel.** A reversed entry and its reversal take no part in lot matching or in a balance
  "as it really was"; a reversed buy leaves no lot behind.
- **Lot matching is FIFO, as a named assumption** (the owner decided on 2026-10-12 to go by our own
  judgement and not to wait for the accountant; `israeli-tax`): the method is a parameter, and results
  say which method they used.
- **Impossible states are errors, not zeros**: selling more than is held, a sale before its buy, a split
  of an instrument that is not held, an entry for an unknown instrument. Report what is wrong and stop.
- **Currencies do not add up.** Cash is kept per currency; a value in shekels exists only through an
  `FxRate` that carries its date and source (Bank of Israel representative rate). A day without a rate
  or without a price is **flagged**, never filled silently; a carried-forward value says so.
- **Prices from the free data pipeline are for valuation only** — never for tax, never for an order.
- **No rounding inside a calculation**; round once, where a figure is shown, with a named rule.
- **Returns**: a contribution is not a gain. Time-weighted and money-weighted returns state their
  period, their cash flows and what they include (fees, withheld tax).
- **Reconciliation never repairs.** A difference between the ledger and the broker's statement is shown
  to the owner with both numbers; it is closed by an import or by a reversal with a reason.
- **Broker statements carry the real account number**: the original file stays in the data folder,
  fixtures in the repository are anonymised (`U0000000`), and logs never print it.

## Tests
- Hand-worked examples from the task card, to the last digit, in each currency.
- Properties (CsCheck): the lots of an instrument add up to its position; importing twice equals
  importing once; a reversal restores every balance; splitting a sale in two changes nothing.
- Whatever the database enforces is tested on a real PostgreSQL (`tests/ITrade.IntegrationTests`), with
  a planted violation seen to fail. Both defects of P3.1.02 were invisible to the unit tests.
- After a change here, the `accounting-reviewer` agent reads it before the commit.

## Wording to the owner
A balance or a position is "by the ledger, as of <date>" with its currency, and "not yet compared with
the broker" until a reconciliation has run. A value in shekels names the rate and its date.
