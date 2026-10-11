import CssBaseline from '@mui/material/CssBaseline'
import { ThemeProvider } from '@mui/material/styles'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, test } from 'vitest'
import { setViewport } from '../test/setup'
import { theme } from '../theme'
import { AppShell, type ShellStatus } from './AppShell'

const unknown: ShellStatus = { mode: undefined, account: undefined, reconciliation: undefined }

function renderShell(status: ShellStatus = unknown, currentPath = '/') {
  return render(
    <ThemeProvider theme={theme} defaultMode="light">
      <CssBaseline />
      <MemoryRouter initialEntries={[currentPath]}>
        <AppShell status={status}>
          <h1>Screen</h1>
        </AppShell>
      </MemoryRouter>
    </ThemeProvider>,
  )
}

beforeEach(() => {
  localStorage.clear()
  document.documentElement.className = ''
  setViewport('wide')
})

describe('header', () => {
  test('stays in place while the page scrolls', () => {
    renderShell()

    expect(screen.getByRole('banner')).toHaveStyle({ position: 'fixed' })
  })

  test.each([
    ['Paper', 'PAPER'],
    ['Live', 'LIVE'],
  ] as const)('shows the %s mode as %s', (mode, text) => {
    renderShell({ ...unknown, mode })

    expect(screen.getByLabelText(`Trading mode: ${mode}`)).toHaveTextContent(text)
  })

  test('paper and live badges do not look alike', () => {
    const { unmount } = renderShell({ ...unknown, mode: 'Paper' })
    const paper = screen.getByLabelText('Trading mode: Paper').className
    unmount()
    renderShell({ ...unknown, mode: 'Live' })

    expect(screen.getByLabelText('Trading mode: Live').className).not.toBe(paper)
  })

  test('shows the account and the reconciliation state, or a dash when unknown', () => {
    const { unmount } = renderShell()
    const empty = within(screen.getByRole('banner'))
    expect(empty.getByText('Account: —')).toBeInTheDocument()
    expect(empty.getByText('Reconciliation: —')).toBeInTheDocument()
    unmount()

    renderShell({ mode: 'Paper', account: 'DU0000000', reconciliation: 'matched' })
    const filled = within(screen.getByRole('banner'))
    expect(filled.getByText('Account: DU0000000')).toBeInTheDocument()
    expect(filled.getByText('Reconciliation: matched')).toBeInTheDocument()
  })
})

describe('menu', () => {
  test('lists the six screens in order and marks the current one', () => {
    renderShell(unknown, '/taxes')

    const links = within(screen.getByRole('navigation', { name: 'Main' })).getAllByRole('link')
    expect(links.map((link) => link.textContent)).toEqual([
      'Overview',
      'Positions',
      'Contribute',
      'Taxes',
      'Reports',
      'Settings',
    ])
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      '/',
      '/positions',
      '/contribute',
      '/taxes',
      '/reports',
      '/settings',
    ])
    expect(links.filter((link) => link.getAttribute('aria-current') === 'page')).toEqual([links[3]])
  })

  test('keeps a screen marked on its sub-pages, and Overview only on its own address', () => {
    renderShell(unknown, '/positions/VOO/lots')

    expect(screen.getByRole('link', { name: 'Positions' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current')
  })

  test('on a narrow screen opens from the header button', async () => {
    setViewport('narrow')
    renderShell()
    expect(screen.queryByRole('link', { name: 'Positions' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Open menu' }))

    expect(screen.getByRole('link', { name: 'Positions' })).toBeInTheDocument()
  })
})

describe('theme', () => {
  test('switches between light and dark and remembers the choice', async () => {
    renderShell()
    expect(document.documentElement).toHaveClass('light')

    await userEvent.click(screen.getByRole('button', { name: 'Switch to dark theme' }))

    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('mui-mode')).toBe('dark')
    expect(screen.getByRole('button', { name: 'Switch to light theme' })).toBeInTheDocument()
  })
})
