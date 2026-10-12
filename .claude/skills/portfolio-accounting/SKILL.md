---
name: portfolio-accounting
description: How iTrade keeps the owner's books — the append-only ledger of transactions (signed effects, kinds, linked entries, reversal, repeatable import), instruments and their ticker history, holding places — and the rules for code that derives lots, positions, cash, valuation, returns and reconciliation from it. Load before writing or reviewing anything under Portfolio in src/ITrade.*, a statement import (Flex, CSV, manual entry), FX rates or prices used for valuation, or before telling the owner a balance, a position or a return.
---

# Portfolio accounting

`docs/PLAN.md` §3–§4 and the cards in `docs/roadmap/P3-accounting.md` win over this summary. Keep this file
current: when a task of phase 3 lands, move its rule from "what comes next" to "what exists".
C# style, money types and layers: the `dotnet-conventions` skill. Tax amounts: the `israeli-tax` skill.

## What exists (P3.1.01 – P3.1.03, P3.3.01, P3.3.02, P3.4.01, P3.4.02, P3.4.04)
| Thing | Domain / Application | Table |
|---|---|---|
| What can be held | `Instrument`, `Ticker`, `Isin`; `InstrumentCatalogue` (repeatable import, renames) | `instruments`, `instrument_former_tickers` |
| Where it is held | `HoldingPlace` (broker, study fund, other; read automatically or by hand). In use: IBKR only — the owner decided on 2026-10-12 not to keep keren hishtalmut in the platform; the `StudyFund` kind stays in the code unused | `holding_places` |
| What happened | `Transaction`; `TransactionLedger` (`RecordAsync`, `ReverseAsync`) | `transactions` |
| Exchange rates | `FxRate`, `AppliedFxRate`; `FxRates` (`UpdateAsync`, `OnAsync`), job `fx-rates`; source `BankOfIsraelRates` | `fx_rates` |
| Closing prices | `ClosingPrice`, `PriceOnDate`; `ClosingPrices` (`ImportAsync`, `OnAsync`); file `ClosingPricesFile` | `closing_prices`, `market_sessions`, `price_imports` |
| What a fund paid and how it split | `FundEvent` (dividend per share as paid, split ratio); `ClosingPrices.EventsAsync` | `fund_events` |
| What is held | `PortfolioBooks.From(ledger, asOf)` → `Position`, `Lot`, `Disposal`, `CashBalance`; `PortfolioBooksReader` | none — computed from the ledger every time |
| What it is worth | `PortfolioValue.Of(books, prices, rate)` → `PositionValue`, `PortfolioTotal`; `PortfolioValuation.AsOfAsync(date)` | none — worked out every time |
| What it earned | `PeriodReturns.Of(points)`, `OwnerFlow.Of(ledger, first, last)`; `PortfolioPerformance.OverAsync(first, last)`; oracle `python/src/itrade/accounting/returns.py` | none |
| What the index would have earned | `IndexPortfolio.Following(points, events, closes, dividendKept)`; `PerformanceReport.Index` | none |

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

## Exchange rates (`FxRates`, P3.3.01)
- **The rate is the Bank of Israel representative rate** of the dollar in shekels (PLAN §6), read from the
  bank's public service: one rate per published day since 2005, none on weekends and Israeli holidays.
  `FxRate` says "1 USD = rate ILS" and carries its date and source; at most six decimals are kept, a finer
  rate is refused, never rounded.
- **`fx_rates` is append-only** (database triggers, as for the ledger). An update reads the last 14 days
  again and stops (`FxRatesDisagreeException`, nothing stored) when the source states another rate for a
  stored date, or a new rate moves more than 8% from the nearest earlier rate known — in the answer or
  stored — or has none within 7 days to be judged by (the largest real move since 2005 is 3.76%). The
  message carries a **code of exactly that finding**. Only the owner gets past it, after
  looking at the bank's own page: `update-fx-rates --accept-changes <code> "<what was checked>"`. The
  source is read again; if it says anything else by then, or nothing is left to accept, nothing is
  accepted. A changed rate is stored as the next **revision** of its date and an unusual one as the first
  rate of its date, each with the reason; the old rate stays; reads give the latest revision. The daily
  job never accepts anything, and neither does Claude.
- A stored rate the source leaves out of an answer is kept and reported (`NotRepeated`); it stops nothing.
  A rate that arrives late for an earlier date is added and named (`Late`): that date used a carried rate
  until then. Every update that stored or noticed something writes an audit event in the same step as the
  rates (`fx-rates.updated`, `fx-rates.noted`, `fx-rates.changes-accepted`) — the daily job too; `check`
  shows the latest note for two weeks.
