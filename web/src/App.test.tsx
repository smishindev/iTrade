import { render, screen, within } from '@testing-library/react'
import { expect, test } from 'vitest'
import { App } from './App'

test('shows the shell around the first screen', () => {
  render(<App />)

  expect(within(screen.getByRole('banner')).getByText('iTrade')).toBeInTheDocument()
  expect(screen.getByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument()
  expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument()
})

test('says "not connected" until the server tells the mode', () => {
  render(<App />)

  expect(screen.getByLabelText('Trading mode: not connected')).toHaveTextContent('NOT CONNECTED')
})
