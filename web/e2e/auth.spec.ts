import { expect, test, type Page } from '@playwright/test'

const secret = process.env.ITRADE_E2E_SECRET ?? ''

/** Everything the browser complained about: script errors and blocked content (Content-Security-Policy). */
function collectProblems(page: Page): string[] {
  const problems: string[] = []
  page.on('pageerror', (error) => problems.push(`script error: ${error.message}`))
  page.on('console', (message) => {
    if (message.type() === 'error' && /content security policy|refused to/i.test(message.text())) {
      problems.push(`blocked: ${message.text()}`)
    }
  })
  return problems
}

async function signIn(page: Page, password: string) {
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

test('sign in, move around, reload, sign out', async ({ page }, testInfo) => {
  const problems = collectProblems(page)

  // Without a session every screen leads to the sign-in page.
  await page.goto('/taxes')
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to iTrade' })).toBeVisible()
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('navigation', { name: 'Main' })).toHaveCount(0)

  // The component styles arrived (the security policy allows them): the button is filled, not a bare element.
  const button = page.getByRole('button', { name: 'Sign in' })
  expect(await button.evaluate((element) => getComputedStyle(element).backgroundColor)).not.toBe(
    'rgba(0, 0, 0, 0)',
  )
  await page.screenshot({ path: `test-results/${testInfo.project.name}-01-sign-in.png` })

  // The right password opens the screen the visitor was going to.
  await signIn(page, secret)
  await expect(page.getByRole('heading', { level: 1, name: 'Taxes' })).toBeVisible()
  await expect(page).toHaveURL(/\/taxes$/)
  await expect(page.getByLabel('Trading mode: Paper')).toHaveText('PAPER')

  // The session is a cookie scripts cannot read, sent to this site only.
  const session = (await page.context().cookies()).find(
    (cookie) => cookie.name === 'itrade.session',
  )
  expect(session).toMatchObject({ httpOnly: true, secure: true, sameSite: 'Strict' })
  expect(await page.evaluate(() => document.cookie)).not.toContain('itrade.session')

  // The menu changes the screen without leaving the application.
  await page.getByRole('link', { name: 'Overview' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
  await expect(page).toHaveURL(/127\.0\.0\.1:\d+\/$/)
  await expect(page.getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('link', { name: 'Taxes' })).not.toHaveAttribute('aria-current')
  await page.screenshot({
    path: `test-results/${testInfo.project.name}-02-overview.png`,
    animations: 'disabled',
  })

  // A reload of a screen address keeps the session and the screen.
  await page.getByRole('link', { name: 'Positions' }).click()
  await page.reload()
  await expect(page.getByRole('heading', { level: 1, name: 'Positions' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Positions' })).toHaveAttribute(
    'aria-current',
    'page',
  )

  // Signing out closes the application again.
  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to iTrade' })).toBeVisible()
  await page.goto('/reports')
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to iTrade' })).toBeVisible()

  expect(problems).toEqual([])
})

test('a wrong password is refused and explained', async ({ page }) => {
  await page.goto('/login')

  await signIn(page, `not-${secret}`)

  await expect(page.getByRole('alert')).toHaveText('The password is not correct.')
  await expect(page.getByLabel('Password')).toHaveValue('')
  await expect(page).toHaveURL(/\/login$/)
})

test('the API is closed without a session, the health check is open', async ({ request }) => {
  expect((await request.get('/openapi/v1.json')).status()).toBe(401)
  expect((await request.post('/api/auth/logout')).status()).toBe(401)

  const health = await request.get('/health')
  expect(health.status()).toBe(200)
  expect(await health.json()).toMatchObject({ status: 'Healthy', mode: 'Paper' })
})
