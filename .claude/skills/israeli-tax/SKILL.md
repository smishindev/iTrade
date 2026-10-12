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
Gate G4 and `docs/SCOPE.md` still name the accountant as the check — that is the owner's to change;
raise it when phase 4 starts instead of working around it.

## What the program assumes today
| Assumption | Where | Status |
|---|---|---|
| Capital gains: 25% of the **real** gain on a sale | `config/costs.toml` `[tax] capital_gains_rate` | general knowledge, not verified |
| Losses offset gains of the same year; carried forward | `[tax] loss_carryforward` | not verified (questions 9–12) |
| Dividends: 25% withheld in the US, credited, nothing more due in Israel | `[tax] dividend_rate` | not verified (questions 17–18) |
| FX rate: Bank of Israel representative rate on the **trade** date | project decision (`docs/data-notes.md`) | date choice not verified (question 4) |
| Lots are matched FIFO | assumption | not verified (question 5) |
| Half-year reports and advances are due | assumption | not verified (questions 13–16) |
| How the real gain is computed for a dollar security | — | **open** (question 3): no formula without a written source; until one is chosen and recorded, the code refuses |
| Where to hold an index (US ETF, Irish UCITS, Israeli broker) | — | open (questions 26–30); keren hishtalmut is out — the owner decided on 2026-10-12 not to keep it in the platform |

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
