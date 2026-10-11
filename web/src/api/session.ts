import { queryOptions } from '@tanstack/react-query'
import { getJson } from './http'

export type TradingMode = 'Paper' | 'Live'

/** `GET /api/auth/session`: whether the owner is signed in, plus the antiforgery token for changing requests. */
export interface Session {
  authenticated: boolean
  antiforgeryToken: string
  antiforgeryHeader: string
}

/** `GET /health`: open without sign-in; tells the mode the server runs in. */
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
    const body = await getJson('/api/auth/session', signal)
    const token = field(body, 'antiforgeryToken')
    const header = field(body, 'antiforgeryHeader')
    return {
      // Anything but an explicit `true` counts as "not signed in".
      authenticated: field(body, 'authenticated') === true,
      antiforgeryToken: typeof token === 'string' ? token : '',
      antiforgeryHeader: typeof header === 'string' ? header : '',
    }
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
