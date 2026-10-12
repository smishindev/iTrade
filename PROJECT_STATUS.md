# iTrade — Project Status

Running memory of what exists, what verified it, and why it is the way it is. Read this first when
picking the project back up. Task-level detail — every card with its dated note — is in
[docs/roadmap/](docs/roadmap/README.md); live numbers come from `python tools/roadmap.py status`.

**Last updated:** 12 October 2026
**Phase:** 3 of 7 — accounting (`config/project.toml`; the owner moves it after a gate check).
**Roadmap:** phases 0–2: 103 of 106 tasks (the three open ones are the owner's, at IBKR);
phase 3: 4 of 12.
**State:** the foundation is finished and gate G2 passed. The books exist — an append-only ledger,
lots and positions derived from it, official exchange rates since 2005 — but they are empty: no
statement has been imported yet, because the broker account is not funded and the statement service
is not available before that. No real money has moved through the platform. Mode is PAPER.

## In one paragraph

iTrade is one person's platform for a long-term index portfolio: it keeps the books (positions and
tax lots, Israeli taxes, contributions, rebalancing) and will send purchases to Interactive Brokers
only after the owner approved each one. It started on 6 October 2026 as a trading-system project.
Phase 1 tested three pre-registered rule-based strategies against simply holding the index, after
every cost, and rejected all three by their own rules; the final period 2021–2026 was never run and
stays locked. On 9 October the owner approved plan v4: the money goes into a broad index and the
platform becomes an accounting and execution-with-approval tool. Phase 2 built the foundation:
.NET 10 Host on loopback HTTPS with sign-in, PostgreSQL 18, daily jobs with catch-up, a React shell,
backup and restore, hooks and gates. Phase 3 so far: the instrument catalogue, the transaction
ledger, FIFO lots and positions, and the Bank of Israel exchange rates. Next come closing prices and
valuation; the statement import and the reconciliation wait for the funded account.

---

## What works today

"Verified" means it was run on this machine on 12 October 2026 unless another date is given — not
read in the code.

| Capability | State | Verified by |
|---|---|---|
| Everything builds and passes | ✅ | `python tools/ci.py` — 16 of 16 steps |
| .NET tests | ✅ | 235 unit, 5 architecture, 93 integration on a real PostgreSQL 18 (Testcontainers) |
| Python tests | ✅ | 60 (`uv run pytest`, no network) |
| Interface tests | ✅ | 41 Vitest tests in 4 files; 3 Playwright tests against a real Host with a throw-away database |
| The real Host starts | ✅ | `check` → `OK`; started for real: `/health` → `{"status":"Healthy","mode":"Paper","checks":{"database":"Healthy"}}` |
| One Host per database | ✅ | a second instance refused by the instance lock (integration test; seen on the real database on 12 October) |
| Sign-in, lockout, session end | ✅ | integration tests and browser tests; the owner's password is set on the working database |
| Working database | ✅ | 8 migrations applied; backup `itrade-20261012-112827Z.dump` taken before the phase-3 migrations |
| Backup and restore | ✅ | `backup.ps1`, then `restore.ps1 -Verify` into a temporary database (phase 2 gate) |
| Instrument catalogue | ✅ | 29 ETFs imported; importing again: 29 unchanged |
| Ledger: repeated import, reversal, corrected line | ✅ tests only | unit and PostgreSQL tests, 7 deliberate breakages caught. **No real statement imported yet** — the table is empty |
| Ledger rules in the database | ✅ | 17 planted violations of the constraints and 6 of the link trigger are refused |
| Lots, positions, cash | ✅ tests only | hand-worked examples (partial sale, split, sale after a split), two properties over random ledgers, 7 deliberate breakages caught |
| Exchange rates | ✅ | 5332 Bank of Israel rates, 2005-01-03 … 2026-10-09, loaded into the working database; a second run: 0 new, 11 compared |
| Daily job `fx-rates` in the real Host | ✅ | the Host was started and the job succeeded for session 2026-10-12 |
| A changed rate waits for the owner | ✅ tests only | never happened for real; 26 deliberate breakages of the rates code caught |
| Stuck job is visible and can be released | ✅ | `check` and `retry-job` in tests; `retry-job` run for real with nothing to retry |
| Market data | ✅ | 33 curated series, last bars 5–6 October 2026; every warning reviewed in [docs/data-notes.md](docs/data-notes.md) |
| IB Gateway connection (paper) | ✅ phase 1 | spike B: connect, reconnect through the 09:25 restart, account summary matches the web interface |
| Statement (Flex) import | ❌ not built | waits for a funded account (see *Waiting on the owner*) |
| Valuation, returns, reconciliation | ❌ not built | phase 3, next tasks |
| Screens with data | ❌ not built | the shell and sign-in exist; six menu entries are placeholders until phase 6 |
| Taxes, broker adapter, purchases | ❌ not built | phases 4, 5, 6 |
| Scheduled backups | ❌ | manual until phase 7 — the owner's decision |

