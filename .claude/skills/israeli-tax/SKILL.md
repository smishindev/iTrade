---
name: israeli-tax
description: How iTrade treats Israeli tax on the owner's investments — the rules and their first-hand sources (docs/tax-sources.md), the parameters with their sources (config/tax.toml), what is still open, and the rules for writing tax code (inputs, FX rate, lots, rounding, wording). Load before writing or reviewing anything in the Tax module, changing config/tax.toml, or telling the owner a tax amount.
---

# Israeli tax — assumptions, sources, rules for code

**Nothing here is tax advice, and almost nothing is confirmed.** The owner is an Israeli tax resident
(not an oleh), an employee, investing own money through a foreign broker that withholds no Israeli tax.
The answers would come from a licensed accountant (`docs/accountant-questions.md`, 30 questions); until then
every number is an assumption. `docs/PLAN.md` §6 and gate G4 win over this summary.

**Owner's decision of 2026-10-12: no consultation for now — "we do it the way we think is right".**
So work does not wait for answers: where a rule is needed, choose the most defensible one, write it down
as an assumption with the **source it was taken from and the date** (a law or Tax Authority publication
is a source; "general knowledge" is not one — say so when that is all there is), make it a parameter,
and keep it labelled *unverified* wherever the owner sees its result. This does not turn an assumption
into a fact: the wording rules below stay, and a real answer later still replaces the assumption.
On 2026-10-17 the owner approved gate G4, `docs/SCOPE.md` and PLAN §6 rewritten to match: rules from
first-hand sources (`docs/tax-sources.md`, task P4.0.01), hand-worked examples that cite them, results
labelled "estimate, not checked by an accountant"; the accountant's check is the owner's P4.3.01, before the
first annual return with a capital gain, not a condition of the gate.

## What the program assumes today
The first-hand sources are in `docs/tax-sources.md` (P4.0.01, 17 October 2026): rules R1–R12 with section, URL,
excerpt and version, and open questions O1–O10. Read it before any tax code; cite its rule ID in the code and tests.
The figures are in `config/tax.toml` (P4.1.01): each a table with `value` (or `open = "O…"`), `rule`, `source`,
`read`, `checked_by_accountant`, read by `TaxParametersFile` into `TaxParameters` (Domain) - a parameter without a
source is refused, an open one throws `TaxRuleOpenException` when its value is asked for.
| Assumption | Where | Status |
|---|---|---|
| Capital gains: 25% of the **real** gain on a sale (ceiling; marginal rate if lower) | `capital_gains_rate` | sourced: R1 (ORD s.91(b)) |
| Real gain of a dollar security: the exchange rate is the index; a negative inflationary amount is 0; the exchange-rate part of a loss is not offsettable | — (P4.1.02) | sourced: R2 (ORD s.88, circular 10/2025, Moses) |
| FX rate: Bank of Israel representative rate of the day (or the last before it); purchase/sale: the trade date or payment, whichever is earlier; dividends: the day paid | `exchange_rate*` | sourced: R3 (conversion rules 2003) |
| Lots are matched FIFO | `lot_matching`, `LotMatching.Fifo` | sourced for listed securities (R4); for ETFs held abroad open (O2) |
| Losses: same year against gains (abroad first), then against dividends/interest on securities taxed ≤ 25%; carried forward against gains only, if returns were filed | `loss_*` | sourced: R5 (ORD s.92, circular 10/2025) |
| Dividends: 25%; the US withholds 25% with W-8BEN (30% without); credit capped at the Israeli tax, excess carried 5 years | `dividend_rate`, `us_dividend_withholding`, `foreign_tax_credit_carryforward_years` | sourced: R7 (ORD s.125B, ss.199–210, treaty art. 12, IRS Pub 515) |
| Interest on dollar cash: 25% (linked asset) | `interest_rate` | sourced, medium: R8 (ORD s.125C) |
| Additional tax: 3% + 2% on capital income above ₪721,560 (2026) | `surtax_*` | sourced: R6; the owner's other income is unknown (O8) |
| Half-year report and advance (31 July, 31 January); annual advance on foreign income (30 April); annual return 30 April | `*_due` | sourced: R9–R11; the half-year form is open (O5) |
| Exchange-rate differences on dollar cash | `exchange_differences_on_cash` (open) | **open** (O3): named as untaxed by assumption, flagged |
| Where to hold an index (US ETF, Irish UCITS) | — | P4.0.02 compares, the owner decides (P4.0.03); keren hishtalmut is out (owner, 2026-10-12) |

