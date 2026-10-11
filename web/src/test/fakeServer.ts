import { vi } from 'vitest'

/** What the pretend Host answers. Tests change the fields while the application is running. */
export interface FakeServer {
  /** false: every request fails like a refused connection. */
  reachable: boolean
  signedIn: boolean
  mode: string
  /** Paths requested so far, in order. */
  requests: string[]
}

function json(status: number, body: unknown, type = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } })
}

function problem(status: number, title: string): Response {
  return json(status, { status, title }, 'application/problem+json')
}

/** Replaces `fetch` with a pretend Host for the current test. */
export function installFakeServer(initial: Partial<FakeServer> = {}): FakeServer {
  const server: FakeServer = {
    reachable: true,
    signedIn: true,
    mode: 'Paper',
    requests: [],
    ...initial,
  }
  const antiforgery = crypto.randomUUID()

  vi.stubGlobal('fetch', (input: RequestInfo | URL): Promise<Response> => {
    const address =
      typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
    const path = new URL(address, 'https://127.0.0.1').pathname
    server.requests.push(path)
    if (!server.reachable) {
      return Promise.reject(new TypeError('Failed to fetch'))
    }

    if (path === '/api/auth/session') {
      return Promise.resolve(
        json(200, {
          authenticated: server.signedIn,
          antiforgeryToken: antiforgery,
          antiforgeryHeader: 'X-XSRF-TOKEN',
        }),
      )
    }

    if (path === '/health') {
      return Promise.resolve(json(200, { status: 'Healthy', mode: server.mode, checks: {} }))
    }

    return Promise.resolve(
      server.signedIn ? problem(404, 'Not Found') : problem(401, 'Unauthorized'),
    )
  })

  return server
}
