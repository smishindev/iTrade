import type { QueryClient } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { createAppQueryClient } from '../api/queryClient'
import { AppProviders } from '../AppProviders'
import { routes } from '../routes'

interface RenderedApp {
  queryClient: QueryClient
  /** The address the application is at now. */
  path: () => string
}

/** The whole application (providers, routes, session gate) at the given address, without a browser. */
export function renderApp(initialPath = '/'): RenderedApp {
  const queryClient = createAppQueryClient({ retry: false })
  const router = createMemoryRouter(routes, { initialEntries: [initialPath] })
  render(
    <AppProviders queryClient={queryClient}>
      <RouterProvider router={router} />
    </AppProviders>,
  )
  return {
    queryClient,
    path: () => router.state.location.pathname,
  }
}
