import { apiFetch } from './client'
import type { AuthResponse, LoginRequest, RegisterRequest } from '../types'

const AUTH_API_URL = import.meta.env.VITE_AUTH_API_URL as string

export function register(request: RegisterRequest): Promise<AuthResponse> {
  return apiFetch<AuthResponse>(AUTH_API_URL, '/register', { method: 'POST', body: request })
}

export function login(request: LoginRequest): Promise<AuthResponse> {
  return apiFetch<AuthResponse>(AUTH_API_URL, '/login', { method: 'POST', body: request })
}
