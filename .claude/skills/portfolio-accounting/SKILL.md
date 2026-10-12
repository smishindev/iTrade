---
name: portfolio-accounting
description: How iTrade keeps the owner's books — the append-only ledger of transactions (signed effects, kinds, linked entries, reversal, repeatable import), instruments and their ticker history, holding places — and the rules for code that derives lots, positions, cash, valuation, returns and reconciliation from it. Load before writing or reviewing anything under Portfolio in src/ITrade.*, a statement import (Flex, CSV, manual entry), FX rates or prices used for valuation, or before telling the owner a balance, a position or a return.
---

# Portfolio accounting

`docs/PLAN.md` §3–§4 and the cards in `docs/roadmap/P3-accounting.md` win over this summary. Keep this file
current: when a task of phase 3 lands, move its rule from "what comes next" to "what exists".
C# style, money types and layers: the `dotnet-conventions` skill. Tax amounts: the `israeli-tax` skill.

## What exists (P3.1.01 – P3.1.03)
| Thing | Domain / Application | Table |
|---|---|---|
| What can be held | `Instrument`, `Ticker`, `Isin`; `InstrumentCatalogue` (repeatable import, renames) | `instruments`, `instrument_former_tickers` |
| Where it is held | `HoldingPlace` (broker, study fund, other; read automatically or by hand). In use: IBKR only — the owner decided on 2026-10-12 not to keep keren hishtalmut in the platform; the `StudyFund` kind stays in the code unused | `holding_places` |
| What happened | `Transaction`; `TransactionLedger` (`RecordAsync`, `ReverseAsync`) | `transactions` |
| What is held | `PortfolioBooks.From(ledger, asOf)` → `Position`, `Lot`, `Disposal`, `CashBalance`; `PortfolioBooksReader` | none — computed from the ledger every time |

## The ledger
- **One entry = one effect**: a change of the shares of one instrument and/or of the cash in one currency
  at one holding place. Signs are effects — a buy is `+shares, −cash` — so a balance on any date is a sum.
- **A kind fixes the signs** (`Transaction` refuses anything else; for a reversal the rule is applied
  to the entry it cancels):

  | Kind | Shares | Cash | Instrument | Price |
  |---|---|---|---|---|
  | Buy | + | − | required | required |
  | Sell | − | + | required | required |
  | Dividend | 0 | ≠ 0 (normally +) | required | — |
  | TaxWithheld, Fee | 0 | ≠ 0 (normally −) | optional | — |
  | Interest, FxConversion | 0 | ≠ 0 | none | — |
  | Deposit | 0 | + | none | — |
  | Withdrawal | 0 | − | none | — |
  | CorporateAction | any | any, not both zero | required | — |

  The broker's own adjustments — a dividend taken back, withheld tax refunded, a rebate — carry the
  other sign and do not name the line they adjust, so they are ordinary entries, not reversals.
- **Composite events are linked entries** (`RelatedTo`): a trade and its commission; a dividend and the
  tax withheld from it; the two legs of a currency conversion (one entry per currency). The entry that
  hangs on another is given after it, at the same holding place, and **links are one level deep**: an
  entry hangs on a main entry, never on another hanging entry.
- **Exact numbers**: `decimal`, six places (`numeric(18,6)`). A value with more places is refused, never
  rounded. The price of a trade is in the currency of its cash.
- **Dates**: the trade date is the calendar date of the place (New York for IBKR), the settlement date is
  when cash and shares move. `RecordedAt` is when the platform wrote the entry, nothing more.
- **Order**: trade date, then the order of recording (`sequence`, numbered by the database). Within a day
  it is the order of the statement — do not sort by `RecordedAt` or by id.
- **Append-only**: the database rejects UPDATE, DELETE and TRUNCATE on `transactions`. A mistake is
  corrected by `ReverseAsync` (the entry and what hangs on it get opposite entries) and then the right
  entry. An entry is reversed once; a reversal is never reversed and is the **exact opposite** of its
  target (same place, kind, dates, instrument, price; opposite shares and cash). A commission or a
  withheld tax may be reversed alone; one leg of a conversion may not — reverse the first leg.
