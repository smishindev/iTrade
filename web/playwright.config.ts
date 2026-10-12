import { defineConfig } from '@playwright/test'
import { randomUUID } from 'node:crypto'

// End-to-end tests (P2.5.07): a real browser against the real Host, which serves the built interface.
// `npm run e2e` builds the interface, starts tests/ITrade.E2EHost (a throw-away PostgreSQL in Docker, its own
// port and data folder) and stops it afterwards. Needs Docker; the owner's database is not involved.

// The owner's secret of this run. Workers inherit it from the first process, so all see the same value.
process.env.ITRADE_E2E_SECRET ??= randomUUID()
const port = process.env.ITRADE_E2E_PORT ?? '7199'
const baseURL = `https://127.0.0.1:${port}`

export default defineConfig({
  testDir: './e2e',
  outputDir: './test-results',
  // One Host and one owner account: failed sign-ins of one test would lock out another.
  fullyParallel: false,
  workers: 1,
  forbidOnly: true,
  reporter: 'list',
  use: {
    baseURL,
    // The installed Microsoft Edge: nothing to download.
    channel: 'msedge',
    // The Host's certificate is the local development one.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'npm run build && dotnet run --project ../tests/ITrade.E2EHost',
    url: `${baseURL}/health`,
    ignoreHTTPSErrors: true,
    reuseExistingServer: false,
    timeout: 240_000,
    stdout: 'pipe',
    env: { ITRADE_E2E_SECRET: process.env.ITRADE_E2E_SECRET, ITRADE_E2E_PORT: port },
  },
})
