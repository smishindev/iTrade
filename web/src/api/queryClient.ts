import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
import { ApiError, isUnauthorized } from './http'
import { sessionQuery } from './session'

/**
 * The query client of the application. Any request answered with 401 means the session ended (expired,
 * password changed, signed out elsewhere): the session is asked again and the router shows the sign-in page.
 */
export function createAppQueryClient(options: { retry: boolean } = { retry: true }): QueryClient {
  const onError = (error: unknown) => {
    if (isUnauthorized(error)) {
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
      mutations: { retry: false },
    },
  })
  return client
}
