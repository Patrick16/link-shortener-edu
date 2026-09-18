import { createContext, useCallback, useContext, useState, type ReactNode } from 'react'
import * as authApi from '../api/authApi'
import { getStoredToken, setStoredToken } from '../api/client'
import { decodeJwt } from './jwt'

type AuthUser = {
  email: string
  name: string
}

type AuthContextValue = {
  user: AuthUser | null
  login: (email: string, password: string) => Promise<void>
  register: (name: string, email: string, password: string) => Promise<void>
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

function userFromToken(token: string | null): AuthUser | null {
  if (!token) return null
  const claims = decodeJwt(token)
  // An already-expired stored token still decodes fine here — the next authenticated API call
  // would 401 on it. Nothing currently does an authenticated call, so there's no refresh flow yet.
  return claims ? { email: claims.email, name: claims.name } : null
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(() => userFromToken(getStoredToken()))

  const applyToken = useCallback((token: string) => {
    setStoredToken(token)
    setUser(userFromToken(token))
  }, [])

  const login = useCallback(
    async (email: string, password: string) => {
      const response = await authApi.login({ email, password })
      applyToken(response.token)
    },
    [applyToken],
  )

  const register = useCallback(
    async (name: string, email: string, password: string) => {
      const response = await authApi.register({ name, email, password })
      applyToken(response.token)
    },
    [applyToken],
  )

  const logout = useCallback(() => {
    setStoredToken(null)
    setUser(null)
  }, [])

  return <AuthContext.Provider value={{ user, login, register, logout }}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider')
  }
  return context
}