---

## Timeline of decisions

The reasoning is the expensive part to reconstruct.

### 1. Own money, small capital, little time (6 October 2026)
One owner, Israeli tax resident (not an oleh), 10,000 ILS to start plus contributions, about four
hours a week. The stack — React, .NET, PostgreSQL, the official IBKR API — was decided then and has
not changed.

### 2. Free market data is good enough for prices, not for the shekel (7 October)
Yahoo's USD/ILS series has wrong ticks, a gap of 17 business days and quote snapshots instead of closes. Anything
in shekels uses the Bank of Israel representative rate instead. Free prices are for valuation only.

### 3. Three hypotheses, three rejections, no trading system (9 October)
Each strategy was pre-registered with its variants and its success rule, run with realistic fills
and costs at the real account size, compared with a calibrated random control, and reviewed. All
three failed their own rule. The final period was never opened. The owner approved plan v4: hold a
broad index; the platform does the accounting. The research code and its ledgers are a frozen
archive.

### 4. The broker works through the official API (phase 1, spike B)
Connection, reconnection through the gateway's daily restart, account summary and delayed quotes
were proven on a demo account. Orders were moved to phase 5 and reduced to what plan v4 needs:
owner-approved buys, limit or market-on-close, no stops.

### 5. Interface: MUI, English, React Router (phase 2)
The owner chose MUI over Tailwind with shadcn, asked for English-only interface texts, and kept
React Router. Conversation with the owner stays in Russian.

### 6. Green tests are not a running program (phase 2)
The real Host did not start although every test passed: tests run it as "Testing", the owner runs
it in Development. Since then there is a `check` command, a CI step that runs it, and the rule that a
command given to the owner was first run exactly as the owner would run it.

### 7. Live ports open with the pilot, by the owner (12 October)
Hooks block the live IB Gateway ports until the owner sets `phase = 6`. Plan v3 had this later; the
plan, the scope and the gates were brought in line.

### 8. Gate G2 passed; phase 3 began (12 October)
An independent gate check, two rounds, every criterion with evidence. The owner changed the phase.

### 9. No accountant for now (12 October)
The owner decided not to wait for a consultation: tax and accounting rules are chosen by us,
recorded as assumptions with source and date, and their results stay labelled as unverified. Gate G4
and the scope still name the accountant as the check — open question below.

### 10. Only IBKR is kept in the platform (12 October)
Keren hishtalmut is not tracked. With one holding place whose entries all come from statements, the
scheduled backup stays in phase 7: the ledger can be rebuilt by importing the statements again.

### 11. The books are append-only, in the database itself (12 October)
A ledger row is never changed: a mistake is reversed, a line the broker corrected is recorded as the
next revision. The same for exchange rates. Because a wrong row would be permanent, the domain's
rules are repeated as constraints and triggers. Positions, lots and balances are not stored at all —
they are computed from the ledger every time.

