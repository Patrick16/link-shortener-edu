import { apiFetch } from './client'
import type { AuthResponse, LoginRequest, RegisterRequest } from '../types'

const AUTH_API_URL = import.meta.env.VITE_AUTH_API_URL as string

export function register(request: RegisterRequest): Promise<AuthResponse> {
  return apiFetch<AuthResponse>(AUTH_API_URL, '/register', {
    method: 'POST',
    body: request,
    credentials: true,
  })
}

export function login(request: LoginRequest): Promise<AuthResponse> {
  return apiFetch<AuthResponse>(AUTH_API_URL, '/login', { method: 'POST', body: request, credentials: true })
}

// Exchanges the httpOnly refresh-token cookie (set by login/register/refresh itself) for a new
// access token. Rejects with ApiError(401) when there's no valid cookie - caller falls back to
// treating the user as signed out.
export function refresh(): Promise<AuthResponse> {
  return apiFetch<AuthResponse>(AUTH_API_URL, '/refresh', { method: 'POST', credentials: true })
}

// Revokes the refresh token server-side and clears the cookie. Best-effort from the caller's
// perspective - the client-side session is torn down regardless of whether this succeeds.
export function logout(): Promise<void> {
  return apiFetch<void>(AUTH_API_URL, '/logout', { method: 'POST', credentials: true })
}
