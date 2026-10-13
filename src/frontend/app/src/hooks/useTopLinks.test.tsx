import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as topLinksApi from '../api/topLinksApi'
import type { TopLinkEntry } from '../types'
import { useTopLinks } from './useTopLinks'

vi.mock('../api/topLinksApi', () => ({
  createTopLinksConnection: vi.fn(),
}))

type Handler = (...args: never[]) => void

// A stand-in for HubConnection that records handlers, so a test can fire the server's messages
// and lifecycle events itself.
function fakeConnection(startResult: Promise<void> = Promise.resolve()) {
  const handlers: Record<string, Handler> = {}
  return {
    handlers,
    on: vi.fn((name: string, handler: Handler) => {
      handlers[name] = handler
    }),
    onreconnecting: vi.fn((handler: Handler) => {
      handlers.reconnecting = handler
    }),
    onreconnected: vi.fn((handler: Handler) => {
      handlers.reconnected = handler
    }),
    onclose: vi.fn((handler: Handler) => {
      handlers.close = handler
    }),
    start: vi.fn(() => startResult),
    stop: vi.fn(() => Promise.resolve()),
  }
}

describe('useTopLinks', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('does not open a connection while disabled', () => {
    renderHook(() => useTopLinks(false))

    expect(topLinksApi.createTopLinksConnection).not.toHaveBeenCalled()
  })

  it('becomes live once the connection starts', async () => {
    const connection = fakeConnection()
    vi.mocked(topLinksApi.createTopLinksConnection).mockReturnValue(connection as never)

    const { result } = renderHook(() => useTopLinks(true))

    await waitFor(() => expect(result.current.status).toBe('live'))
  })

  it('stores the latest list the server pushes, replacing the previous one', async () => {
    const connection = fakeConnection()
    vi.mocked(topLinksApi.createTopLinksConnection).mockReturnValue(connection as never)
    const { result } = renderHook(() => useTopLinks(true))
    await waitFor(() => expect(result.current.status).toBe('live'))

    const first: TopLinkEntry[] = [{ hash: 'aaa11111', clicks: 5 }]
    const second: TopLinkEntry[] = [
      { hash: 'bbb22222', clicks: 9 },
      { hash: 'aaa11111', clicks: 6 },
    ]
    const push = connection.handlers.topLinks as unknown as (items: TopLinkEntry[]) => void

    act(() => push(first))
    expect(result.current.items).toEqual(first)

    act(() => push(second))
    expect(result.current.items).toEqual(second)
  })

  it('reports reconnecting and back to live across a dropped connection', async () => {
    const connection = fakeConnection()
    vi.mocked(topLinksApi.createTopLinksConnection).mockReturnValue(connection as never)
    const { result } = renderHook(() => useTopLinks(true))
    await waitFor(() => expect(result.current.status).toBe('live'))

    act(() => (connection.handlers.reconnecting as unknown as () => void)())
    expect(result.current.status).toBe('reconnecting')

    act(() => (connection.handlers.reconnected as unknown as () => void)())
    expect(result.current.status).toBe('live')
  })

  it('reports offline when the initial connection fails', async () => {
    const connection = fakeConnection(Promise.reject(new Error('hub unreachable')))
    vi.mocked(topLinksApi.createTopLinksConnection).mockReturnValue(connection as never)

    const { result } = renderHook(() => useTopLinks(true))

    await waitFor(() => expect(result.current.status).toBe('offline'))
  })

  it('stops the connection on unmount', async () => {
    const connection = fakeConnection()
    vi.mocked(topLinksApi.createTopLinksConnection).mockReturnValue(connection as never)
    const { result, unmount } = renderHook(() => useTopLinks(true))
    await waitFor(() => expect(result.current.status).toBe('live'))

    unmount()

    expect(connection.stop).toHaveBeenCalledTimes(1)
  })
})
