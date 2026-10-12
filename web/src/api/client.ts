import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './generated/schema'
import { ApiError, problemTitle } from './http'

export type Schemas = components['schemas']

const changing = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

/**
 * Every changing request carries the antiforgery token (the Host answers 400 without it). The token is bound
 * to the signed-in user, so it is asked for right before the request instead of being kept: a token from
 * before sign-in would be refused after it.
 */
const antiforgery: Middleware = {
  async onRequest({ request }) {
    if (changing.has(request.method)) {
      const session = required(await api.GET('/api/auth/session'))
      request.headers.set(session.antiforgeryHeader, session.antiforgeryToken)
    }

    return request
  },
}

/** Any answer that is not a success becomes an `ApiError` with the title of the problem details. */
const errors: Middleware = {
  async onResponse({ response }) {
    if (!response.ok) {
      throw new ApiError(response.status, await problemTitle(response.clone()))
    }

    return response
  },
}

/**
 * The typed client of the Host API, generated from `openapi/v1.json` (`npm run gen:api`).
 * Paths, request bodies and answers are checked by the compiler against that document.
 */
export const api = createClient<paths>({
  baseUrl: window.location.origin,
  credentials: 'same-origin',
  // Looked up at call time, so tests can put a pretend server in its place.
  fetch: (request) => globalThis.fetch(request),
})
api.use(antiforgery, errors)

/** The body of a successful answer. Errors were thrown by the middleware, so "no body" is a broken answer. */
export function required<T>(result: { data?: T }): T {
  if (result.data === undefined) {
    throw new ApiError(502, 'The server sent an empty answer.')
  }

  return result.data
}
