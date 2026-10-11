import Box from '@mui/material/Box'
import Paper from '@mui/material/Paper'
import Typography from '@mui/material/Typography'

/** Where a visitor without a session lands. The form itself arrives with P2.5.05. */
export function LoginPage() {
  return (
    <Box component="main" sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2 }}>
      <Paper variant="outlined" sx={{ p: 4, width: '100%', maxWidth: 400 }}>
        <Typography variant="h5" component="h1" gutterBottom>
          Sign in
        </Typography>
        <Typography sx={{ color: 'text.secondary' }}>
          You are not signed in. The sign-in form is under construction.
        </Typography>
      </Paper>
    </Box>
  )
}
