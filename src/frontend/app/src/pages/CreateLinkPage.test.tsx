import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import * as linkApi from '../api/linkApi'
import * as authContext from '../auth/AuthContext'
import CreateLinkPage from './CreateLinkPage'

vi.mock('../api/linkApi', async () => {
  const actual = await vi.importActual<typeof import('../api/linkApi')>('../api/linkApi')
  return {
    ...actual,
    createLink: vi.fn(),
  }
})

vi.mock('../auth/AuthContext', async () => {
  const actual = await vi.importActual<typeof import('../auth/AuthContext')>('../auth/AuthContext')
  return {
    ...actual,
    useAuth: vi.fn(),
  }
})

function mockUser(user: { email: string; name: string } | null) {
  vi.mocked(authContext.useAuth).mockReturnValue({
    user,
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  })
}

describe('CreateLinkPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    // Signed in by default - most tests below exercise the plain-hash path, which behaves
    // identically either way; the alias-specific tests set their own auth state explicitly.
    mockUser({ email: 'alice@example.com', name: 'Alice' })
  })

  it('renders the shorten form', () => {
    render(<CreateLinkPage />)

    expect(screen.getByRole('heading', { name: 'Shorten a link' })).toBeInTheDocument()
    expect(screen.getByPlaceholderText('https://example.com/a/very/long/path')).toHaveValue('')
  })

  it('shows the shortened link and clears the input on success', async () => {
    const user = userEvent.setup()
    vi.mocked(linkApi.createLink).mockResolvedValue({
      shortenLink: 'abc12345',
      createdAt: '2099-01-01T00:00:00Z',
    })
    render(<CreateLinkPage />)

    const input = screen.getByPlaceholderText('https://example.com/a/very/long/path')
    await user.type(input, 'https://example.com/a/long/path')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))

    await waitFor(() => expect(linkApi.createLink).toHaveBeenCalledWith('https://example.com/a/long/path', undefined))
    expect(screen.getByRole('link', { name: linkApi.buildShortUrl('abc12345') })).toBeInTheDocument()
    expect(input).toHaveValue('')
  })

  it('shows the ApiError message when creation fails', async () => {
    const user = userEvent.setup()
    vi.mocked(linkApi.createLink).mockRejectedValue(new ApiError(400, 'That does not look like a URL.'))
    render(<CreateLinkPage />)

    await user.type(screen.getByPlaceholderText('https://example.com/a/very/long/path'), 'https://example.com')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))

    expect(await screen.findByText('That does not look like a URL.')).toBeInTheDocument()
  })

  it('shows a generic message when creation fails with a non-ApiError', async () => {
    const user = userEvent.setup()
    vi.mocked(linkApi.createLink).mockRejectedValue(new Error('network down'))
    render(<CreateLinkPage />)

    await user.type(screen.getByPlaceholderText('https://example.com/a/very/long/path'), 'https://example.com')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))

    expect(
      await screen.findByText('Could not shorten that link. Please try again.'),
    ).toBeInTheDocument()
  })

  it('copies the short link to the clipboard when Copy is clicked', async () => {
    const user = userEvent.setup()
    // userEvent.setup() installs its own navigator.clipboard stub - spy on it after setup,
    // rather than replacing it, so user-event's internal bookkeeping stays intact.
    const writeText = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue(undefined)
    vi.mocked(linkApi.createLink).mockResolvedValue({
      shortenLink: 'abc12345',
      createdAt: '2099-01-01T00:00:00Z',
    })
    render(<CreateLinkPage />)
    await user.type(screen.getByPlaceholderText('https://example.com/a/very/long/path'), 'https://example.com')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))
    await screen.findByRole('link', { name: linkApi.buildShortUrl('abc12345') })

    await user.click(screen.getByRole('button', { name: 'Copy' }))

    expect(writeText).toHaveBeenCalledWith(linkApi.buildShortUrl('abc12345'))
    expect(await screen.findByRole('button', { name: 'Copied!' })).toBeInTheDocument()
  })

  it('clears the pending "Copied!" reset timeout on unmount', async () => {
    // Regression: unmounting within the 2s "Copied!" window used to leave the timeout armed - it
    // still fired and called setState on an unmounted component (a React warning, and the kind of
    // pattern that becomes a real leak if this page grows more one-shot timers).
    const user = userEvent.setup()
    vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue(undefined)
    const clearTimeoutSpy = vi.spyOn(window, 'clearTimeout')
    vi.mocked(linkApi.createLink).mockResolvedValue({
      shortenLink: 'abc12345',
      createdAt: '2099-01-01T00:00:00Z',
    })
    const { unmount } = render(<CreateLinkPage />)
    await user.type(screen.getByPlaceholderText('https://example.com/a/very/long/path'), 'https://example.com')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))
    await screen.findByRole('link', { name: linkApi.buildShortUrl('abc12345') })
    await user.click(screen.getByRole('button', { name: 'Copy' }))
    await screen.findByRole('button', { name: 'Copied!' })

    unmount()

    expect(clearTimeoutSpy).toHaveBeenCalled()
  })

  it('disables the alias field and shows a sign-in hint when signed out', () => {
    mockUser(null)
    render(<CreateLinkPage />)

    expect(screen.getByPlaceholderText('my-cool-link')).toBeDisabled()
    expect(screen.getByText('Sign in to pick your own short link instead of a random one.')).toBeInTheDocument()
  })

  it('does not show the sign-in hint, and the alias field is enabled, when signed in', () => {
    render(<CreateLinkPage />)

    expect(screen.getByPlaceholderText('my-cool-link')).toBeEnabled()
    expect(screen.queryByText('Sign in to pick your own short link instead of a random one.')).not.toBeInTheDocument()
  })

  it('sends the alias when signed in and one is entered', async () => {
    const user = userEvent.setup()
    vi.mocked(linkApi.createLink).mockResolvedValue({ shortenLink: 'my-link', createdAt: '2099-01-01T00:00:00Z' })
    render(<CreateLinkPage />)

    await user.type(screen.getByPlaceholderText('https://example.com/a/very/long/path'), 'https://example.com')
    await user.type(screen.getByPlaceholderText('my-cool-link'), 'my-link')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))

    await waitFor(() => expect(linkApi.createLink).toHaveBeenCalledWith('https://example.com', 'my-link'))
    expect(screen.getByPlaceholderText('my-cool-link')).toHaveValue('')
  })

  it('omits the alias when the field is left empty', async () => {
    const user = userEvent.setup()
    vi.mocked(linkApi.createLink).mockResolvedValue({ shortenLink: 'abc12345', createdAt: '2099-01-01T00:00:00Z' })
    render(<CreateLinkPage />)

    await user.type(screen.getByPlaceholderText('https://example.com/a/very/long/path'), 'https://example.com')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))

    await waitFor(() => expect(linkApi.createLink).toHaveBeenCalledWith('https://example.com', undefined))
  })

  it('shows the "ready right away" hint for an alias result instead of the async-delay one', async () => {
    const user = userEvent.setup()
    vi.mocked(linkApi.createLink).mockResolvedValue({ shortenLink: 'my-link', createdAt: '2099-01-01T00:00:00Z' })
    render(<CreateLinkPage />)

    await user.type(screen.getByPlaceholderText('https://example.com/a/very/long/path'), 'https://example.com')
    await user.type(screen.getByPlaceholderText('my-cool-link'), 'my-link')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))

    expect(await screen.findByText('Ready to use right away.')).toBeInTheDocument()
    expect(screen.getByText('Custom alias')).toBeInTheDocument()
    expect(screen.queryByText(/created asynchronously/)).not.toBeInTheDocument()
  })

  it('shows an alias-conflict error message from the backend', async () => {
    const user = userEvent.setup()
    vi.mocked(linkApi.createLink).mockRejectedValue(new ApiError(409, "Alias 'taken' is already taken."))
    render(<CreateLinkPage />)

    await user.type(screen.getByPlaceholderText('https://example.com/a/very/long/path'), 'https://example.com')
    await user.type(screen.getByPlaceholderText('my-cool-link'), 'taken')
    await user.click(screen.getByRole('button', { name: 'Shorten' }))

    expect(await screen.findByText("Alias 'taken' is already taken.")).toBeInTheDocument()
  })
})
