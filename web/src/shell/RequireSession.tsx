import Alert from '@mui/material/Alert'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import CircularProgress from '@mui/material/CircularProgress'
import Snackbar from '@mui/material/Snackbar'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { signOut } from '../api/auth'
import { isUnauthorized } from '../api/http'
import { forgetOwnerData } from '../api/queryClient'
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
  const queryClient = useQueryClient()
  const logout = useMutation({
    mutationFn: signOut,
    // The session is asked again and the gate closes.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: sessionQuery.queryKey }),
  })

  // However the session ended (sign-out, expiry, a new password, another tab): nothing fetched for the
  // signed-in owner stays in memory.
  const signedOut = session.data !== undefined && !session.data.authenticated
  useEffect(() => {
    if (signedOut) {
      forgetOwnerData(queryClient)
    }
  }, [signedOut, queryClient])

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

  const signOutFailed = logout.isError && !isUnauthorized(logout.error)
  // The mode is shown only while the server confirms it: after a failed check the last answer is history,
  // and the badge goes back to "not connected".
  const mode = health.isError ? undefined : health.data?.mode
  return (
    <AppShell
      status={{ mode, account: undefined, reconciliation: undefined }}
      onSignOut={() => {
        logout.mutate()
      }}
    >
      <Outlet />
      <Snackbar
        open={signOutFailed}
        autoHideDuration={8000}
        onClose={() => {
          logout.reset()
        }}
      >
        <Alert severity="error" variant="filled">
          Could not sign out: the iTrade server did not answer. You are still signed in.
        </Alert>
      </Snackbar>
    </AppShell>
  )
}

function Centered({ children }: { children: React.ReactNode }) {
  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2 }}>{children}</Box>
  )
}
