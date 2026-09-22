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

type RequestOptions = {
  method?: 'GET' | 'POST'
  body?: unknown
  auth?: boolean
  // Sends/receives cookies cross-origin (the httpOnly refresh-token cookie AuthApi issues).
  // Opt-in per call rather than always-on, since it requires the target's CORS policy to allow
  // credentials, which only AuthApi's does.
  credentials?: boolean
}

export async function apiFetch<TResponse>(
  baseUrl: string,
  path: string,
  { method = 'GET', body, auth = false, credentials = false }: RequestOptions = {},
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

  const response = await fetch(`${baseUrl}${path}`, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    credentials: credentials ? 'include' : 'same-origin',
  })

  if (!response.ok) {
    const message = await response.text().catch(() => '')
    throw new ApiError(response.status, message || response.statusText)
  }

  if (response.status === 204) {
    return undefined as TResponse
  }

  return (await response.json()) as TResponse
}
