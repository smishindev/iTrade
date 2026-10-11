import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, test } from 'vitest'
import { getJson } from './api/http'
import { installFakeServer } from './test/fakeServer'
import { renderApp } from './test/renderApp'
import { setViewport } from './test/setup'

beforeEach(() => {
  localStorage.clear()
  setViewport('wide')
})

describe('signed in', () => {
  test.each([
    ['/', 'Overview'],
    ['/positions', 'Positions'],
    ['/contribute', 'Contribute'],
    ['/taxes', 'Taxes'],
    ['/reports', 'Reports'],
    ['/settings', 'Settings'],
  ])('%s shows the %s screen inside the shell', async (path, title) => {
    installFakeServer()
    renderApp(path)

    expect(await screen.findByRole('heading', { level: 1, name: title })).toBeInTheDocument()
    expect(within(screen.getByRole('banner')).getByText('iTrade')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: title })).toHaveAttribute('aria-current', 'page')
  })

  test('the menu moves between screens without reloading the page', async () => {
    const server = installFakeServer()
    const app = renderApp('/')
    await screen.findByRole('heading', { level: 1, name: 'Overview' })
    const sessionRequests = server.requests.filter((path) => path === '/api/auth/session').length

    await userEvent.click(screen.getByRole('link', { name: 'Taxes' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Taxes' })).toBeInTheDocument()
    expect(app.path()).toBe('/taxes')
    expect(screen.getByRole('link', { name: 'Taxes' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current')
    expect(server.requests.filter((path) => path === '/api/auth/session')).toHaveLength(
      sessionRequests,
    )
  })

  test('the header shows the mode the server reports', async () => {
    installFakeServer({ mode: 'Paper' })
    renderApp('/')

    expect(await screen.findByLabelText('Trading mode: Paper')).toHaveTextContent('PAPER')
  })

  test('a mode the application does not know is shown as not connected, not guessed', async () => {
    installFakeServer({ mode: 'Sandbox' })
    const app = renderApp('/')
    await screen.findByRole('heading', { level: 1, name: 'Overview' })
    await app.queryClient.refetchQueries({ queryKey: ['health'] })

    expect(screen.getByLabelText('Trading mode: not connected')).toBeInTheDocument()
  })

  test('an unknown address says so and keeps the menu', async () => {
    installFakeServer()
    renderApp('/no-such-screen')

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Page not found' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument()
  })
})

describe('not signed in', () => {
  test('any screen leads to the sign-in page', async () => {
    installFakeServer({ signedIn: false })
    const app = renderApp('/taxes')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
    expect(app.path()).toBe('/login')
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument()
  })

  test('a session that ends while working leads to the sign-in page', async () => {
    const server = installFakeServer()
    const app = renderApp('/positions')
    await screen.findByRole('heading', { level: 1, name: 'Positions' })

    // The session expires on the server; the next request of any screen is answered with 401.
    server.signedIn = false
    await app.queryClient
      .query({ queryKey: ['positions'], queryFn: () => getJson('/api/positions') })
      .catch(() => undefined)

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
    expect(app.path()).toBe('/login')
  })
})

describe('server not reachable', () => {
  test('says so and offers a retry instead of the sign-in page', async () => {
    const server = installFakeServer({ reachable: false })
    const app = renderApp('/taxes')

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot reach the iTrade server')
    expect(app.path()).toBe('/taxes')

    server.reachable = true
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Taxes' })).toBeInTheDocument()
  })
})
