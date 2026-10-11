import type { RouteObject } from 'react-router'
import { LoginPage } from './pages/LoginPage'
import { PlaceholderPage } from './pages/PlaceholderPage'
import { RequireSession } from './shell/RequireSession'

/** Paths match `shell/navigation.ts`. Everything except the sign-in page sits behind the session gate. */
export const routes: RouteObject[] = [
  { path: '/login', element: <LoginPage /> },
  {
    element: <RequireSession />,
    children: [
      {
        index: true,
        element: (
          <PlaceholderPage
            title="Overview"
            description="Portfolio value, cash and what needs attention."
          />
        ),
      },
      {
        path: 'positions',
        element: <PlaceholderPage title="Positions" description="Holdings and their tax lots." />,
      },
      {
        path: 'contribute',
        element: (
          <PlaceholderPage
            title="Contribute"
            description="Plan a contribution and approve the purchase."
          />
        ),
      },
      {
        path: 'taxes',
        element: (
          <PlaceholderPage
            title="Taxes"
            description="Israeli tax on gains and dividends, by period."
          />
        ),
      },
      {
        path: 'reports',
        element: (
          <PlaceholderPage
            title="Reports"
            description="Monthly summary and the yearly tax package."
          />
        ),
      },
      {
        path: 'settings',
        element: (
          <PlaceholderPage title="Settings" description="Accounts, targets and assumptions." />
        ),
      },
      {
        path: '*',
        element: (
          <PlaceholderPage
            title="Page not found"
            description="There is no screen at this address."
          />
        ),
      },
    ],
  },
]
