# Spike B — IBKR feasibility console

Throw-away console used to answer the questions of roadmap section **P1.B** (docs/roadmap/P1-spikes.md).
Findings go to [docs/spikes/ibkr-feasibility.md](../../docs/spikes/ibkr-feasibility.md).
Production code will live in `src/ITrade.Broker.IBKR/` (phase 3+), not here.

## Prerequisites
- **IB Gateway** running and logged in to a **paper** account (Free Trial or the linked paper account),
  API → Settings: socket port **4002**, *Allow connections from localhost only*, trusted IP 127.0.0.1.
  Keep **Read-Only API** on except during order tasks (P1.B.08+).
- **IBKR TWS API 10.50.02** installed (interactivebrokers.github.io). Default location `D:\TWS API`;
  otherwise set the environment variable `TWS_API_DIR`. The build fails on a different version — bump
  `TwsApiExpectedVersion` in the .csproj on purpose and re-check the spike results.
- .NET 10 SDK.

## Run
```powershell
dotnet build spikes/ibkr-feasibility/IbkrFeasibility.csproj
cd spikes/ibkr-feasibility
.\bin\Debug\net10.0\ibkr-spike.exe connect
```

All commands are **read-only** (no `placeOrder`/`cancelOrder`); each connects, checks the paper account and
writes every request and callback to `out/messages-*.jsonl`.

| Command | Task | What it does |
|---|---|---|
| `connect` | P1.B.03 | connect, wait for `nextValidId` and `managedAccounts`, refuse unless every account starts with `DU` (paper), disconnect |
| `reconnect [--minutes 3] [--drops 2] [--drop-every 30] [--heartbeat 30]` | P1.B.03 | hold a session (heartbeat `reqCurrentTime`), force `--drops` client-side disconnects, detect any drop (also a Gateway restart), reconnect with backoff 1-2-4…60 s, re-check `DU` each time; explains codes 1100/1101/1102/2104/2106/2158. Use `--drops 0 --minutes 30` around 09:25 Israel time to catch the Gateway auto-restart |
| `account` | P1.B.04 | `reqAccountSummary` (all tags), `$LEDGER:ALL` (cash by currency), `reqPositions`, cash/settlement keys of `reqAccountUpdates` |
| `contracts [--universe path]` | P1.B.05 | contract details for every candidate of `config/universes/etf_pullback_v1.toml` → `out/contracts.csv`; compares `primaryExchange` with `listing` |
| `quotes [--tickers SPY,VWO] [--types 1,2,3,4] [--seconds 10]` | P1.B.06 | per market data type: stream briefly, then a free (non-regulatory) snapshot; records `marketDataType`, tick fields, last-trade age, errors → `out/quotes-*.csv` |
| `history [--tickers SPY,VWO] [--duration "5 Y"] [--what TRADES] [--end "…"] [--repeat N]` | P1.B.07 | daily bars, regular hours, one request at a time → `out/history-<T>-<what>.csv`; `--repeat N` re-sends the same request N times (pacing probe) |

## Safety
- Only paper ports (4002 / 7497) and localhost are accepted; anything else exits before connecting.
- The account number is never stored in config or code. Override settings in `appsettings.Local.json` (git-ignored).
- Every callback and request is written to `out/messages-*.jsonl` (git-ignored; contains the account number).
  Fixtures for replay tests are **anonymised** copies in `fixtures/` (hooks block real account numbers).

## Licence note
The IBKR C# client source is distributed by IBKR under GPL v3 and is referenced from its installation
folder, not copied here. Fine for personal use; distributing binaries built with it would bring GPL obligations.
