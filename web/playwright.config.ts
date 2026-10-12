import { defineConfig } from '@playwright/test'
import { randomUUID } from 'node:crypto'

// End-to-end tests (P2.5.07): a real browser against the real Host. `npm run e2e` builds the interface, starts
// tests/ITrade.E2EHost (a throw-away PostgreSQL in Docker, its own port and data folder) and stops it afterwards.
// Needs Docker; the owner's database is not involved.
//
// The same scenarios run twice:
//   host       the Host serves the built interface itself - how the application is normally used;
//   dev-proxy  the Vite development server serves the pages and passes the API to that Host (`npm run dev`).

// The owner's secret of this run. Workers inherit it from the first process, so all see the same value.
process.env.ITRADE_E2E_SECRET ??= randomUUID()
const port = process.env.ITRADE_E2E_PORT ?? '7199'
const host = `https://127.0.0.1:${port}`
const devPort = '5199'
const dev = `http://127.0.0.1:${devPort}`

export default defineConfig({
  testDir: './e2e',
  outputDir: './test-results',
  // One Host and one owner account: failed sign-ins of one test would lock out another.
  fullyParallel: false,
  workers: 1,
  forbidOnly: true,
  reporter: 'list',
  use: {
    // The installed Microsoft Edge: nothing to download.
    channel: 'msedge',
    // The Host's certificate is the local development one.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'host', use: { baseURL: host } },
    { name: 'dev-proxy', use: { baseURL: dev } },
  ],
  webServer: [
    {
      command: 'npm run build && dotnet run --project ../tests/ITrade.E2EHost',
      url: `${host}/health`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 240_000,
      stdout: 'pipe',
      env: { ITRADE_E2E_SECRET: process.env.ITRADE_E2E_SECRET, ITRADE_E2E_PORT: port },
    },
    {
      command: `npm run dev -- --port ${devPort}`,
      url: dev,
      reuseExistingServer: false,
      timeout: 60_000,
      env: { ITRADE_HOST: host },
    },
  ],
})
