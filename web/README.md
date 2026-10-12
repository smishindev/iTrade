# iTrade web

The browser interface of the platform: React 19 + TypeScript (strict) on Vite. It talks only to
`ITrade.Host` on the same machine; it never talks to a broker.

Node 24 (see `.nvmrc`). From this folder:

| Command           | What it does                                                         |
| ----------------- | -------------------------------------------------------------------- |
| `npm install`     | install the locked dependencies                                      |
| `npm run dev`     | development server with hot reload                                   |
| `npm run build`   | type-check and build into `dist/`                                    |
| `npm test`        | unit tests (Vitest + Testing Library, jsdom)                         |
| `npm run lint`    | ESLint with type-aware rules; warnings fail                          |
| `npm run format`  | format with Prettier (`format:check` only checks)                    |
| `npm run gen:api` | regenerate the typed API client from `openapi/v1.json`               |
| `npm run e2e`     | browser tests against the real Host (needs Docker)                   |
| `npm run check`   | everything a commit must pass: API client, format, lint, test, build |

## Running

- **Normal use — one process.** `npm run build` here, then `dotnet run --project src/ITrade.Host` in the
  repository root: the Host copies `dist/` to its `wwwroot` and serves the interface and the API together at
  `https://127.0.0.1:7120`.
- **Development — hot reload.** Start the Host as above, then `npm run dev` here and open
  `http://localhost:5173`. Vite serves the pages and passes `/api`, `/health`, `/openapi` and `/hubs` to the
  Host (another address: environment variable `ITRADE_HOST`).

The Host sends a Content-Security-Policy: scripts, data and frames from its own origin only. A library that
needs anything else (a CDN font, an inline script) will not work — bundle it instead.

## End-to-end tests

`npm run e2e` runs Playwright (`e2e/`) in the installed Microsoft Edge against the real Host. It builds the
interface and starts `tests/ITrade.E2EHost`: a throw-away PostgreSQL in Docker, its own port (7199) and data
folder, and an owner secret generated for the run. The owner's database and settings are not involved, and
the container is removed afterwards. Every scenario runs twice: against the Host serving the built interface
(`host`) and through the Vite development server on port 5199 passing the API to that Host (`dev-proxy`). Needs Docker; a Host started by hand must be stopped first (it locks
the build). Not part of `npm run check` — run it before a phase gate and after changes to sign-in, routing
or the Host's static files and security headers.

## API client

`src/api/generated/schema.d.ts` is generated from `openapi/v1.json` — never edit it by hand. After a change
of the Host API:

1. refresh the document: run the integration test `OpenApiDocumentInTheRepositoryIsCurrent` once with the
   environment variable `ITRADE_UPDATE_OPENAPI=1` (without it the test fails while the document is stale);
2. `npm run gen:api`;
3. fix what the compiler now reports, and commit both files.

`npm run gen:api:check` (part of `npm run check`) fails when step 2 was forgotten.

`openapi-typescript` 7 declares TypeScript 5 as its peer; `overrides` in `package.json` lets it run on our
TypeScript 6. It only prints types, and the compiler checks its output. Remove the override when a release
supports TypeScript 6.

## Conventions

Everything is in English: interface texts, code, comments (owner's decision, 2026-10-12).

Components come from MUI (Material UI, MIT). Use its community components only; the paid data-grid
features (row grouping, aggregation, Excel export) are not used — totals come from the server.
