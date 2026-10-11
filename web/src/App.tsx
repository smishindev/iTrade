import { createBrowserRouter } from 'react-router'
import { RouterProvider } from 'react-router/dom'
import { createAppQueryClient } from './api/queryClient'
import { AppProviders } from './AppProviders'
import { routes } from './routes'

const queryClient = createAppQueryClient()
const router = createBrowserRouter(routes)

export function App() {
  return (
    <AppProviders queryClient={queryClient}>
      <RouterProvider router={router} />
    </AppProviders>
  )
}
