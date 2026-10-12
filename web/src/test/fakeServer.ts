import { vi } from 'vitest'

/** What the pretend Host answers. Tests change the fields while the application is running. */
export interface FakeServer {
  /** false: every request fails like a refused connection. */
  reachable: boolean
  signedIn: boolean
  mode: string
  /** The owner's secret; a sign-in with anything else is refused. */
  secret: string
  /** Seconds of lockout left; above zero every sign-in is answered with 429. */
  lockedForSeconds: number
  /** Requests so far, in order: "GET /api/auth/session". */
  requests: string[]
}

const antiforgeryHeader = 'X-XSRF-TOKEN'

function json(status: number, body: unknown, type = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } })
}

function problem(status: number, title: string, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify({ status, title }), {
    status,
    headers: { 'Content-Type': 'application/problem+json', ...headers },
  })
}

/** Replaces `fetch` with a pretend Host for the current test. It follows the rules of the real one. */
export function installFakeServer(initial: Partial<FakeServer> = {}): FakeServer {
  const server: FakeServer = {
    reachable: true,
    signedIn: true,
    mode: 'Paper',
    secret: crypto.randomUUID(),
    lockedForSeconds: 0,
    requests: [],
    ...initial,
  }
  // Like the real token, it is bound to the user: signing in or out makes the old one useless.
  const antiforgery = () => `${server.signedIn ? 'owner' : 'anonymous'}-${nonce}`
  const nonce = crypto.randomUUID()

  const answer = async (request: Request): Promise<Response> => {
    const path = new URL(request.url).pathname
    server.requests.push(`${request.method} ${path}`)
    if (!server.reachable) {
      throw new TypeError('Failed to fetch')
    }

    if (request.method === 'GET' && path === '/api/auth/session') {
      return json(200, {
        authenticated: server.signedIn,
        antiforgeryToken: antiforgery(),
        antiforgeryHeader,
      })
    }

    if (request.method === 'GET' && path === '/health') {
      return json(200, { status: 'Healthy', mode: server.mode, checks: {} })
    }

    if (request.method !== 'GET' && request.headers.get(antiforgeryHeader) !== antiforgery()) {
      return problem(400, 'The antiforgery token is missing or not valid.')
    }

    if (request.method === 'POST' && path === '/api/auth/login') {
      if (server.lockedForSeconds > 0) {
        return problem(429, 'Too many failed sign-in attempts. Try again later.', {
          'Retry-After': String(server.lockedForSeconds),
        })
      }

      const body = (await request.json()) as { password?: unknown }
      if (body.password !== server.secret) {
        return problem(401, 'The password is not correct.')
      }

      server.signedIn = true
      return new Response(null, { status: 204 })
    }

    if (!server.signedIn) {
      return problem(401, 'Unauthorized')
    }

    if (request.method === 'POST' && path === '/api/auth/logout') {
      server.signedIn = false
      return new Response(null, { status: 204 })
    }

    return problem(404, 'Not Found')
  }

  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    // The typed client sends a ready Request; plain `fetch('/health')` sends a path relative to the page.
    const request =
      input instanceof Request ? input : new Request(new URL(input, location.origin), init)
    return answer(request)
  })

  return server
}