### 12. An independent reviewer reads money code before every commit (12 October)
The `accounting-reviewer` agent was added for phase 3 and found real defects each time, none of
which a test had caught: seven in the ledger (a corrected line could never be recorded; a line
without an id would be imported again and again), three in the lots (a split on the day of a trade
gave wrong lots with right totals), and across three rounds in the rates (a single disagreement with
the bank would have stopped updates for good; accepting a change would have accepted more than the
owner saw). All were fixed before the code was committed.

### 13. The statement import waits for the funded account (12 October)
Flex queries live in a part of the IBKR portal that appears only after the first deposit, and the
demo account has no reports. The steps for the owner are written on the card of P3.2.01.

---

## Known gaps

Ordered roughly by how much they would hurt.

1. **Nothing here was checked against the broker's books.** The ledger and the lots are proven on
   hand-made examples only. The first real statement will show what the import has to handle:
   whether every line has its own id, how many decimals commissions carry, how corrections,
   cancellations, splits and cash in lieu look (listed on the cards of P3.2.01 and P3.2.02).
2. **Every tax number is an unverified assumption** — the 25% rate, FIFO, which date's exchange rate
   applies to a trade. The formula for the real gain on a dollar security is not chosen yet. Gate G4
   cannot be passed as written (decision 9).
3. **The session calendar has no holidays** (`WeekdayCalendar`). Harmless for the rates job; not
   good enough for settlement dates or anything that decides about money.
4. **The Host connects as the database owner**, who could switch the append-only triggers off.
5. **Books "as of a date" do not remember what was known then.** A later reversal, a late fee or a
   late exchange rate changes a past date's figure. A closed tax year will need figures that keep
   what they were computed with.
6. **Order within a trade date is the order of recording.** A second source or intraday trading
   would need an execution time on the entry, and a way to restore a line reversed by mistake.
7. **Corporate actions beyond splits** cannot be written down (spin-off, merger, transfer); cash in
   lieu of a fraction is counted and flagged but not worked into the lots.
8. **The Host does not notice an applied migration it does not know.**
9. **`check` stays `OK` while the rates job merely cannot reach the bank**: the age of the rates
   becomes a remark on day 5 and a problem on day 8.
10. **Backups are manual.**
11. **[docs/accountant-questions.md](docs/accountant-questions.md) still describes the active
    trading of plan v3.**

---

## What is next

Small steps, one task at a time (`/next-task`):

1. **P3.3.02 — closing prices** from the curated snapshots, for valuation only.
2. **P3.4.01 — value of the portfolio on a date**, in dollars and shekels, with the rate's marks.
3. **P3.4.02 — returns** (time-weighted and money-weighted) against the index.
4. When the account is funded: **P3.2.01** (the owner creates the Flex query) → **P3.2.02** (import)
   → **P3.4.03** (reconciliation with the broker) → **P3.G.01** (gate check).

## Waiting on the owner

- **Fund the IBKR account** (P0.2.07), then create the paper account (P0.2.05) and install IBKR
  Mobile for two-factor sign-in (P0.2.03). The statement import and the reconciliation cannot start
  before this.
- **Push** local commits (VS Code "Sync Changes") — Claude has no credentials for it.

## Open questions for the owner

- **P3.2.03, manual entry and CSV import "for other places":** with only IBKR in the platform, is
  it needed at all? Gate G3 names it. Recommendation: drop it, and bring it back if a second holding
  place appears.
- **Gate G4 and the scope name the accountant as the check of the tax module.** Without a
  consultation, what replaces it? Recommendation: pass the gate on documented assumptions with
  hand-worked examples from written sources, and make a consultation mandatory before the first sale
  or the first report to the tax authority.
- **Where to hold the index** (US ETF, Irish UCITS, an Israeli broker) and the target weights — the
  decision gate G4 asks for.
