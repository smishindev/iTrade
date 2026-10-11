# iTrade web

The browser interface of the platform: React 19 + TypeScript (strict) on Vite. It talks only to
`ITrade.Host` on the same machine; it never talks to a broker.

Node 24 (see `.nvmrc`). From this folder:

| Command          | What it does                                             |
| ---------------- | -------------------------------------------------------- |
| `npm install`    | install the locked dependencies                          |
| `npm run dev`    | development server with hot reload                       |
| `npm run build`  | type-check and build into `dist/`                        |
| `npm test`       | unit tests (Vitest + Testing Library, jsdom)             |
| `npm run lint`   | ESLint with type-aware rules; warnings fail              |
| `npm run format` | format with Prettier (`format:check` only checks)        |
| `npm run check`  | everything a commit must pass: format, lint, test, build |

Interface texts are in Russian (the owner's language); code, comments and identifiers are in English.
