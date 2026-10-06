---
name: ibkr-api
description: How iTrade talks to Interactive Brokers through the official TWS API (C#) and IB Gateway — connection, orders (LOO/OPG, attached GTC stop, OCA exits), callbacks, recovery after crashes, market data types, Flex reports, and known traps. Load before writing or reviewing any code in spikes/ibkr-feasibility/ or src/ITrade.Broker.IBKR/, or when answering questions about IBKR orders.
---

# IBKR API — rules and traps

Facts marked **(verify)** are from IBKR documentation or general knowledge and must be confirmed in
spike B (roadmap P1.B) before code relies on them. Record every confirmation in
`docs/spikes/ibkr-feasibility.md` with the message log as evidence.

## Where code may live
Only `spikes/ibkr-feasibility/` (phase 1) and `src/ITrade.Broker.IBKR/` (phase 3+). Hooks block IBApi
code elsewhere, block Python broker code always, and block live ports before phase 8.

## Connection
- IB Gateway ports: **paper 4002**, live 4001 (TWS: 7497 / 7496). Host `127.0.0.1` only.
- `EClientSocket` + `EReader` + `EReaderSignal`; after `eConnect` wait for **`nextValidId`** before
  sending any order. `managedAccounts` lists accounts — **refuse to run unless it equals the configured
  account** (paper accounts start with `DU`). Never write real account numbers into the repo — use `DU0000000`.
- One `clientId` per connection. `orderId` must increase per clientId; `permId` is assigned by IBKR
  and stays unique across sessions — store both.
- Connectivity codes: 1100 lost, 1101 restored with data lost (re-subscribe, reconcile), 1102 restored
  with data kept; 2104/2106/2158 are informational farm-OK messages, not errors.
- Gateway restarts daily and needs periodic re-login with 2FA: the app must survive disconnects and
  reconcile on every reconnect.
- During research or debugging enable **Read-Only API** in IB Gateway settings.

## Orders used by ETF_PULLBACK_V1
| Purpose | orderType | tif | Notes |
|---|---|---|---|
| Entry: limit-on-open (LOO) | `LMT` | `OPG` | lmtPrice = close + 0.5·ATR; submitted the evening before; unfilled OPG orders are cancelled after the auction (verify) |
| Protective stop | `STP` | `GTC` | auxPrice = stop; child of the entry (`parentId`); becomes a market order when triggered — gap fills can be far worse |
| Exit: market-on-open (MOO) | `MKT` | `OPG` | in an **OCA group** with the stop so only one sells (OCA type chosen in spike B) |

- **Bracket transmit rule:** parent `Transmit = false`, child `Transmit = true` (the last order transmits
  the group). Otherwise the entry can go live without its stop.
- Set `OrderRef` = our `intent_id` — it is a **label for matching**, not an idempotency guarantee.
- Fractional quantities and which order types/TIFs accept them: **(verify)** — the key question of spike B.
- Auction cut-off for OPG orders depends on the listing exchange (≈ 09:28 NY) **(verify)**; our own cut-off is 09:15 NY.
- GTC is not forever: IBKR may cancel GTC orders on corporate actions or after long periods **(verify)**.

## Callbacks and state
- `openOrder`, `orderStatus` (may repeat the same status — dedupe), `execDetails` (one per fill,
  unique `execId`; corrections arrive as new executions **(verify format)**), `commissionReport` (keyed by `execId`), `error`.
- Status names: PendingSubmit, PreSubmitted, Submitted, PendingCancel, Cancelled, ApiCancelled,
  Filled, Inactive. **PendingCancel is not Cancelled** — a fill can still arrive.
- Persist the intent **before** sending. If no acknowledgement arrives, the state is **Unknown**:
  never resend; recover with `reqOpenOrders` / `reqAllOpenOrders`, `reqCompletedOrders`,
  `reqExecutions` and match by `OrderRef` and `permId`.
- After a partial fill, check that the stop quantity equals the filled quantity.
- IB Gateway may re-submit stored orders after reconnecting (setting) **(verify)** — account for it.

## Market data
- `reqMarketDataType`: 1 live, 2 frozen, 3 delayed, 4 delayed-frozen. What arrives without
  subscriptions through the API differs from what the desktop shows **(verify)**.
- Always show source, type and age of a quote; stale or delayed quotes block new entries (not risk-reducing exits).
- Historical data has pacing limits — queue requests, never burst.

## Reports
Flex Web Service (token + query id, kept in user-secrets, never in files) → Activity Statement XML,
end-of-day. Use for reconciliation and tax data, not for real-time state.

## Paper ≠ live
The paper simulator fills from top-of-book and simulates stops and complex orders; partial-fill
behaviour differs. Paper proves mechanics, not execution quality.
