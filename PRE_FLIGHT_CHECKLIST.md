# Pre-Flight Checklist — Before, During and After a Change

Run through this for anything beyond a typo. It turns [CLAUDE.md](CLAUDE.md), the skills in
`.claude/skills/` and [docs/roadmap/README.md](docs/roadmap/README.md) into steps. Hooks, the commit
gate and `tools/ci.py` catch formatting, builds and tests; they do not catch a wrong rule, a number
nobody recomputed, or a command that was never run for real. This list is for those.

One task at a time. Never leave a task half done.

---

## Phase 1 — Before writing code

### 1.1 The task
- [ ] It is a task of the roadmap: `python tools/roadmap.py show <ID>`, everything in its "после:"
      is `[x]`. Work with no card is raised with the owner, not started.
- [ ] It belongs to the current phase (`config/project.toml`; read it, never edit it).
- [ ] It is not research: no new strategy, variant or backtest without a new scope from the owner.
- [ ] An owner task (👤) is explained, not done for the owner.
- [ ] The card is marked `[~]`.

### 1.2 The outcome, in one sentence
State what will be **observable** when it is done ("`check` shows the rates as fresh";
"a statement imported twice is in the ledger once"). If it cannot be observed, it cannot be verified.

### 1.3 Load what applies
- [ ] `dotnet-conventions` — any C# in `src/` or `tests/`.
- [ ] `portfolio-accounting` — ledger, imports, lots, positions, rates, valuation, any balance.
- [ ] `israeli-tax` — the tax module, `config/tax.toml` and `docs/tax-sources.md`, any tax figure.
- [ ] `ibkr-api` — anything touching IBKR, Flex included.
- [ ] `data-pipeline`, `cost-model` — market data, fees.

### 1.4 What does it touch?
- [ ] **The database** → a new migration in its own commit; never edit one that is on `origin/main`.
      A row that can be wrong for good gets its rule in the database too.
- [ ] **Money recorded or computed** → `accounting-reviewer` before the commit.
- [ ] **Orders** (proposing, approving, sending, reconciling) → `execution-reviewer` before the commit.
- [ ] **Something outside the machine** → named in the task; strict parsing; a switch the test Hosts
      turn off.
- [ ] **A fee or tax number** → source and date in a comment; the result stays labelled unverified.
- [ ] **A new package** → say what it does and why nothing installed does it; `uv add` /
      `dotnet add` / `npm install` ask the owner first.
- [ ] **The owner's working database** → read with SELECT at most; back it up before a migration
      runs on it (`.\deploy\backup.ps1`).

### 1.5 Decisions that are the owner's
Scope, a gate criterion, the phase, a tax or accounting assumption that changes a number the owner
sees, anything that spends money or reaches a broker. Ask with options and a recommendation; carry
on with what does not depend on the answer.

---

## Phase 2 — While writing code

### 2.1 Layers
- [ ] Domain: pure — no clock, network, database or files; rules live here.
- [ ] Application: use cases and the interfaces they need; no Infrastructure, Broker or Host.
- [ ] Stores write on their own (refuse a context with other unsaved changes) and leave nothing
      tracked.

### 2.2 Money and time
- [ ] `decimal` / `numeric` everywhere — also in tests, JSON and logs. No `double`.
- [ ] Currencies are never added; a conversion goes through an `FxRate` and keeps its
      `AppliedFxRate`.
- [ ] No rounding inside a calculation; a proportional part is kept to a fixed scale and the last
      part takes the remainder.
- [ ] NodaTime only; the clock is injected; trade dates are New York dates, rate dates are Israeli.

### 2.3 Refuse, do not smooth over
- [ ] An impossible state is an error that names the entry, not a zero and not "the nearest value".
- [ ] A stored fact is never overwritten: reversal, revision, or a report to the owner.
- [ ] An answer from outside that is not exactly what was expected is refused.
- [ ] A failure is not swallowed to keep a job green.

