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

| Command | What it does |
|---|---|
| `connect` | connect, wait for `nextValidId` and `managedAccounts`, refuse unless every account starts with `DU` (paper), disconnect |

## Safety
- Only paper ports (4002 / 7497) and localhost are accepted; anything else exits before connecting.
- The account number is never stored in config or code. Override settings in `appsettings.Local.json` (git-ignored).
- Every callback and request is written to `out/messages-*.jsonl` (git-ignored; contains the account number).
  Fixtures for replay tests are **anonymised** copies in `fixtures/` (hooks block real account numbers).

## Licence note
The IBKR C# client source is distributed by IBKR under GPL v3 and is referenced from its installation
folder, not copied here. Fine for personal use; distributing binaries built with it would bring GPL obligations.
