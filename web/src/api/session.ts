import { queryOptions } from '@tanstack/react-query'
import { api, required, type Schemas } from './client'
import { getJson } from './http'

export type TradingMode = 'Paper' | 'Live'

/** `GET /api/auth/session`: whether the owner is signed in, plus the antiforgery token for changing requests. */
export type Session = Schemas['SessionResponse']

/** `GET /health`: open without sign-in; tells the mode the server runs in. Not part of the OpenAPI document. */
export interface Health {
  healthy: boolean
  mode: TradingMode | undefined
}

function field(body: unknown, name: string): unknown {
  return typeof body === 'object' && body !== null
    ? (body as Record<string, unknown>)[name]
    : undefined
}

export const sessionQuery = queryOptions({
  queryKey: ['session'],
  queryFn: async ({ signal }): Promise<Session> => {
    const session = required(await api.GET('/api/auth/session', { signal }))
    // Anything but an explicit `true` counts as "not signed in", whatever the types promise.
    return { ...session, authenticated: (session.authenticated as unknown) === true }
  },
})

export const healthQuery = queryOptions({
  queryKey: ['health'],
  queryFn: async ({ signal }): Promise<Health> => {
    const body = await getJson('/health', signal)
    const mode = field(body, 'mode')
    return {
      healthy: field(body, 'status') === 'Healthy',
      // The badge shows only a mode the server named; an unknown value is "not known", never a guess.
      mode: mode === 'Paper' || mode === 'Live' ? mode : undefined,
    }
  },
  refetchInterval: 60_000,
})