## What exists (P4.1.01 – P4.1.02)
- `TaxParameters` (Domain) from `config/tax.toml` through `TaxParametersFile` (Infrastructure); the Host reads the
  file the first time a tax figure needs it (`AddTax`).
- `RealGain.Of(disposal, purchaseRate, saleRate)` → `SaleInShekels` (Domain): cost = (cost + purchase commission) ×
  the purchase rate, proceeds = (proceeds − sale commission) × the sale rate; the inflationary amount = the dollars
  paid × the change of the rate; a gain: real = nominal − the inflationary amount between 0 and nominal; a loss: all
  of it when the shekel weakened, only the dollar loss at the sale rate when it strengthened (R2, Moses). Dollar
  securities only; refused: a rate that is provisional, of another day or not the Bank of Israel's representative
  one, and a lot bought before 2006 (transitional rules, not built). Tested on circular 10/2025's examples 1–4.
- `CapitalGains.OfYearAsync(year)` (Application): the books of each holding place as of today (a commission dated
  after 31 December stays with its sale), the parts of the sales of that year, the Bank of Israel rates of the
  trade days (`FxRates.FindAsync`; a day without a rate takes the last one before it once a later one is stored,
  R3). Refuses to run when `config/tax.toml` names another real-gain, lot or rate rule, or answers O2/O9/O10.
  What cannot be computed (corporate-action cash, a lot before 2006, no rate, a provisional or foreign one) is
  named in `NotComputed` and the sums are null then. `YearOver` is false while the year runs (New York date):
  `IsComplete` needs both. `Remarks` name the year not over, a loss with the fund bought at any holding place
  within 30 days or those days not over (O9), a fraction of a share (O10) - they may over-name (a lot sold in the
  same sale counts as a purchase around it; a fraction left by a split), never under-name. `Assumptions` lists the
  parameters it rests on, `ParametersFrom` the file. A sale not imported yet is not counted: the ledger is all it
  knows.
  No tax amount yet: the rate and the offsets of losses come with P4.1.04.

When an answer arrives: replace the assumption, write the answer's **date and source** next to the
number (its `source` and `read` in `config/tax.toml`, `checked_by_accountant = true`, the row in
`docs/tax-sources.md`), and add the
accountant's worked example as a test. A tax number changes only this way.

## Rules for tax code
- **Pure functions in `ITrade.Domain`**: transactions, lots and rates in, amounts out. No clock, no
  database, no network (architecture tests). The tax year and the calculation date are parameters.
- **Inputs come from the broker** (Flex statements, executions) and the **Bank of Israel** (official
  rates). Yahoo prices are for valuation only, never for tax.
- **Money is `decimal`** with its currency (`Money`); a dollar amount becomes shekels only through an
  `FxRate` that carries its date and source. Keep both the dollar amount and the rate used — the
  accountant needs to see them.
- **No rounding inside a calculation.** Amounts stay exact; rounding to agorot happens once, where a
  figure is shown or exported, with the rule passed in explicitly and recorded with the result.
  Whether tax forms want per-line or per-total rounding is not confirmed — never bake one in.
- **Every result names its assumptions.** A computed tax amount carries the list of assumption
  versions it used (rate, FX rule, lot rule), so a report can say what it rests on and old reports
  can be recomputed after an answer changes.
- **A missing rate or an unmatched lot is an error**, not a zero and not "the nearest date". Report
  what is missing and stop.
- **Wash sales, fractional shares, FX on a dollar cash balance, dividends from accumulating funds**:
  open questions (7, 8, 12, 28). Until answered, the code refuses or flags these cases — it does not
  guess.
- Tests: the accountant's examples (gate G4) are the acceptance tests when there are any; without them,
  hand-worked examples that cite the source of the rule they follow — and say so. Add properties that must hold
  for any input (the sum over lots equals the total; a loss never produces negative tax; splitting a
  sale into two sales of the same lots changes nothing).

## Wording to the owner
Say "estimated tax under the current assumptions: …" and name the unverified ones. Never "you owe",
never a figure without its basis. The yearly export is a draft for the accountant, not a tax return.
A change of assumptions is the owner's and the accountant's decision, never the program's.
