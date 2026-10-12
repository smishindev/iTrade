/** An answer of the server that is not a success. The server sends RFC 9457 problem details. */
export class ApiError extends Error {
  readonly status: number

  /** Seconds the server asks to wait (`Retry-After`), when it said so. */
  readonly retryAfterSeconds: number | undefined

  constructor(status: number, title: string, retryAfterSeconds?: number) {
    super(title)
    this.name = 'ApiError'
    this.status = status
    this.retryAfterSeconds = retryAfterSeconds
  }
}

/** True for "the session is gone": the application then shows the sign-in page. */
export function isUnauthorized(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401
}

/** The `title` of a problem-details answer, or the status text when the body is something else. */
export async function problemTitle(response: Response): Promise<string> {
  try {
    const body: unknown = await response.json()
    if (typeof body === 'object' && body !== null && 'title' in body) {
      const { title } = body
      if (typeof title === 'string' && title !== '') {
        return title
      }
    }
  } catch {
    // not JSON: fall through to the status text
  }

  return response.statusText || `HTTP ${String(response.status)}`
}

/** The error for an answer that is not a success. */
export async function apiError(response: Response): Promise<ApiError> {
  const wait = Number(response.headers.get('Retry-After'))
  return new ApiError(
    response.status,
    await problemTitle(response),
    Number.isFinite(wait) && wait > 0 ? wait : undefined,
  )
}

/** GET a JSON document from the Host (same origin, with the session cookie). */
export async function getJson(path: string, signal?: AbortSignal): Promise<unknown> {
  const response = await fetch(path, {
    headers: { Accept: 'application/json' },
    credentials: 'same-origin',
    ...(signal ? { signal } : {}),
  })
  if (!response.ok) {
    throw await apiError(response)
  }

  return response.json()
}
