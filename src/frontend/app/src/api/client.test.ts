import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiFetch, ApiError, getAccessToken, setAccessToken } from './client'

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

describe('ApiError', () => {
  it('carries the status code and message', () => {
    const error = new ApiError(404, 'not found')

    expect(error.status).toBe(404)
    expect(error.message).toBe('not found')
    expect(error.name).toBe('ApiError')
  })
})
