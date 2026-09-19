import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import LoginPage from './LoginPage'

const navigate = vi.fn()

vi.mock('../auth/AuthContext', () => ({
  useAuth: vi.fn(),
}))

vi.mock('react-router-dom', () => ({
  useNavigate: () => navigate,
}))

describe('LoginPage', () => {
  const login = vi.fn()
  const register = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(useAuth).mockReturnValue({ user: null, login, register, logout: vi.fn() })
  })

  it('renders the sign-in form by default', () => {
    render(<LoginPage />)

    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Name')).not.toBeInTheDocument()
  })

  it('switches to the register form when the toggle link is clicked', async () => {
    const user = userEvent.setup()
    render(<LoginPage />)

    await user.click(screen.getByText("Don't have an account? Create one"))

    expect(screen.getByRole('heading', { name: 'Create account' })).toBeInTheDocument()
    expect(screen.getByLabelText('Name')).toBeInTheDocument()
  })

  it('submits login credentials and navigates home on success', async () => {
    const user = userEvent.setup()
    login.mockResolvedValue(undefined)
    render(<LoginPage />)

    await user.type(screen.getByLabelText('Email'), 'alice@example.com')
    await user.type(screen.getByLabelText('Password'), 'password123')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => expect(login).toHaveBeenCalledWith('alice@example.com', 'password123'))
    expect(navigate).toHaveBeenCalledWith('/')
  })

  it('submits registration details in register mode', async () => {
    const user = userEvent.setup()
    register.mockResolvedValue(undefined)
    render(<LoginPage />)
    await user.click(screen.getByText("Don't have an account? Create one"))

    await user.type(screen.getByLabelText('Name'), 'Alice')
    await user.type(screen.getByLabelText('Email'), 'alice@example.com')
    await user.type(screen.getByLabelText('Password'), 'password123')
    await user.click(screen.getByRole('button', { name: 'Create account' }))

    await waitFor(() =>
      expect(register).toHaveBeenCalledWith('Alice', 'alice@example.com', 'password123'),
    )
    expect(navigate).toHaveBeenCalledWith('/')
  })

  it('shows the ApiError message when login fails', async () => {
    const user = userEvent.setup()
    login.mockRejectedValue(new ApiError(401, 'Invalid email or password.'))
    render(<LoginPage />)

    await user.type(screen.getByLabelText('Email'), 'alice@example.com')
    await user.type(screen.getByLabelText('Password'), 'wrong-password')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('Invalid email or password.')).toBeInTheDocument()
    expect(navigate).not.toHaveBeenCalled()
  })

  it('shows a generic message when login fails with a non-ApiError', async () => {
    const user = userEvent.setup()
    login.mockRejectedValue(new Error('network down'))
    render(<LoginPage />)

    await user.type(screen.getByLabelText('Email'), 'alice@example.com')
    await user.type(screen.getByLabelText('Password'), 'password123')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument()
  })
})
