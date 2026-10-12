import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
import { ApiError, isUnauthorized } from './http'
import { healthQuery, sessionQuery } from './session'

/** What may stay in memory without a session: whether there is one, and the server's public health. */
const publicKeys: readonly unknown[] = [sessionQuery.queryKey[0], healthQuery.queryKey[0]]

/**
 * Drops everything fetched for the signed-in owner. Called whenever the session ends, however it ends:
 * sign-out, expiry, a new password, a 401 from any request.
 */
export function forgetOwnerData(client: QueryClient): void {
  client.removeQueries({ predicate: ({ queryKey }) => !publicKeys.includes(queryKey[0]) })
}

/**
 * The query client of the application. Any request answered with 401 means the session ended (expired,
 * password changed, signed out elsewhere): the owner's data is dropped, the session is asked again and the
 * router shows the sign-in page.
 */
export function createAppQueryClient(options: { retry: boolean } = { retry: true }): QueryClient {
  const onError = (error: unknown) => {
    if (isUnauthorized(error)) {
      forgetOwnerData(client)
      void client.invalidateQueries({ queryKey: sessionQuery.queryKey })
    }
  }
  const client = new QueryClient({
    queryCache: new QueryCache({ onError }),
    mutationCache: new MutationCache({ onError }),
    defaultOptions: {
      queries: {
        // An answer of the server (4xx, 5xx) is final; only a failed connection is worth another try.
        retry: (failures, error) => options.retry && !(error instanceof ApiError) && failures < 2,
        staleTime: 10_000,
      },
      // Not retried, and not kept once nothing shows them: a mutation holds what was sent (a password, an order).
      mutations: { retry: false, gcTime: 0 },
    },
  })
  return client
}
