// Thin fetch wrapper shared by authApi/linkApi: builds the request, attaches the JWT (if any),
// parses JSON, and turns a non-2xx response into a thrown ApiError with the backend's message.

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

// In-memory only, deliberately not persisted to localStorage/sessionStorage: either would be
// readable by any script on the page, so an XSS bug could exfiltrate a long-lived access token.
// A hard page reload starts with no token here; AuthProvider restores the session by exchanging
// the httpOnly refresh-token cookie for a new one via POST /refresh.
let accessToken: string | null = null

export function getAccessToken(): string | null {
  return accessToken
}

export function setAccessToken(token: string | null): void {
  accessToken = token
}

// Set once by AuthProvider on mount (see its own useEffect) - this file can't import authApi.ts
// directly (authApi.ts already imports apiFetch from here, so that would be circular), so the
// actual POST /refresh + applyToken logic is injected from the auth layer instead.
let refreshHandler: (() => Promise<string>) | null = null

export function setRefreshTokenHandler(handler: (() => Promise<string>) | null): void {
  refreshHandler = handler
}

// Dedupes concurrent 401s into a single POST /refresh instead of one per failed request - found
// in review: without this, N requests failing at once (e.g. a batch of parallel calls right as the
// access token expires) would each independently race to refresh, and the refresh-token rotation's
// own reuse detection (see AuthContext's session-restore comment) would treat every call after the
// first as a replay and revoke the session, logging the user out from what should have been a
// transparent refresh.
let refreshPromise: Promise<string> | null = null

// Exported so AuthProvider's own proactive, scheduled refresh (see its exp-based timer) can share
// this exact in-flight promise too, not just apiFetch's reactive 401 path below - otherwise the
// two could still independently race each other into two concurrent POST /refresh calls right as
// a token's exp passes under load, the same replay-detection risk this dedup exists to prevent.
export function requestTokenRefresh(): Promise<string> {
  if (!refreshHandler) {
    return Promise.reject(new Error('No refresh handler registered'))
  }

  refreshPromise ??= refreshHandler().finally(() => {
    refreshPromise = null
  })
  return refreshPromise
}

type RequestOptions = {
  method?: 'GET' | 'POST' | 'DELETE'
  body?: unknown
  auth?: boolean
  // Sends/receives cookies cross-origin (the httpOnly refresh-token cookie AuthApi issues).
  // Opt-in per call rather than always-on, since it requires the target's CORS policy to allow
  // credentials, which only AuthApi's does.
  credentials?: boolean
  // Extra headers merged in after the built-in Content-Type/Authorization ones above - e.g.
  // Idempotency-Key (see linkApi.createLink). Not validated or capped here; that's each endpoint's
  // own concern (LinksController caps Idempotency-Key at 256 chars server-side).
  headers?: Record<string, string>
}

export function apiFetch<TResponse>(
  baseUrl: string,
  path: string,
  options: RequestOptions = {},
): Promise<TResponse> {
  // retryOn401 is only ever false on the one recursive call below (after a refresh already ran) -
  // never exposed on RequestOptions itself, so every real caller gets the retry behavior with no
  // opt-in needed.
  return apiFetchInternal(baseUrl, path, options, /* retryOn401 */ true)
}

async function apiFetchInternal<TResponse>(
  baseUrl: string,
  path: string,
  { method = 'GET', body, auth = false, credentials = false, headers: extraHeaders }: RequestOptions,
  retryOn401: boolean,
): Promise<TResponse> {
  const headers: Record<string, string> = {}
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }
  if (auth) {
    const token = getAccessToken()
    if (token) {
      headers['Authorization'] = `Bearer ${token}`
    }
  }
  if (extraHeaders) {
    Object.assign(headers, extraHeaders)
  }

  const response = await fetch(`${baseUrl}${path}`, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    credentials: credentials ? 'include' : 'same-origin',
  })

  if (!response.ok) {
    // Only for calls that actually attached a Bearer token - login/register/refresh themselves
    // never pass auth: true, so a 401 from refresh (no valid refresh cookie at all) correctly
    // falls straight through to the throw below instead of looping back into itself.
    if (response.status === 401 && auth && retryOn401) {
      try {
        await requestTokenRefresh()
        return apiFetchInternal<TResponse>(baseUrl, path, { method, body, auth, credentials, headers: extraHeaders }, false)
      } catch {
        // Refresh itself failed (no valid cookie, or it expired/was revoked) - fall through and
        // report the original request's own 401, same as before this retry existed.
      }
    }

    throw new ApiError(response.status, await extractErrorMessage(response))
  }

  if (response.status === 204) {
    return undefined as TResponse
  }

  return (await response.json()) as TResponse
}

// [ApiController]'s default content negotiation serializes even a plain-string Conflict("...")/
// Unauthorized("...") body as JSON (a quoted string), and RFC 7807 ProblemDetails (Problem(...))
// wraps the human-readable message in a `detail` field alongside type/title/status/instance that
// aren't meant for a user to see. Either way the raw response body is not something to render
// as-is - this unwraps both shapes down to a plain message, falling back to the raw text (then
// statusText) for anything else, e.g. a proxy error page or an empty body.
async function extractErrorMessage(response: Response): Promise<string> {
  const text = await response.text().catch(() => '')
  if (!text) {
    return response.statusText
  }

  try {
    const parsed: unknown = JSON.parse(text)
    if (typeof parsed === 'string') {
      return parsed
    }
    if (parsed && typeof parsed === 'object') {
      const problem = parsed as { detail?: unknown; title?: unknown }
      if (typeof problem.detail === 'string' && problem.detail) {
        return problem.detail
      }
      if (typeof problem.title === 'string' && problem.title) {
        return problem.title
      }
    }
  } catch {
    // Not JSON - fall through to the raw text below.
  }

  return text
}
