import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createLink } from './linkApi'
import { setAccessToken } from './client'

describe('createLink', () => {
  beforeEach(() => {
    setAccessToken('test-token')
    vi.stubGlobal('fetch', vi.fn())
  })

  afterEach(() => {
    setAccessToken(null)
    vi.unstubAllGlobals()
  })

  it('sends a fresh Idempotency-Key header', async () => {
    // Found in review: the backend has supported this header since the Resilience & Scale-Out
    // Backlog's item 4, but nothing in the frontend ever sent it - a double-click or a browser-level
    // retry on a slow network could still create two links for one user action.
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ hash: 'abc123', originalLink: 'https://example.com', shortUrl: '' }), { status: 201 }),
    )

    await createLink('https://example.com')

    const [, init] = vi.mocked(fetch).mock.calls[0]
    const headers = init!.headers as Record<string, string>
    expect(headers['Idempotency-Key']).toBeDefined()
    expect(headers['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('uses a different key for two separate createLink calls', async () => {
    vi.mocked(fetch).mockImplementation(
      async () =>
        new Response(JSON.stringify({ hash: 'abc123', originalLink: 'https://example.com', shortUrl: '' }), { status: 201 }),
    )

    await createLink('https://example.com')
    await createLink('https://example.org')

    const [, firstInit] = vi.mocked(fetch).mock.calls[0]
    const [, secondInit] = vi.mocked(fetch).mock.calls[1]
    const firstKey = (firstInit!.headers as Record<string, string>)['Idempotency-Key']
    const secondKey = (secondInit!.headers as Record<string, string>)['Idempotency-Key']
    expect(firstKey).not.toBe(secondKey)
  })
})
