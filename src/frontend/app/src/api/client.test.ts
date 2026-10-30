import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiFetch, ApiError, getAccessToken, setAccessToken, setRefreshTokenHandler } from './client'

describe('access token storage', () => {
  beforeEach(() => {
    setAccessToken(null)
  })

  it('getAccessToken returns null when nothing is set', () => {
    expect(getAccessToken()).toBeNull()
  })

  it('setAccessToken then getAccessToken round-trips the value', () => {
    setAccessToken('my-token')

    expect(getAccessToken()).toBe('my-token')
  })

  it('setAccessToken(null) clears a previously set token', () => {
    setAccessToken('my-token')

    setAccessToken(null)

    expect(getAccessToken()).toBeNull()
  })
})

describe('apiFetch', () => {
  beforeEach(() => {
    setAccessToken(null)
    vi.stubGlobal('fetch', vi.fn())
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('sends a GET request to baseUrl + path and returns the parsed JSON body', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ hello: 'world' }), { status: 200 }),
    )

    const result = await apiFetch<{ hello: string }>('https://api.example.com', '/things')

    expect(fetch).toHaveBeenCalledWith(
      'https://api.example.com/things',
      expect.objectContaining({ method: 'GET' }),
    )
    expect(result).toEqual({ hello: 'world' })
  })

  it('serializes the body and sets Content-Type for POST requests', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ ok: true }), { status: 200 }))

    await apiFetch('https://api.example.com', '/things', { method: 'POST', body: { a: 1 } })

    const [, init] = vi.mocked(fetch).mock.calls[0]
    expect(init?.method).toBe('POST')
    expect(init?.body).toBe(JSON.stringify({ a: 1 }))
    const headers = init!.headers as Record<string, string>
    expect(headers['Content-Type']).toBe('application/json')
  })

  it('attaches a Bearer token when auth is true and a token is set', async () => {
    setAccessToken('secret-token')
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({}), { status: 200 }))

    await apiFetch('https://api.example.com', '/things', { auth: true })

    const [, init] = vi.mocked(fetch).mock.calls[0]
    const headers = init!.headers as Record<string, string>
    expect(headers['Authorization']).toBe('Bearer secret-token')
  })

  it('does not attach an Authorization header when auth is true but no token is set', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({}), { status: 200 }))

    await apiFetch('https://api.example.com', '/things', { auth: true })

    const [, init] = vi.mocked(fetch).mock.calls[0]
    const headers = init!.headers as Record<string, string>
    expect(headers['Authorization']).toBeUndefined()
  })

  it('throws ApiError with the response body text on a non-2xx response', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response('Invalid email or password.', { status: 401 }))

    await expect(apiFetch('https://api.example.com', '/login')).rejects.toMatchObject({
      status: 401,
      message: 'Invalid email or password.',
    })
  })

  it('unwraps a JSON-quoted-string error body (a bare Conflict("...")/Unauthorized("...") result)', async () => {
    // [ApiController]'s default content negotiation serializes a plain-string ObjectResult as a
    // JSON string, so the wire body is `"Invalid email or password."` (with the quotes) - rendering
    // that verbatim would show the quote characters to the user.
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify('Invalid email or password.'), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    await expect(apiFetch('https://api.example.com', '/login')).rejects.toMatchObject({
      status: 401,
      message: 'Invalid email or password.',
    })
  })

  it('extracts detail from an RFC 7807 ProblemDetails error body', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(
        JSON.stringify({
          type: 'https://tools.ietf.org/html/rfc7231#section-6.5.8',
          title: 'Email already registered.',
          status: 409,
          detail: 'A user with this email already exists.',
        }),
        { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )

    await expect(apiFetch('https://api.example.com', '/register')).rejects.toMatchObject({
      status: 409,
      message: 'A user with this email already exists.',
    })
  })

  it('falls back to title when a ProblemDetails body has no detail', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ title: 'Authentication failed.', status: 401 }), { status: 401 }),
    )

    await expect(apiFetch('https://api.example.com', '/login')).rejects.toMatchObject({
      status: 401,
      message: 'Authentication failed.',
    })
  })

  it('falls back to statusText when the error body is empty', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response('', { status: 500, statusText: 'Server Error' }))

    await expect(apiFetch('https://api.example.com', '/oops')).rejects.toMatchObject({
      status: 500,
      message: 'Server Error',
    })
  })

  it('returns undefined for a 204 No Content response', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 204 }))

    const result = await apiFetch<undefined>('https://api.example.com', '/things')

    expect(result).toBeUndefined()
  })
})