- **Repeatable import**: an entry from a source (`Flex`, `ApiExecution`) **must** carry the source's id
  of the line; it is known by (holding place, source, external id) and recorded once. Only entries
  typed in by hand have no external id — the same line entered twice is two entries.
- **What `RecordAsync` reports instead of recording** (`LedgerResult.NotRecorded`, for the owner):
  a line the ledger holds with other facts (the report names every differing fact and the stored
  entry); a line whose recording was reversed while the source still states the same; a line that
  hangs on either of those, or on a reversed entry. Nothing is ever overwritten.
- **A corrected line**: the owner reverses the old recording, the next import records the line again as
  the next `ExternalRevision` (0, 1, …), and what hung on it comes back with it.
- **A broken batch throws and records nothing**: a link to nowhere, a second level of links, a link
  across holding places, a reversal that is not the opposite of its target, a second reversal.
- **The database repeats these rules** (migration `LedgerRules`: CHECK constraints for kinds, signs,
  instrument, price, external id, revision; a BEFORE INSERT trigger for links and reversals). A row is
  permanent, so a rule added to `Transaction` gets its constraint — and a planted violation in
  `LedgerRulesTests` — in the same change.
- `TransactionStore.AddAsync` writes the ledger on its own: it refuses a context with other unsaved
  changes, and inside a caller's transaction a refused batch is rolled back to a savepoint.
- **Sources**: `Flex` is the broker's own books and the source of truth for money; `ApiExecution` is
  the same day's report from the API; `Manual` is the owner or a statement file of a place without
  integration. How an API execution and the Flex line of the same trade are matched is not decided yet
  (phase 5) — do not record both as two trades.
- All writes go through `TransactionLedger`. Nothing else inserts into `transactions`, and nothing —
  no code, no migration, no SQL by hand — changes or removes a row.

## The books (`PortfolioBooks`, P3.1.03)
- **Derived, not stored as truth.** Lots, positions and cash are computed from the ledger by a pure
  function (entries in ledger order in, books out; the date is a parameter). Nothing is cached yet; a
  stored result would have to be reproducible from the ledger and is never corrected by hand.
- **Dates are trade dates**: a purchase counts from the day of the trade, cash too. Settled cash (what
  can be spent) is a separate question for phase 5.
- **Reversals cancel.** A reversed entry and its reversal take no part, on any date; a reversed buy
  leaves no lot.
- **A lot** is the shares of one purchase still held: quantity, cost (without commission) and the
  purchase commission kept apart, so that the tax module decides what belongs to the cost basis.
  A commission is found by its link to the trade, whenever it was recorded.
- **Lot matching is FIFO, as a named assumption** (owner, 2026-10-12: by our own judgement, not waiting
  for the accountant; `israeli-tax`). `LotMatching` is a parameter and the result names it. "First" is
  the ledger's order: trade date, then order of recording.
- **A `Disposal`** is the part of a sale taken from one lot: cost, both commissions, proceeds.
  `NominalGain` is in the currency of the trade and is **not** the taxable amount.
- **Parts add up exactly.** A proportional part (of a lot's cost, of a sale's proceeds, of the shares
  after a split) is kept to 12 decimal places (`PartScale`) and the last part takes the remainder.
- **A split or reverse split** is a `CorporateAction` that changes shares without cash: the lots keep
  cost and purchase date and share the new number of shares in proportion. The entry carries the
  change of shares, not a ratio, so an import gives it **before** that day's trades (after a trade of
  its day it is refused: the lots would come out wrong while the totals looked right). Nothing else is a
  zero-cash share change yet: a spin-off, a merger, a transfer in or a change of instrument id cannot
  be written down, and an import must refuse them rather than guess.
- **Corporate-action cash** (cash in lieu of a fraction, a return of capital) is counted in the cash and
  listed in `PortfolioBooks.NotInTheLots`: what it means for the lots is not worked out. Code that
  computes a gain or a tax from `Disposals` refuses an instrument named there.
- **Refused, not smoothed over** (`InvalidOperationException` naming the entry): selling more than is
  held, a sale before its buy, a split of something not held or one that removes every share, a
  corporate action that moves shares and cash at once, a commission in another currency than its
  trade, a second currency for one instrument, entries out of the ledger's order. Entries after the
  date asked for cannot break the books of that date.

## Rules for what comes next (FX, prices, valuation, reconciliation)
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
