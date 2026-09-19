import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as authApi from '../api/authApi'
import { getStoredToken } from '../api/client'
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
    localStorage.clear()
    vi.resetAllMocks()
  })

  it('starts with no user when no token is stored', () => {
    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )

    expect(screen.getByTestId('user')).toHaveTextContent('anonymous')
  })

  it('restores the user from a token already in storage', () => {
    localStorage.setItem(
      'link-shortener:token',
      fakeJwt({ sub: '1', email: 'bob@example.com', name: 'Bob', exp: 9999999999 }),
    )

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )

    expect(screen.getByTestId('user')).toHaveTextContent('Bob <bob@example.com>')
  })

  it('login stores the token and updates the user', async () => {
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
    expect(getStoredToken()).toBe(token)
  })

  it('register stores the token and updates the user', async () => {
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

  it('logout clears the stored token and the user', async () => {
    const user = userEvent.setup()
    const token = fakeJwt({ sub: '1', email: 'alice@example.com', name: 'Alice', exp: 9999999999 })
    localStorage.setItem('link-shortener:token', token)

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    )
    await user.click(screen.getByText('logout'))

    expect(screen.getByTestId('user')).toHaveTextContent('anonymous')
    expect(getStoredToken()).toBeNull()
  })

  it('useAuth throws when used outside an AuthProvider', () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})

    expect(() => render(<TestConsumer />)).toThrow('useAuth must be used within AuthProvider')

    consoleError.mockRestore()
  })
})
