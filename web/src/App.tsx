import CssBaseline from '@mui/material/CssBaseline'
import { ThemeProvider } from '@mui/material/styles'
import Typography from '@mui/material/Typography'
import { AppShell, type ShellStatus } from './shell/AppShell'
import { theme } from './theme'

/** Nothing is known about the server until the API client arrives (P2.5.04); the header says so. */
const notConnected: ShellStatus = { mode: undefined, account: undefined, reconciliation: undefined }

export function App() {
  return (
    <ThemeProvider theme={theme} defaultMode="system">
      <CssBaseline enableColorScheme />
      <AppShell status={notConnected} currentPath={window.location.pathname}>
        <Typography variant="h4" component="h1" gutterBottom>
          Overview
        </Typography>
        <Typography sx={{ color: 'text.secondary' }}>
          Portfolio accounting platform. The screens are under construction.
        </Typography>
      </AppShell>
    </ThemeProvider>
  )
}
