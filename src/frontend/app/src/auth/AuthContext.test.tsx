import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as authApi from '../api/authApi'
import { getAccessToken, setAccessToken } from '../api/client'
import { AuthProvider, useAuth } from './AuthContext'

vi.mock('../api/authApi')

function encodeSegment(value: unknown): string {
  const json = JSON.stringify(value)
  return btoa(json).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function fakeJwt(payload: Record<string, unknown>): string {
  const header = encodeSegment({ alg: 'HS256', typ: 'JWT' })
  const body = encodeSegment(payload)
  return `${header}.${body}.fake-signature`
}

function TestConsumer() {
  const { user, login, register, logout } = useAuth()
  return (
    <div>
      <span data-testid="user">{user ? `${user.name} <${user.email}>` : 'anonymous'}</span>
      <button onClick={() => login('alice@example.com', 'password123')}>login</button>
      <button onClick={() => register('Alice', 'alice@example.com', 'password123')}>register</button>
      <button onClick={() => logout()}>logout</button>
    </div>
  )
}

describe('AuthProvider / useAuth', () => {
  beforeEach(() => {
    // The access token lives in a module-level variable (deliberately not localStorage - see
    // client.ts), so it has to be reset by hand between tests instead of localStorage.clear().
    setAccessToken(null)
    vi.resetAllMocks()
    // Default: no valid refresh-token cookie. Individual tests override this to exercise the
    // silent-restore / silent-refresh paths.
    vi.mocked(authApi.refresh).mockRejectedValue(new Error('no refresh token'))
    vi.mocked(authApi.logout).mockResolvedValue(undefined)
  })

  it('starts with no user when no token is set', () => {
    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )

    expect(screen.getByTestId('user')).toHaveTextContent('anonymous')
  })

  it('restores the session via silent refresh when no access token is set', async () => {
    const token = fakeJwt({ sub: '1', email: 'carol@example.com', name: 'Carol', exp: 9999999999 })
    vi.mocked(authApi.refresh).mockResolvedValue({ token, expiresAt: '2099-01-01T00:00:00Z' })

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )

    await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('Carol <carol@example.com>'))
    expect(getAccessToken()).toBe(token)
  })

  it('under StrictMode, the session-restore effect only calls refresh once (regression)', async () => {
    // StrictMode double-invokes effects in dev (mount -> cleanup -> mount again). The session-
    // restore effect used to gate on a ref it never flipped off, so the second invocation saw the
    // same "needs restore" flag and fired a second POST /refresh with the same cookie - which
    // refresh-token rotation's reuse detection would treat as a replay and log the user back out.
    const token = fakeJwt({ sub: '1', email: 'carol@example.com', name: 'Carol', exp: 9999999999 })
    vi.mocked(authApi.refresh).mockResolvedValue({ token, expiresAt: '2099-01-01T00:00:00Z' })

    render(
      <StrictMode>
        <AuthProvider>
          <TestConsumer />
        </AuthProvider>
      </StrictMode>,
    )

    await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('Carol <carol@example.com>'))
    expect(authApi.refresh).toHaveBeenCalledTimes(1)
  })

  it('restores the user from a token already held in memory', () => {
    setAccessToken(fakeJwt({ sub: '1', email: 'bob@example.com', name: 'Bob', exp: 9999999999 }))

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )

    expect(screen.getByTestId('user')).toHaveTextContent('Bob <bob@example.com>')
  })

  it('login sets the token and updates the user', async () => {
    const user = userEvent.setup()
    const token = fakeJwt({ sub: '1', email: 'alice@example.com', name: 'Alice', exp: 9999999999 })
    vi.mocked(authApi.login).mockResolvedValue({ token, expiresAt: '2099-01-01T00:00:00Z' })

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )
    await user.click(screen.getByText('login'))

    await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('Alice <alice@example.com>'))
    expect(authApi.login).toHaveBeenCalledWith({ email: 'alice@example.com', password: 'password123' })
    expect(getAccessToken()).toBe(token)
  })

  it('register sets the token and updates the user', async () => {
    const user = userEvent.setup()
    const token = fakeJwt({ sub: '1', email: 'alice@example.com', name: 'Alice', exp: 9999999999 })
    vi.mocked(authApi.register).mockResolvedValue({ token, expiresAt: '2099-01-01T00:00:00Z' })

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )
    await user.click(screen.getByText('register'))

    await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('Alice <alice@example.com>'))
    expect(authApi.register).toHaveBeenCalledWith({
      name: 'Alice',
      email: 'alice@example.com',
      password: 'password123',
    })
  })

  it('logout clears the token and the user', async () => {
    const user = userEvent.setup()
    const token = fakeJwt({ sub: '1', email: 'alice@example.com', name: 'Alice', exp: 9999999999 })
    setAccessToken(token)

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )
    await user.click(screen.getByText('logout'))

    expect(screen.getByTestId('user')).toHaveTextContent('anonymous')
    expect(getAccessToken()).toBeNull()
  })

  it('logout also revokes the refresh token server-side', async () => {
    const user = userEvent.setup()
    const token = fakeJwt({ sub: '1', email: 'alice@example.com', name: 'Alice', exp: 9999999999 })
    setAccessToken(token)

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )
    await user.click(screen.getByText('logout'))

    await waitFor(() => expect(authApi.logout).toHaveBeenCalledTimes(1))
  })

  it('silently refreshes the access token once it expires while the app stays open', async () => {
    vi.useFakeTimers()
    try {
      const expiringToken = fakeJwt({
        sub: '1',
        email: 'alice@example.com',
        name: 'Alice',
        exp: Math.floor(Date.now() / 1000) + 1,
      })
      setAccessToken(expiringToken)
      const refreshedToken = fakeJwt({ sub: '1', email: 'alice@example.com', name: 'Alice', exp: 9999999999 })
      vi.mocked(authApi.refresh).mockResolvedValue({ token: refreshedToken, expiresAt: '2099-01-01T00:00:00Z' })

      render(
        <AuthProvider>
          <TestConsumer />
        </AuthProvider>,
      )

      await vi.advanceTimersByTimeAsync(1_100)

      expect(getAccessToken()).toBe(refreshedToken)
      expect(screen.getByTestId('user')).toHaveTextContent('Alice <alice@example.com>')
    } finally {
      vi.useRealTimers()
    }
  })

  it('logs out when the access token expires and the silent refresh fails', async () => {
    vi.useFakeTimers()
    try {
      const expiringToken = fakeJwt({
        sub: '1',
        email: 'alice@example.com',
        name: 'Alice',
        exp: Math.floor(Date.now() / 1000) + 1,
      })
      setAccessToken(expiringToken)
      vi.mocked(authApi.refresh).mockRejectedValue(new Error('refresh token expired'))

      render(
        <AuthProvider>
          <TestConsumer />
        </AuthProvider>,
      )

      await vi.advanceTimersByTimeAsync(1_100)
      vi.useRealTimers()

      expect(getAccessToken()).toBeNull()
      await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('anonymous'))
    } finally {
      vi.useRealTimers()
    }
  })

  it('useAuth throws when used outside an AuthProvider', () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})

    expect(() => render(<TestConsumer />)).toThrow('useAuth must be used within AuthProvider')

    consoleError.mockRestore()
  })
})