- **`FxRates.OnAsync(date)` is the one way to get a rate**, and it returns an `AppliedFxRate`:
  the rate of the day; or the latest earlier one, **carried forward** (`DaysCarried`) over a weekend or a
  holiday; and **provisional** (`IsProvisional`) while no later rate is stored — today before the
  afternoon publication, or rates that were not updated — because that figure can still change. There
  is no rate for a date after today, and none when the latest earlier one is more than 7 days old (the
  source's longest silence): it throws instead of returning an old rate. "Not provisional" is as final
  as a rate gets, not a promise — a late or revised rate can still change the answer for a past date.
- **Keep the `AppliedFxRate` next to every amount converted with it** and show its marks: once only the
  shekel amount is left, nothing says how good the rate was. A provisional figure is said to be so in words.
- **For tax a carried rate is not automatically right**: which date's rate applies to a trade on a day
  without one is an open assumption (`israeli-tax`), and a provisional rate is never good enough for a
  tax figure. Tax code states the rule it uses and does not take `OnAsync` for granted.
- **Currencies do not add up.** Cash is kept per currency; a value in shekels exists only through an
  `FxRate`. A day without a rate or without a price is **flagged**, never filled silently.

## Closing prices (`ClosingPrices`, P3.3.02)
- **For valuation only** — never for tax, never for an order. They come from the free data pipeline.
- **The price as traded, not the vendor's adjusted close.** The vendor rewrites history after every split
  (a 2:1 split halves all earlier closes); the ledger holds the shares really held on a date, so the price
  must be the one really paid that day. The pipeline undoes the adjustment (`itrade export-closes`, also at
  the end of every ingest; the catalogue's funds are `itrade ingest --universe etf_pullback_v1`). On the split day the price is already the new one — and the books apply the
  split before the trades of that day, so shares and price agree.
- **Only the close of a finished session.** A bar fetched while its session was open is the last trade so
  far: the exporter leaves it out (session over = the exchange's close + 30 minutes, from the exchange
  calendar), and the import refuses a close whose data was fetched before 16:00 New York time of its day.
  The owner's evening is the middle of the New York session — this is the usual case, not a rare one.
- **Sessions come from the exchange calendar, not from the prices.** The export carries every session of
  XNYS from the first bar to the last finished session (`closing-prices-sessions.csv`); the platform keeps
  them in `market_sessions`. That is what tells a closed exchange (carry Friday's close over the weekend)
  from a gap in the data (say "missing") — also when the vendor gave *nobody* a price for a session.
- **The export** is three files in `<data>/derived/closing-prices/`: the prices, the sessions, and a
  manifest with the SHA-256 of both, the fetch time, currency and split days of each ticker. A value that
  is not a quoted price (cents, a fraction of a dollar before 2001, 3–4 decimals) stops the export.
- **`import-prices`** takes the tables only if they are exactly the ones the manifest vouches for, matches
  tickers to the catalogue (others are skipped and named) and stores what is new. `closing_prices` is
  append-only: a close the vendor corrected is the next revision. It is refused whole when the export is
  older than the one imported last (it would undo corrections), when a close is of a day that is no
  session, when the sessions contradict the stored ones, or when the export states another currency than
  the instrument's. (The export takes its currency from the catalogue file, not from the vendor: this
  catches a catalogue that disagrees with the database, not a vendor quoting in pence. Every fund is in
  dollars today; the first one that is not needs the quote currency from the vendor.)
- **What only the owner can accept** (by the code printed with the finding; never accept for the owner):
  more than 20 revised prices; any stored price restated by more than 2%; a new close more than 30% away
  from the close before it on a day the export names no split for (the largest real one-day move in the
  stored history, split days aside, is 25.6%). Up to 20 revisions within 2% are recorded unasked and listed.
- **`ClosingPrices.OnAsync(instrument, date)` is the one way to get a price**, and its `PriceOnDate` says
  what it is: the session's own close; the last session's close **carried** over a day the calendar says
  the exchange was closed; **missing** when the session the date goes by has no close for this instrument
  — a gap is said, never filled with an older price; or **unavailable** (after today in New York, before
  the instrument's first price, or more than 4 days past the last stored session). Past the last stored
  session a carried close is **provisional**: the exchange may have been open since.
- **Who is behind is said.** The import and `check` name the instruments whose last close is older than
  the latest session — from then on they have no price.
- **A value needs a price that exists.** A position whose price is missing or unavailable is shown as
  not valued; it is not valued at an old price and not left out of a total without saying so.
- **Known limits:** a session cannot be withdrawn - if the calendar library calls a day of an
  unscheduled closure a session, every fund reads "missing" for it, and after the library is corrected the
  imports are refused until a migration deals with it; an export made from an older data folder carries a
  fresh date and passes the "not older" check (it can put back up to 20 corrections of at most 2%).
- A dividend's ex-date dip is a real price move, not an error: the dividend arrives as its own ledger entry.

## The value on a date (`PortfolioValue`, P3.4.01)
- **`PortfolioValuation.AsOfAsync(date)` is the one way to get a value**: the books of the trade date from
  the ledger (all holding places), `ClosingPrices.OnAsync` for every instrument held, `FxRates.FindAsync`
  for the rate. Nothing is stored; the figure is worked out from the ledger, prices and rates as they are
  now — a later reversal, a revised price or a late rate changes the value of a past date.
- **For looking at, never for tax.** The closes of session D are converted with the Bank of Israel rate of
  calendar date D, set some hours before that close: this platform's convention, a named assumption.
- **A position has a value only with a usable price** (`PositionValueState`): missing or unavailable price
  → not valued; a close of a session **before a split the books have applied since** → not valued (new
  shares × old price would be double or half the truth; the close of the split's own day is the new one).
  Never valued at an older price, never at zero.
- **A total is called a total only when nothing is left out.** `PortfolioTotal.Valued` is what could be
  valued and converted; `Total` is null while `NotValued` (positions) or `NotConverted` (sums in another
  currency, when there is no rate) is not empty. Show `Valued` as "partly valued", with what is missing.
- **Amounts are added in their own currency and converted once**, exactly (dollars to shekels multiplies;
  the other way divides). No rounding inside; round where the figure is shown.
- **What is provisional is said**: `IsProvisional` on a total (a provisional close, or a provisional rate
  that was used for it) and `PortfolioValue.Remarks(nameOf)` — the sentences to show next to the figures:
  not valued and why, provisional price, provisional rate, no rate. Every position keeps its
  `PriceOnDate`, the value its `AppliedFxRate`.
- **Refused**: a price of another date or instrument than asked, a rate of another date, a price in
  another currency than the position was bought in.
- **Known limit:** if the vendor's prices already reflect a split that the ledger does not hold yet (the
  statement import lags), old shares × new price shows half the value and nothing here can see it — the
  reconciliation (P3.4.03) has to compare the split days of the price export with the ledger.

## Returns (`PeriodReturns`, P3.4.02)
- **A contribution is not a gain, a withdrawal is not a loss.** `PeriodReturns.Gain` is the money earned:
  closing value + withdrawn − opening value − contributed.
- **Two returns, never one figure called "the return".** *Time-weighted*: what one unit of money made that
  was there all the time — independent of when and how much was paid in; the one to compare with an index.
  *Money-weighted*: the single rate that turns the owner's own contributions into what they became (what a
  spreadsheet calls XIRR). Say which one a figure is.
- **Timing, a named assumption:** a contribution counts from the start of its day (the end of the day
  before), a withdrawal at the end of its day. Step factor = (value + taken out) / (value before + paid in).
- **The owner's flows are the ledger's deposits and withdrawals** (`OwnerFlow.Of`; reversed ones are none).
  Dividends, interest, fees, withheld tax and conversions happen inside the portfolio: they are return.
- **Valued around the flows, not every day:** the day before the period, every day with a flow, the day
  before each contribution, the last day (`PerformanceReport.ValuedOn`). A contribution whose day before
  is not valued is refused — the rise before it would be credited to the new money.
- **In dollars and in shekels separately**: every value and every flow at the rate that goes with its own
  day. The two differ by what the dollar did against the shekel.
- **Per year only from a year on**: 365 days *at work* (`DaysAtWork` — days on which there was money in
  the portfolio; a stretch in which it stood empty is not counted). A few weeks are not stretched.
- **No money-weighted figure for a period with an empty stretch in it.** The rate per year while invested
  stays; compounded across days on which the money was out it would claim earnings that were not made.
- **Money that turns up while nothing is invested** (a dividend after everything was taken out, or a
  contribution the ledger does not hold) is in `Gain` and in `GainedOnNothing`, and in neither return;
  `PerformanceIn.Notes` says so. Not zero and not small: look for the missing contribution.
- **No return over a hole.** If a value of a needed day is not a complete total, there are no returns in
  that currency and `PerformanceIn.WhyNone` names the day and the reason. Provisional values make the
  returns provisional (`IsProvisional`), and that is said.
- **Money is `decimal`; a rate that needs a power or a root is computed in floating point** and rounded to
  ten places (`RateScale`). The time-weighted return over the period is a chain of exact divisions.
- **The oracle** is Python on purpose and by another route (exact fractions; the annual-rate equation). The
  fixture `tests/ITrade.UnitTests/Fixtures/returns-oracle.json` is written by it
  (`ITRADE_UPDATE_FIXTURES=1 uv run pytest tests/test_returns_oracle.py`) and read by the C# tests.
- **Known limits:** a transfer between two holding places would count as a withdrawal and a deposit (one
  place is in use); flows that change sign more than once can have several rates and the search returns
  one of them; the figures are before tax on gains. For looking at, never for tax.

## The index (`IndexPortfolio`, `fund_events`, P3.4.04)
- **The comparison is with a portfolio, not with a price.** `IndexPortfolio.Following` puts the same money
  on the same days into the index fund and is then measured by the same `PeriodReturns`: what was there at
  the opening and every contribution buy the fund **at the close of their own day** (the first price anyone
  could have had — no looking ahead), a withdrawal sells at the close of its day. Each of the portfolio's
  returns has its counterpart: time-weighted beside time-weighted, money-weighted beside money-weighted.
- **Dividends are in it.** The portfolio's value includes the dividends it received; a fund's price does
  not. The index portfolio buys more of the fund with each dividend at the close of its ex-date, after what
  is withheld (`ITrade:DividendWithheld`, 0.25 — the assumption of `[tax] dividend_rate` in
  `config/costs.toml`, 2026-10-06, unverified). A comparison on prices alone would show the portfolio ahead
  by the dividend yield, about 2% a year.
- **A split changes the shares, as it changed the price.** Prices are as traded, so without the split a
  2:1 split would read as a fall by half.
- **`fund_events`** holds each fund's dividends (per share, as paid — the vendor's figure multiplied back by
  the later splits) and splits (new shares per old), from the same export as the closing prices
  (`closing-prices-events.csv`, its SHA-256 in the manifest). Append-only with revisions; an event belongs
  to a session. A restated event is recorded as the next revision and named by the import; nothing about
  it is asked. A stored event the export no longer states (the vendor moved an ex-date, dropped a doubled
  dividend) **stays and is named** (`EventsNotRepeated`): it cannot be withdrawn yet, and the index is
  followed with it. Dividends the vendor is known to lack come from `config/corporate_actions.toml` (each
  with a second source) and are in the export; the manifest names them (`dividends_corrected`). **These are
  the market's facts about a fund, not the owner's**: what the owner received and what was withheld is in
  the ledger, from the broker's statement.
- **Which fund is the index** is a setting (`ITrade:IndexTicker`, VT) and must be in the catalogue.
- **No comparison rather than a wrong one** (`IndexPerformance.WhyNone`): the fund is not in the catalogue;
  the portfolio's own values are not complete; a close of the fund is missing on a day that is needed (a
  valued day or a dividend day); more is withdrawn than the index portfolio holds — it cannot follow then.
- **Provisional follows**: the index's figures are provisional when a close of the fund is, when the rate
  its value was told with is, or when the flows it follows were told at a provisional rate.
- **It flatters the index**: no commission, no spread, the dividend reinvested on its ex-date and not on
  its pay date. A portfolio that holds only the index fund, bought at the close of each contribution's day,
  matches it — that is tested. For looking at, never for tax.
- **Known limits:** dividends come from the free data vendor and, but for the recorded corrections, were
  not checked against the fund's own announcements (`docs/data-notes.md` lists gaps without a second
  source: TLT, EWG, EWY, INDA); a special distribution (capital gains, return of capital) is counted as a
  dividend; a spin-off the vendor encodes as a split (XLF 2016-09-19, 1.231) is stored as a split; an
  event cannot be withdrawn; once the real portfolio was emptied at more than the index portfolio held,
  no comparison is given for any period with that day in it; the split days stored here are not yet
  compared with the ledger (P3.4.03).

## Rules for what comes next (reconciliation)
- **No rounding inside a calculation**; round once, where a figure is shown, with a named rule.
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
