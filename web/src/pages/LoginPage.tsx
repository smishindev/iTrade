import { zodResolver } from '@hookform/resolvers/zod'
import Alert from '@mui/material/Alert'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Paper from '@mui/material/Paper'
import Stack from '@mui/material/Stack'
import TextField from '@mui/material/TextField'
import Typography from '@mui/material/Typography'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { Navigate, useLocation } from 'react-router'
import { z } from 'zod'
import { signIn } from '../api/auth'
import { ApiError } from '../api/http'
import { sessionQuery } from '../api/session'

const schema = z.object({
  password: z.string().min(1, 'Enter the password').max(256, 'The password is too long'),
})
type LoginForm = z.infer<typeof schema>

/** Where to go after sign-in: the screen the visitor wanted, if it is an address of this application. */
function destination(state: unknown): string {
  const from =
    typeof state === 'object' && state !== null && 'from' in state ? state.from : undefined
  return typeof from === 'string' && from.startsWith('/') && !from.startsWith('//') ? from : '/'
}

function wait(seconds: number): string {
  if (seconds < 60) {
    return `${String(seconds)} ${seconds === 1 ? 'second' : 'seconds'}`
  }

  const minutes = Math.ceil(seconds / 60)
  return `${String(minutes)} ${minutes === 1 ? 'minute' : 'minutes'}`
}

function isLockout(error: unknown): error is ApiError {
  return error instanceof ApiError && error.status === 429
}

function explain(error: unknown): string {
  return error instanceof ApiError
    ? error.message
    : 'Cannot reach the iTrade server. Check that it is running.'
}

/** The only screen open without a session. One owner, so only a password is asked. */
export function LoginPage() {
  const location = useLocation()
  const queryClient = useQueryClient()
  const session = useQuery(sessionQuery)
  const {
    register,
    handleSubmit,
    resetField,
    setFocus,
    formState: { errors },
  } = useForm<LoginForm>({ resolver: zodResolver(schema), defaultValues: { password: '' } })

  // After five wrong passwords the server refuses every attempt for a while and says for how long.
  const [lockSeconds, setLockSeconds] = useState(0)
  const locked = lockSeconds > 0
  useEffect(() => {
    if (!locked) {
      return
    }

    const timer = setInterval(() => {
      setLockSeconds((seconds) => Math.max(0, seconds - 1))
    }, 1000)
    return () => {
      clearInterval(timer)
    }
  }, [locked])

  const login = useMutation({
    mutationFn: (form: LoginForm) => signIn(form.password),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: sessionQuery.queryKey }),
    onError: (error) => {
      resetField('password')
      setFocus('password')
      if (isLockout(error)) {
        setLockSeconds(Math.ceil(error.retryAfterSeconds ?? 60))
      }
    },
  })

  if (session.data?.authenticated === true) {
    return <Navigate to={destination(location.state)} replace />
  }

  const { ref: passwordRef, ...passwordField } = register('password')
  return (
    <Box component="main" sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2 }}>
      <Paper variant="outlined" sx={{ p: 4, width: '100%', maxWidth: 400 }}>
        <Stack
          component="form"
          noValidate
          spacing={2}
          onSubmit={(event) => {
            void handleSubmit((form) => {
              login.mutate(form)
            })(event)
          }}
        >
          <Typography variant="h5" component="h1">
            Sign in to iTrade
          </Typography>
          {locked ? (
            <Alert severity="warning">
              Too many failed attempts. Try again in {wait(lockSeconds)}.
            </Alert>
          ) : (
            // A lockout that has run out is over: its message must not linger.
            login.isError &&
            !isLockout(login.error) && <Alert severity="error">{explain(login.error)}</Alert>
          )}
          <TextField
            {...passwordField}
            inputRef={passwordRef}
            label="Password"
            type="password"
            autoComplete="current-password"
            autoFocus
            fullWidth
            error={errors.password !== undefined}
            helperText={errors.password?.message ?? ' '}
          />
          <Button
            type="submit"
            variant="contained"
            size="large"
            disabled={locked}
            loading={login.isPending}
          >
            Sign in
          </Button>
        </Stack>
      </Paper>
    </Box>
  )
}