describe('apiFetch - 401 refresh-and-retry', () => {
  beforeEach(() => {
    setAccessToken(null)
    setRefreshTokenHandler(null)
    vi.stubGlobal('fetch', vi.fn())
  })

  afterEach(() => {
    setRefreshTokenHandler(null)
    vi.unstubAllGlobals()
  })

  it('refreshes once and retries with the new token on a 401 from an authenticated call', async () => {
    setAccessToken('stale-token')
    const refreshHandler = vi.fn().mockImplementation(async () => {
      setAccessToken('fresh-token')
      return 'fresh-token'
    })
    setRefreshTokenHandler(refreshHandler)
    vi.mocked(fetch)
      .mockResolvedValueOnce(new Response('Unauthorized', { status: 401 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ ok: true }), { status: 200 }))

    const result = await apiFetch<{ ok: boolean }>('https://api.example.com', '/things', { auth: true })

    expect(result).toEqual({ ok: true })
    expect(refreshHandler).toHaveBeenCalledOnce()
    expect(fetch).toHaveBeenCalledTimes(2)
    const [, secondInit] = vi.mocked(fetch).mock.calls[1]
    const headers = secondInit!.headers as Record<string, string>
    expect(headers['Authorization']).toBe('Bearer fresh-token')
  })

  it('does not retry a 401 from a call that never attached a Bearer token (e.g. refresh itself)', async () => {
    const refreshHandler = vi.fn()
    setRefreshTokenHandler(refreshHandler)
    vi.mocked(fetch).mockResolvedValue(new Response('Unauthorized', { status: 401 }))

    await expect(apiFetch('https://api.example.com', '/refresh', { method: 'POST' })).rejects.toMatchObject({
      status: 401,
    })
    expect(refreshHandler).not.toHaveBeenCalled()
    expect(fetch).toHaveBeenCalledOnce()
  })

  it('throws the original 401 (not the refresh failure) when the refresh itself fails', async () => {
    setAccessToken('stale-token')
    setRefreshTokenHandler(vi.fn().mockRejectedValue(new Error('refresh token expired')))
    vi.mocked(fetch).mockResolvedValue(new Response('Unauthorized', { status: 401 }))

    await expect(apiFetch('https://api.example.com', '/things', { auth: true })).rejects.toMatchObject({
      status: 401,
      message: 'Unauthorized',
    })
    expect(fetch).toHaveBeenCalledOnce()
  })

  it('dedupes two concurrent 401s into a single refresh call', async () => {
    // Regression this dedup exists to prevent: two requests failing at once would otherwise each
    // independently call POST /refresh, and the refresh-token rotation's own reuse detection would
    // revoke the session on the second call, logging the user out of what should be transparent.
    setAccessToken('stale-token')
    let resolveRefresh!: (token: string) => void
    const refreshHandler = vi.fn(
      () =>
        new Promise<string>((resolve) => {
          resolveRefresh = resolve
        }),
    )
    setRefreshTokenHandler(refreshHandler)
    vi.mocked(fetch).mockImplementation(async (_url, init) => {
      const headers = init?.headers as Record<string, string> | undefined
      return headers?.['Authorization'] === 'Bearer fresh-token'
        ? new Response(JSON.stringify({ ok: true }), { status: 200 })
        : new Response('Unauthorized', { status: 401 })
    })

    const first = apiFetch('https://api.example.com', '/a', { auth: true })
    const second = apiFetch('https://api.example.com', '/b', { auth: true })
    await Promise.resolve() // let both requests reach their first (401) fetch call
    setAccessToken('fresh-token')
    resolveRefresh('fresh-token')

    await Promise.all([first, second])
    expect(refreshHandler).toHaveBeenCalledOnce()
  })
})

describe('ApiError', () => {
  it('carries the status code and message', () => {
    const error = new ApiError(404, 'not found')

    expect(error.status).toBe(404)
    expect(error.message).toBe('not found')
    expect(error.name).toBe('ApiError')
  })
})
