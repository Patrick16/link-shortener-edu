import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import * as authApi from '../api/authApi'
import { getAccessToken, setAccessToken } from '../api/client'
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

// Largest delay setTimeout accepts before its 32-bit signed int overflows.
const MAX_TIMEOUT_MS = 2_147_483_647

function isExpired(expSeconds: number): boolean {
  return expSeconds * 1000 <= Date.now()
}

function userFromToken(token: string | null): AuthUser | null {
  if (!token) return null
  const claims = decodeJwt(token)
  if (!claims || isExpired(claims.exp)) return null
  return { email: claims.email, name: claims.name }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(() => {
    const inMemory = getAccessToken()
    // Clear an already-expired in-memory token rather than keeping a dead one around.
    return userFromToken(inMemory) ? inMemory : null
  })
  const [user, setUser] = useState<AuthUser | null>(() => userFromToken(token))

  const applyToken = useCallback((newToken: string) => {
    setAccessToken(newToken)
    setToken(newToken)
    setUser(userFromToken(newToken))
  }, [])

  const logout = useCallback(() => {
    setAccessToken(null)
    setToken(null)
    setUser(null)
    // Best-effort: revokes the refresh token server-side so it can't be used again. The
    // client-side session above is already torn down regardless of whether this succeeds.
    void authApi.logout().catch(() => {})
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

  // Once the current access token's exp passes, while the app stays open, try a silent refresh
  // first (using the httpOnly refresh-token cookie) and only fall back to logging out if that
  // fails - e.g. the refresh token itself expired, was revoked, or was never issued.
  useEffect(() => {
    if (!token) return

    const claims = decodeJwt(token)
    if (!claims) return

    let timer: ReturnType<typeof setTimeout>
    let cancelled = false

    const refreshOrLogout = async () => {
      try {
        const response = await authApi.refresh()
        if (!cancelled) applyToken(response.token)
      } catch {
        if (!cancelled) logout()
      }
    }

    // setTimeout's delay is a 32-bit signed int; a delay past ~24.8 days overflows and fires
    // almost immediately instead of waiting. Cap each wait and recompute on wake-up so long-lived
    // tokens (or the far-future expiries test fixtures use) don't trigger a premature refresh.
    const scheduleNextCheck = () => {
      const msUntilExpiry = claims.exp * 1000 - Date.now()
      if (msUntilExpiry <= 0) {
        void refreshOrLogout()
        return
      }
      timer = setTimeout(scheduleNextCheck, Math.min(msUntilExpiry, MAX_TIMEOUT_MS))
    }
    scheduleNextCheck()

    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [token, logout, applyToken])

  // The access token lives only in memory (never persisted), so a page reload/reopen always
  // starts with none. On mount, try exchanging the httpOnly refresh-token cookie for a new one
  // to restore the session transparently instead of showing the user as signed out.
  const needsSessionRestoreRef = useRef(!user)
  useEffect(() => {
    if (!needsSessionRestoreRef.current) return

    let cancelled = false
    authApi
      .refresh()
      .then((response) => {
        if (!cancelled) applyToken(response.token)
      })
      .catch(() => {
        // No valid refresh cookie (never logged in, or it expired/was revoked) - stay signed out.
      })

    return () => {
      cancelled = true
    }
  }, [applyToken])

  return <AuthContext.Provider value={{ user, login, register, logout }}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider')
  }
  return context
}
