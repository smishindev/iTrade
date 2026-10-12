import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, test } from 'vitest'
import { installFakeServer, type FakeServer } from '../test/fakeServer'
import { renderApp } from '../test/renderApp'
import { setViewport } from '../test/setup'

beforeEach(() => {
  localStorage.clear()
  setViewport('wide')
})

async function passwordField(): Promise<HTMLElement> {
  return screen.findByLabelText('Password')
}

async function submit(text: string) {
  const field = await passwordField()
  if (text !== '') {
    await userEvent.type(field, text)
  }

  await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
}

function loginRequests(server: FakeServer): number {
  return server.requests.filter((request) => request === 'POST /api/auth/login').length
}

describe('sign in', () => {
  test('the right password opens the screen the visitor was going to', async () => {
    const server = installFakeServer({ signedIn: false })
    const app = renderApp('/taxes')
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Sign in to iTrade' }),
    ).toBeVisible()

    await submit(server.secret)

    expect(await screen.findByRole('heading', { level: 1, name: 'Taxes' })).toBeInTheDocument()
    expect(app.path()).toBe('/taxes')
    expect(server.signedIn).toBe(true)
  })

  test('the password is typed hidden and offered to the password manager', async () => {
    installFakeServer({ signedIn: false })
    renderApp('/login')

    const field = await passwordField()

    expect(field).toHaveAttribute('type', 'password')
    expect(field).toHaveAttribute('autocomplete', 'current-password')
    expect(field).toHaveFocus()
  })

  test('a wrong password is explained, the field is emptied, nothing opens', async () => {
    const server = installFakeServer({ signedIn: false })
    const app = renderApp('/taxes')

    await submit(crypto.randomUUID())

    expect(await screen.findByRole('alert')).toHaveTextContent('The password is not correct.')
    expect(await passwordField()).toHaveValue('')
    expect(app.path()).toBe('/login')
    expect(server.signedIn).toBe(false)
  })

  test('an empty password is not sent', async () => {
    const server = installFakeServer({ signedIn: false })
    renderApp('/login')

    await submit('')

    expect(await screen.findByText('Enter the password')).toBeInTheDocument()
    expect(loginRequests(server)).toBe(0)
  })

  test('a locked account says how long to wait and does not accept attempts', async () => {
    const server = installFakeServer({ signedIn: false, lockedForSeconds: 900 })
    renderApp('/login')

    await submit(server.secret)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Too many failed attempts. Try again in 15 minutes.',
    )
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeDisabled()
    expect(server.signedIn).toBe(false)
  })

  test('the lockout counts down and lets the visitor try again when it is over', async () => {
    const server = installFakeServer({ signedIn: false, lockedForSeconds: 2 })
    renderApp('/login')
    await submit(server.secret)
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again in 2 seconds.')

    server.lockedForSeconds = 0
    await waitFor(
      () => {
        expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled()
      },
      { timeout: 4000 },
    )
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()

    await submit(server.secret)
    expect(await screen.findByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument()
  })

  test('a server that does not answer is named as the problem, not the password', async () => {
    const server = installFakeServer({ signedIn: false })
    renderApp('/login')
    await passwordField()
    server.reachable = false

    await submit(server.secret)

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot reach the iTrade server')
  })

  test('a signed-in visitor of the sign-in page is sent to the application', async () => {
    installFakeServer({ signedIn: true })
    const app = renderApp('/login')

    expect(await screen.findByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument()
    expect(app.path()).toBe('/')
  })
})

describe('sign out', () => {
  test('ends the session and shows the sign-in page', async () => {
    const server = installFakeServer({ signedIn: true })
    const app = renderApp('/positions')
    await screen.findByRole('heading', { level: 1, name: 'Positions' })

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Sign in to iTrade' }),
    ).toBeVisible()
    expect(app.path()).toBe('/login')
    expect(server.signedIn).toBe(false)
  })

  test('keeps nothing of the owner’s data in memory', async () => {
    installFakeServer({ signedIn: true })
    const app = renderApp('/')
    await screen.findByRole('heading', { level: 1, name: 'Overview' })
    app.queryClient.setQueryData(['positions'], [{ ticker: 'VOO' }])

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }))
    await screen.findByRole('heading', { level: 1, name: 'Sign in to iTrade' })

    expect(app.queryClient.getQueryData(['positions'])).toBeUndefined()
  })

  test('says so when the server did not answer, and stays signed in', async () => {
    const server = installFakeServer({ signedIn: true })
    const app = renderApp('/positions')
    await screen.findByRole('heading', { level: 1, name: 'Positions' })
    server.reachable = false

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not sign out')
    expect(app.path()).toBe('/positions')
    expect(server.signedIn).toBe(true)
  })
})