### 2.4 Safety
- [ ] No secret, credential or real account number in code, tests, fixtures, logs or messages.
- [ ] No IBKR API code outside `src/ITrade.Broker.IBKR/`; none in Python, ever.
- [ ] No live port before phase 6. Only `ExecutionService` sends orders; an unknown order state is
      reconciled, never resent.
- [ ] Every endpoint needs sign-in unless it says why not; changing requests need the antiforgery
      header; no exception text in a response.
- [ ] Statement files stay in the data folder; fixtures are anonymised.

### 2.5 Interface
- [ ] English texts, MUI components, the generated API client — no hand-written `fetch`.
- [ ] Loading, empty and error states.
- [ ] A figure carries its date, currency and source; mode and account come from the server.

---

## Phase 3 — Verifying

Report what actually happened, not what should happen.

### 3.1 Tests that can fail
- [ ] The card's "Готово, когда" is a test.
- [ ] Hand-worked numbers, recomputed; a property where a rule must hold for any input.
- [ ] Whatever the database enforces is tested on a real PostgreSQL.
- [ ] **A guard is not done until a deliberate violation was seen to fail its test.** Break each new
      rule once, on purpose.

### 3.2 The gates
```powershell
python tools/ci.py        # all 16 steps; --fast only when Docker is not available, and then say so
```
The Stop hook runs the fast part by itself; it does not run the integration tests.

### 3.3 For real
- [ ] Every command the owner will be given was run, exactly as written, as the owner would run it.
- [ ] `dotnet run --project src/ITrade.Host -- check` is `OK`.
- [ ] If it wrote to the working database: read the result back with SELECT.
- [ ] A screen was opened in a browser, not only type-checked.

### 3.4 The independent read
- [ ] The reviewer that applies (1.4) read the change — given the code, not the conclusion.
- [ ] Each finding was checked against the code, then fixed or answered; say which round was the
      last and what was not re-reviewed.

---

## Phase 4 — Before committing

- [ ] Diff read end to end: no debug output, no commented-out code.
- [ ] Schema, strategy, risk and broker-adapter changes are separate commits.
- [ ] The message starts with the task id, says why, and ends with the attribution line.
- [ ] Nothing from the data folder, no `.env`, no `appsettings.Local.json`.
- [ ] The card is `[x]` with a dated note: what was done, how it was verified, what was found.
      Anything noticed outside the task is in the phase's "Входящие".
- [ ] Pushing is the owner's step.

### Update the documents that go stale
- [ ] Something now works, was verified, was decided, or a gap was found or closed →
      [PROJECT_STATUS.md](PROJECT_STATUS.md).
- [ ] A new table, endpoint, command, job, outgoing call or guard →
      [ARCHITECTURE.md](ARCHITECTURE.md).
- [ ] A new command, recipe, or a gotcha that cost time → [QUICK_REFERENCE.md](QUICK_REFERENCE.md).
- [ ] Setup or an everyday command changed → [README.md](README.md) and the *Commands* of
      [CLAUDE.md](CLAUDE.md).
- [ ] A rule of how code is written or how the books work → the skill it belongs to.

### The report to the owner
In Russian, the outcome first: what works now, how it was verified (with the numbers), what was
found on the way, what the owner has to do, what is next. A tax or accounting figure is "an estimate
under the current assumptions", never "you owe".

---

## Stop and ask when

- The change needs the phase file, a gate criterion or the scope changed.
- It needs a push, or anything on another machine or account.
- It needs a migration that is already pushed to be edited or removed.
- It needs a secret read, a credential created, or the owner's password.
- It would write to the working database other than through the Host, or remove its volume.
- It would send, change or cancel an order — paper or live — without the owner's approval of that
  exact order.
- It would accept something on the owner's behalf: a changed exchange rate, a tax assumption, a
  reconciliation difference.
- It starts research: a new hypothesis, a variant, a backtest, the unseen period 2021–2026.
- A hook blocks it. Explain what the hook protects; do not work around it.
- A rule and the code disagree — report it rather than picking one.
