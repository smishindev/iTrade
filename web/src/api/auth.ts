import { api } from './client'

/** Signs the owner in. Refusals arrive as `ApiError`: 401 wrong password, 429 locked (with the wait). */
export async function signIn(password: string): Promise<void> {
  await api.POST('/api/auth/login', { body: { password } })
}

export async function signOut(): Promise<void> {
  await api.POST('/api/auth/logout')
}
