import Alert from '@mui/material/Alert'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import CircularProgress from '@mui/material/CircularProgress'
import { useQuery } from '@tanstack/react-query'
import { Navigate, Outlet, useLocation } from 'react-router'
import { healthQuery, sessionQuery } from '../api/session'
import { AppShell } from './AppShell'

/**
 * The gate in front of every screen: no session — the sign-in page (remembering where the visitor was going);
 * no answer from the server — an error with a retry, not a sign-in page (the password is not the problem).
 */
export function RequireSession() {
  const location = useLocation()
  const session = useQuery(sessionQuery)
  const health = useQuery({ ...healthQuery, enabled: session.data?.authenticated === true })

  if (session.isPending) {
    return (
      <Centered>
        <CircularProgress aria-label="Loading" />
      </Centered>
    )
  }

  if (session.isError) {
    return (
      <Centered>
        <Alert
          severity="error"
          action={
            <Button color="inherit" size="small" onClick={() => void session.refetch()}>
              Retry
            </Button>
          }
        >
          Cannot reach the iTrade server. Check that it is running.
        </Alert>
      </Centered>
    )
  }

  if (!session.data.authenticated) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  }

  return (
    <AppShell status={{ mode: health.data?.mode, account: undefined, reconciliation: undefined }}>
      <Outlet />
    </AppShell>
  )
}

function Centered({ children }: { children: React.ReactNode }) {
  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2 }}>{children}</Box>
  )
}
