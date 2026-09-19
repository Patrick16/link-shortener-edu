import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import * as linkApi from '../api/linkApi'
import CreateLinkPage from './CreateLinkPage'

vi.mock('../api/linkApi', async () => {
  const actual = await vi.importActual<typeof import('../api/linkApi')>('../api/linkApi')
  return {
    ...actual,
    createLink: vi.fn(),
  }
})

describe('CreateLinkPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
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

    await waitFor(() => expect(linkApi.createLink).toHaveBeenCalledWith('https://example.com/a/long/path'))
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
})
