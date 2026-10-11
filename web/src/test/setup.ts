import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// Vitest globals are off, so Testing Library cannot register its own cleanup.
afterEach(() => {
  cleanup()
})

// jsdom has no layout and no matchMedia. Tests choose the viewport; "wide" (a desktop window) is the default.
let viewport: 'wide' | 'narrow' = 'wide'

export function setViewport(next: 'wide' | 'narrow') {
  viewport = next
}

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string): MediaQueryList => ({
    matches: query.includes('min-width') && viewport === 'wide',
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  }),
})
