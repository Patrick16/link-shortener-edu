import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import * as linkApi from '../api/linkApi'
import * as statsApi from '../api/statsApi'
import * as authContext from '../auth/AuthContext'
import DashboardPage from './DashboardPage'

vi.mock('../api/linkApi', async () => {
  const actual = await vi.importActual<typeof import('../api/linkApi')>('../api/linkApi')
  return {
    ...actual,
    getLinks: vi.fn(),
  }
})

vi.mock('../api/statsApi', () => ({
  getClickSummary: vi.fn(),
}))

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

function page(overrides: Partial<Awaited<ReturnType<typeof linkApi.getLinks>>> = {}) {
  return {
    items: [],
    page: 1,
    pageSize: 50,
    totalCount: 0,
    totalPages: 0,
    ...overrides,
  }
}

describe('DashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders nothing when signed out', () => {
    mockUser(null)

    const { container } = render(<DashboardPage />)

    expect(container).toBeEmptyDOMElement()
    expect(linkApi.getLinks).not.toHaveBeenCalled()
  })

  it('shows an empty-state message when the user has no links', async () => {
    mockUser({ email: 'alice@example.com', name: 'Alice' })
    vi.mocked(linkApi.getLinks).mockResolvedValue(page())

    render(<DashboardPage />)

    expect(await screen.findByText("You haven't created any links yet.")).toBeInTheDocument()
  })

  it('lists links with their click counts', async () => {
    mockUser({ email: 'alice@example.com', name: 'Alice' })
    vi.mocked(linkApi.getLinks).mockResolvedValue(
      page({
        items: [
          { shortenLink: 'abc12345', originalLink: 'https://example.com/a', createdAt: '2099-01-01T00:00:00Z', clickCount: 5 },
          { shortenLink: 'def67890', originalLink: 'https://example.com/b', createdAt: '2099-01-01T00:00:00Z', clickCount: 1 },
        ],
        totalCount: 2,
      }),
    )

    render(<DashboardPage />)

    expect(await screen.findByText('5 clicks')).toBeInTheDocument()
    expect(screen.getByText('1 click')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: linkApi.buildShortUrl('abc12345') })).toBeInTheDocument()
  })

  it('shows the ApiError message when loading fails', async () => {
    mockUser({ email: 'alice@example.com', name: 'Alice' })
    vi.mocked(linkApi.getLinks).mockRejectedValue(new ApiError(401, 'Unauthorized'))

    render(<DashboardPage />)

    expect(await screen.findByText('Unauthorized')).toBeInTheDocument()
  })

  it('paginates to the next page and back', async () => {
    const user = userEvent.setup()
    mockUser({ email: 'alice@example.com', name: 'Alice' })
    vi.mocked(linkApi.getLinks).mockImplementation((requestedPage = 1) =>
      Promise.resolve(
        page({
          items: [
            {
              shortenLink: `p${requestedPage}`,
              originalLink: 'https://example.com',
              createdAt: '2099-01-01T00:00:00Z',
              clickCount: 0,
            },
          ],
          page: requestedPage,
          totalCount: 100,
          totalPages: 2,
        }),
      ),
    )

    render(<DashboardPage />)
    await screen.findByText('Page 1 of 2')
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Next' }))

    await waitFor(() => expect(linkApi.getLinks).toHaveBeenCalledWith(2))
    await screen.findByText('Page 2 of 2')
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()
  })

  it('expands stats on click and fetches the summary once', async () => {
    const user = userEvent.setup()
    mockUser({ email: 'alice@example.com', name: 'Alice' })
    vi.mocked(linkApi.getLinks).mockResolvedValue(
      page({
        items: [{ shortenLink: 'abc12345', originalLink: 'https://example.com/a', createdAt: '2099-01-01T00:00:00Z', clickCount: 5 }],
        totalCount: 1,
      }),
    )
    vi.mocked(statsApi.getClickSummary).mockResolvedValue({
      hash: 'abc12345',
      totalClicks: 5,
      byDay: [{ key: '2099-01-01', count: 5 }],
      byCountry: [{ key: 'Germany', count: 5 }],
      byDevice: [],
      byBrowser: [],
    })

    render(<DashboardPage />)
    await user.click(await screen.findByRole('button', { name: 'Stats' }))

    expect(await screen.findByText('5')).toBeInTheDocument()
    expect(screen.getByText('Germany (5)')).toBeInTheDocument()
    expect(statsApi.getClickSummary).toHaveBeenCalledWith('abc12345')

    // Collapsing and re-expanding must not refetch - this is a CQRS read model, not a live query.
    await user.click(screen.getByRole('button', { name: 'Hide stats' }))
    await user.click(screen.getByRole('button', { name: 'Stats' }))
    expect(statsApi.getClickSummary).toHaveBeenCalledTimes(1)
  })

  it('shows an error message when stats fail to load', async () => {
    const user = userEvent.setup()
    mockUser({ email: 'alice@example.com', name: 'Alice' })
    vi.mocked(linkApi.getLinks).mockResolvedValue(
      page({
        items: [{ shortenLink: 'abc12345', originalLink: 'https://example.com/a', createdAt: '2099-01-01T00:00:00Z', clickCount: 0 }],
        totalCount: 1,
      }),
    )
    vi.mocked(statsApi.getClickSummary).mockRejectedValue(new ApiError(500, 'boom'))

    render(<DashboardPage />)
    await user.click(await screen.findByRole('button', { name: 'Stats' }))

    expect(await screen.findByText('Could not load stats for this link.')).toBeInTheDocument()
  })

  it('retries the stats fetch after a previous failure, unlike a previous success', async () => {
    const user = userEvent.setup()
    mockUser({ email: 'alice@example.com', name: 'Alice' })
    vi.mocked(linkApi.getLinks).mockResolvedValue(
      page({
        items: [{ shortenLink: 'abc12345', originalLink: 'https://example.com/a', createdAt: '2099-01-01T00:00:00Z', clickCount: 1 }],
        totalCount: 1,
      }),
    )
    vi.mocked(statsApi.getClickSummary)
      .mockRejectedValueOnce(new ApiError(500, 'boom'))
      .mockResolvedValueOnce({
        hash: 'abc12345',
        totalClicks: 1,
        byDay: [{ key: '2099-01-01', count: 1 }],
        byCountry: [],
        byDevice: [],
        byBrowser: [],
      })

    render(<DashboardPage />)
    await user.click(await screen.findByRole('button', { name: 'Stats' }))
    await screen.findByText('Could not load stats for this link.')

    // A failed fetch must NOT lock the panel out of retrying, unlike a successful one (see the
    // 'fetches the summary once' test above) - the backend can recover between attempts.
    await user.click(screen.getByRole('button', { name: 'Hide stats' }))
    await user.click(screen.getByRole('button', { name: 'Stats' }))

    expect(await screen.findByText('Total clicks')).toBeInTheDocument()
    expect(screen.queryByText('Could not load stats for this link.')).not.toBeInTheDocument()
    expect(statsApi.getClickSummary).toHaveBeenCalledTimes(2)
  })

  it('does not update state after unmounting while a stats fetch is in flight', async () => {
    const user = userEvent.setup()
    mockUser({ email: 'alice@example.com', name: 'Alice' })
    vi.mocked(linkApi.getLinks).mockResolvedValue(
      page({
        items: [{ shortenLink: 'abc12345', originalLink: 'https://example.com/a', createdAt: '2099-01-01T00:00:00Z', clickCount: 1 }],
        totalCount: 1,
      }),
    )
    let resolveSummary: (() => void) | undefined
    vi.mocked(statsApi.getClickSummary).mockReturnValue(
      new Promise((resolve) => {
        resolveSummary = () =>
          resolve({ hash: 'abc12345', totalClicks: 1, byDay: [], byCountry: [], byDevice: [], byBrowser: [] })
      }),
    )
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})

    const { unmount } = render(<DashboardPage />)
    await user.click(await screen.findByRole('button', { name: 'Stats' }))
    unmount()
    resolveSummary?.()
    await new Promise((resolve) => setTimeout(resolve, 0))

    // No "state update on an unmounted component" warning - mountedRef guards the .then callback.
    expect(consoleError).not.toHaveBeenCalled()
    consoleError.mockRestore()
  })
})
