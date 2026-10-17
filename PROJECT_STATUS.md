# iTrade — Project Status

Running memory of what exists, what verified it, and why it is the way it is. Read this first when
picking the project back up. Task-level detail — every card with its dated note — is in
[docs/roadmap/](docs/roadmap/README.md); live numbers come from `python tools/roadmap.py status`.

**Last updated:** 17 October 2026
**Phase:** 3 of 7 — accounting (`config/project.toml`; the owner moves it after a gate check).
**Roadmap:** phases 0–2: all 106 tasks;
phase 3: 10 of 12 (one more was dropped by the owner).
**State:** the foundation is finished and gate G2 passed. The books exist — an append-only ledger,
lots and positions derived from it, official exchange rates since 2005, closing prices of the 29
funds since 1993 — but they are empty: no
statement of the real account has been imported yet. The import of IBKR's statement exists and was
proven on three statements of the paper account (16 October) — the paper account itself is never
recorded; the real account needs its own Flex query and token. No real money has moved through the
platform. Mode is PAPER.

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
ledger, FIFO lots and positions, the Bank of Israel exchange rates, closing prices as traded, the
value of the portfolio on a date, its returns and their comparison with the index, and the import of
the broker's statement, held against IBKR's own figures. Left of phase 3: the reconciliation and the
gate check.

---

## What works today

"Verified" means it was run on this machine on 12–13 October 2026 unless another date is given — not
read in the code.

| Capability | State | Verified by |
|---|---|---|
| Everything builds and passes | ✅ | `python tools/ci.py` — 16 of 16 steps |
| .NET tests | ✅ | 373 unit, 6 architecture, 123 integration on a real PostgreSQL 18 (Testcontainers) — 16 October |
| Python tests | ✅ | 337 collected, 1 skipped (`uv run pytest`, no network) |
| Interface tests | ✅ | 41 Vitest tests in 4 files; 3 Playwright tests against a real Host with a throw-away database |
| The real Host starts | ✅ | `check` → `OK`; started for real: `/health` → `{"status":"Healthy","mode":"Paper","checks":{"database":"Healthy"}}` |
| One Host per database | ✅ | a second instance refused by the instance lock (integration test; seen on the real database on 12 October) |
| Sign-in, lockout, session end | ✅ | integration tests and browser tests; the owner's password is set on the working database |
| Working database | ✅ | 11 migrations applied; backups before each schema change, the latest `itrade-20261012-215529Z.dump` |
| Backup and restore | ✅ | `backup.ps1`, then `restore.ps1 -Verify` into a temporary database (phase 2 gate) |
| Instrument catalogue | ✅ | 29 ETFs imported; importing again: 29 unchanged |
| Ledger: repeated import, reversal, corrected line | ✅ tests only | unit and PostgreSQL tests, 7 deliberate breakages caught. **No real statement imported yet** — the table is empty |
| Ledger rules in the database | ✅ | 17 planted violations of the constraints and 6 of the link trigger are refused |
| Lots, positions, cash | ✅ tests only | hand-worked examples (partial sale, split, sale after a split), two properties over random ledgers, 7 deliberate breakages caught |
| Exchange rates | ✅ | 5332 Bank of Israel rates, 2005-01-03 … 2026-10-09, loaded into the working database; a second run: 0 new, 11 compared |
| Daily job `fx-rates` in the real Host | ✅ | the Host was started and the job succeeded for session 2026-10-12 |
| A changed rate waits for the owner | ✅ tests only | never happened for real; 26 deliberate breakages of the rates code caught |
| Stuck job is visible and can be released | ✅ | `check` and `retry-job` in tests; `retry-job` run for real with nothing to retry |
| Closing prices | ✅ | 185,301 closes of 29 funds and 8,483 exchange sessions, 1993-01-29 … 2026-10-12, in the working database; a second import: 0 new. An ingest while New York was open left the half-formed bar of 12 October out for every fund; the one after the close brought it |
| Value of the portfolio on a date | ✅ tests only | by holding place and in all, in dollars and shekels: a hand-worked example to the last digit, and through the real Host on PostgreSQL. The ledger is empty, so no real value exists yet. 34 deliberate breakages caught |
| A price for any date says what it is | ✅ tests only | the session's own, carried over a closed day, missing, unavailable; nothing values a position with it yet. 88 deliberate breakages of the prices code caught |
| Changed or unusual prices wait for the owner | ✅ tests only | never happened for real: the full re-fetch of 12 October restated none of 185,189 stored closes |
| Market data | ✅ | 33 curated series; the 29 funds of the catalogue fetched on 12 October 2026, finished sessions up to 12 October (EWC up to 6 October: the vendor's bar of 12 October has its open below the day's low and fails the quality check, also after the close). The warnings of that fetch were **not** reviewed again; the earlier ones are in [docs/data-notes.md](docs/data-notes.md) |
| IB Gateway connection (paper) | ✅ phase 1 | spike B: connect, reconnect through the 09:25 restart, account summary matches the web interface |
| Statement (Flex) import | ✅ paper statements only | `fetch-statement` downloaded a statement with the owner's token; three real paper-account statements (anonymised copies) imported one after another and again into a throw-away database, each agreeing with IBKR at its start and end (cash per currency, positions); the working database refused the paper statement, as it must. 14 integration and 73 unit tests; three rounds of `accounting-reviewer`. Not yet seen: a dividend, a sale, a split, a cancellation — each is refused until seen |
| Returns of a period | ✅ tests only | time-weighted, money-weighted and the gain in money, in dollars and shekels; agrees with the Python reference calculation on 23 synthetic cases; through the real Host on PostgreSQL. No real flows yet. 59 deliberate breakages caught |
| Comparison with the index | ✅ tests only | the same contributions put into the index fund (VT) at each day's close, its dividends put back after withholding; a portfolio that holds only the index matches it; a split of the index is no fall. 60 deliberate breakages caught. No real flows yet |
| Dividends and splits of the funds | ✅ | 2,538 events of 28 funds in the working database (2,524 dividends, 14 splits; one dividend the vendor lacks comes from the recorded corrections); with them one share reproduces the vendor's total-return series within 0.005% a year over up to 33 years |
| Reconciliation with the broker | ❌ not built | phase 3: P3.4.03, waits for the first statement |
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
On 13 October the owner dropped manual entry and CSV import for other places (P3.2.03) for the same
reason, and gate G3 no longer asks for it; it comes back with a second holding place.

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

