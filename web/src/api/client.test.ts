import { describe, expect, test } from 'vitest'
import { installFakeServer } from '../test/fakeServer'
import { api, required } from './client'
import { ApiError, isUnauthorized } from './http'

describe('typed client', () => {
  test('reads a document with the types of the OpenAPI schema', async () => {
    installFakeServer({ signedIn: false })

    const session = required(await api.GET('/api/auth/session'))

    expect(session.authenticated).toBe(false)
    expect(session.antiforgeryHeader).toBe('X-XSRF-TOKEN')
  })

  test('adds a fresh antiforgery token to a changing request', async () => {
    const server = installFakeServer({ signedIn: true })

    const { response } = await api.POST('/api/auth/logout')

    expect(response.status).toBe(204)
    expect(server.signedIn).toBe(false)
    expect(server.requests).toEqual(['GET /api/auth/session', 'POST /api/auth/logout'])
  })

  test('the token is asked again after sign-in, because it is bound to the user', async () => {
    const server = installFakeServer({ signedIn: false })

    await api.POST('/api/auth/login', { body: { password: server.secret } })
    const { response } = await api.POST('/api/auth/logout')

    expect(response.status).toBe(204)
    expect(server.requests).toEqual([
      'GET /api/auth/session',
      'POST /api/auth/login',
      'GET /api/auth/session',
      'POST /api/auth/logout',
    ])
  })

  test('turns a refused request into an ApiError with the server’s explanation', async () => {
    installFakeServer({ signedIn: false })

    const refused = api.POST('/api/auth/login', { body: { password: 'not the secret' } })

    await expect(refused).rejects.toThrow(ApiError)
    await expect(refused).rejects.toMatchObject({
      status: 401,
      message: 'The password is not correct.',
    })
  })

  test('a 401 is recognised as "the session is gone"', async () => {
    installFakeServer({ signedIn: false })

    const error: unknown = await api.POST('/api/auth/logout').catch((reason: unknown) => reason)

    expect(isUnauthorized(error)).toBe(true)
  })

  test('a failed connection is not an ApiError', async () => {
    installFakeServer({ reachable: false })

    const error: unknown = await api.GET('/api/auth/session').catch((reason: unknown) => reason)

    expect(error).toBeInstanceOf(TypeError)
    expect(isUnauthorized(error)).toBe(false)
  })
})
