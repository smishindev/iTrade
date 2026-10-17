---
name: israeli-tax
description: How iTrade treats Israeli tax on the owner's investments — which numbers are unverified assumptions, where they live, what the accountant still has to answer, and the rules for writing tax code (inputs, FX rate, lots, rounding, wording). Load before writing or reviewing anything in the Tax module, changing the [tax] section of config/costs.toml, or telling the owner a tax amount.
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
excerpt and version, and open questions O1–O8. Read it before any tax code; cite its rule ID in the code and tests.
| Assumption | Where | Status |
|---|---|---|
| Capital gains: 25% of the **real** gain on a sale (ceiling; marginal rate if lower) | `config/costs.toml` `[tax] capital_gains_rate` → `config/tax.toml` (P4.1.01) | sourced: R1 (ORD s.91(b)) |
| Real gain of a dollar security: the exchange rate is the index; a negative inflationary amount is 0; the exchange-rate part of a loss is not offsettable | — (P4.1.02) | sourced: R2 (ORD s.88, circular 10/2025, Moses) |
| FX rate: Bank of Israel representative rate of the day (or the last before it); purchase/sale: the trade date or payment, whichever is earlier; dividends: the day paid | — | sourced: R3 (conversion rules 2003) |
| Lots are matched FIFO | `LotMatching.Fifo` | sourced for listed securities (R4); for ETFs held abroad open (O2) |
| Losses: same year against gains (abroad first), then against dividends/interest on securities taxed ≤ 25%; carried forward against gains only, if returns were filed | `[tax] loss_carryforward` | sourced: R5 (ORD s.92, circular 10/2025) |
| Dividends: 25%; the US withholds 25% with W-8BEN (30% without); credit capped at the Israeli tax, excess carried 5 years | `[tax] dividend_rate` | sourced: R7 (ORD s.125B, ss.199–210, treaty art. 12, IRS Pub 515) |
| Interest on dollar cash: 25% (linked asset) | — | sourced, medium: R8 (ORD s.125C) |
| Additional tax: 3% + 2% on capital income above ₪721,560 (2026) | — | sourced: R6; the owner's other income is unknown (O8) |
| Half-year report and advance (31 July, 31 January); annual advance on foreign income (30 April); annual return 30 April | — | sourced: R9–R11; the half-year form is open (O5) |
| Exchange-rate differences on dollar cash | — | **open** (O3): named as untaxed by assumption, flagged |
| Where to hold an index (US ETF, Irish UCITS) | — | P4.0.02 compares, the owner decides (P4.0.03); keren hishtalmut is out (owner, 2026-10-12) |

When an answer arrives: replace the assumption, write the answer's **date and source** next to the
number (a comment in `config/costs.toml`, the row in `accountant-questions.md`), and add the
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