### 13. Prices as traded, sessions from the exchange calendar (12 October)
The vendor's close is adjusted back in time after every split; the books hold the shares really held
on a date, so the platform stores the price really paid that day. The first version passed every
test and was sent back by the reviewer: a bar fetched while the session was open was stored as a
close (the owner's evening is the middle of the New York session), and "was the exchange open" was
guessed from the prices themselves. Now only finished sessions are exported, the export carries the
exchange's calendar, and changes of stored prices are judged by size as well as by number.

### 14. Two returns, and no index comparison on prices alone (13 October)
Time-weighted and money-weighted returns are two figures and each is named; a contribution is not a
gain. The comparison with the index was split off (P3.4.04): the portfolio's value includes the
dividends it received, a fund's price does not, so a comparison on prices alone would show the
portfolio ahead by the dividend yield. It needs the fund's dividends and split days in the database.

### 15. The index is a portfolio, not a price (13 October)
The owner's contributions, on their days, buy the index fund at that day's close; its dividends buy
more after withholding; the result is measured by the same two returns. The fund that stands for the
index is a setting (VT). The comparison flatters the index - no commission, dividends put back on the
ex-date - and says so.

### 17. The statement import refuses what it has not seen and agrees with IBKR or records nothing (16 October)
Every execution is an entry (as IBKR keeps its lots), a conversion two linked legs. A cancelled or
corrected trade, a corporate action, a transfer, a distribution that is not an ordinary dividend, a tax
without its dividend — formats never seen on a real statement — stop the whole statement instead of
being guessed into a ledger that keeps every entry. Before recording, the ledger's cash on the evening
before the statement must be IBKR's opening cash, and the ledger with what it would add must hold
IBKR's closing cash and positions; after recording the same is checked on what was written. A paper
account's statement never enters the books. The reviewer's three rounds found a gap between downloads
that would have gone unnoticed, a split that would have passed if the query lost a section, and a check
that would have blocked every import after the owner's first reversal; all fixed before the commit.

### 16. The statement format is read before the import is written (12–16 October)
Flex queries live in a part of the IBKR portal that appears only after the first deposit, so the
import waited for the account to be funded. Then the owner bought on the paper account and
downloaded three statements, and the format was read before any code: one order arrives as many
executions, the first one carrying the minimum commission; times are New York time without a zone;
a conversion books its commission in another currency; unfilled orders are absent; the month and
year totals of a day's statement belong to the day of download; IBKR's own return is in percent.
Notes on the card of P3.2.01.

---

## Known gaps

Ordered roughly by how much they would hurt.

1. **Nothing here was checked against the broker's books.** The ledger and the lots are proven on
   hand-made examples and paper-account statements. A dividend with withholding, a sale, a split, a
   cancelled or corrected trade, interest booked on another day than it is dated have not been seen:
   the import refuses each of them until it has (cards of P3.2.01 and P3.2.02). The first such
   statement stops the imports until the import is taught from it.
2. **Every tax number is an unverified assumption** — the 25% rate, FIFO, which date's exchange rate
   applies to a trade. The formula for the real gain on a dollar security is not chosen yet. Gate G4
   cannot be passed as written (decision 9).
3. **The session calendar of the daily jobs has no holidays** (`WeekdayCalendar`). Harmless for the
   rates job; not good enough for settlement dates or anything that decides about money. The real
   sessions are in `market_sessions` now, but only as far as the last import of prices.
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
11. **Prices are refreshed by hand**, with two commands, after the New York close; nothing runs the
    data pipeline by itself. `check` shows the age of the prices and who is behind.
12. **A stored session cannot be withdrawn.** If the calendar library calls a day of an unscheduled
    closure a session, every fund reads "missing" for that day, and once the library is corrected
    the imports are refused until a migration deals with it.
13. **An export made from an older data folder passes as new** (it gets a fresh date); it can put
    back up to 20 price corrections of at most 2% each.
14. **The currency of a price comes from the catalogue, not from the vendor.** Every fund is in
    dollars; the first one that is not needs the quote currency at ingest.
15. **A split that is in the prices and not yet in the ledger halves a value without a word**
    (the statement lags a day), and a purchase after a split the prices have not seen is valued at
    the old price, marked only as provisional. The valuation cannot see either; the reconciliation
    has to (card of P3.4.03).
16. **The value of a past date is not remembered**: it is worked out again from the ledger, the
    prices and the rates as they are now.
17. **One fund (EWC) has no price after 6 October**: the pipeline takes a fetch whole or not at
    all, and the vendor's newest bar is inconsistent.
18. **Returns are before tax on gains**; a transfer between two holding places would be counted as
    the owner's money going out and coming in. The index comparison takes no commission from the
    index, and its dividends come from the free vendor, unchecked against the fund's announcements.
    A dividend or split the vendor later moves or withdraws stays stored - it is named at the import,
    it cannot be withdrawn yet.
19. **[docs/accountant-questions.md](docs/accountant-questions.md) still describes the active
    trading of plan v3.**
20. **IBKR leaves the Cash Report's rows per currency out of some days**, differently from download to
    download. A day without them is not checked alone; a first or last day without them, once the
    account holds two currencies, refuses the statement - download it again.
21. **Two imports run at the same moment** are kept apart only by the ledger's unique keys; what one
    records may differ from what it checked, and the check after recording then says so.

---

## What is next

Small steps, one task at a time (`/next-task`):

1. **P3.4.03** (reconciliation with the broker) → **P3.G.01** (gate check).
2. With the owner: a Flex query on the **real** account (the same settings as `iTrade`) and its own
   token, then the first real `fetch-statement` / `import-statement`.

## Waiting on the owner

- **Create the Flex query and token on the real account** (the paper login's token downloads the
  paper account, which is never recorded).
- **Renew the Flex token** before it expires — at most a year after 16 October 2026 (the date is in the portal).
- **Push** local commits (VS Code "Sync Changes") — Claude has no credentials for it.
- **Refresh the prices after the New York close** (after 23:30 in Israel), when a current value is
  wanted: `uv run --directory python itrade ingest --universe etf_pullback_v1`, then
  `dotnet run --project src/ITrade.Host -- import-prices`. Done on the evening of 12 October; EWC is
  still behind (gap 17) - the next run should bring it.

## Open questions for the owner

- **Gate G4 and the scope name the accountant as the check of the tax module.** Without a
  consultation, what replaces it? Recommendation: pass the gate on documented assumptions with
  hand-worked examples from written sources, and make a consultation mandatory before the first sale
  or the first report to the tax authority.
- **Where to hold the index** (US ETF, Irish UCITS, an Israeli broker) and the target weights — the
  decision gate G4 asks for.
