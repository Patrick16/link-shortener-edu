import { apiFetch } from './client'
import type { LinkCreateRequest, LinkResponse, LinksPage } from '../types'

const LINK_API_URL = import.meta.env.VITE_LINK_API_URL as string
const REDIRECT_API_URL = import.meta.env.VITE_REDIRECT_API_URL as string

// The hash resolves through RedirectApi, not LinkApi — this is the URL a user actually visits.
export function buildShortUrl(hash: string): string {
  return `${REDIRECT_API_URL}/${hash}`
}

// alias is only honored by the backend for an authenticated caller (401 otherwise) - auth: true is
// unconditional here regardless, same as before aliases existed: a present-but-expired/absent token
// just means no Authorization header goes out (see apiFetch), so an anonymous plain-hash request is
// unaffected. ttlSeconds is counted by the caller (this app), not a target timestamp - see
// CreateLinkPage's TTL select for where the seconds actually come from.
export function createLink(originalLink: string, alias?: string, ttlSeconds?: number): Promise<LinkResponse> {
  const request: LinkCreateRequest = { originalLink, ...(alias ? { alias } : {}), ...(ttlSeconds ? { ttlSeconds } : {}) }
  return apiFetch<LinkResponse>(LINK_API_URL, '/links', { method: 'POST', body: request, auth: true })
}

// Requires auth: the backend rejects anonymous callers with 401, since there's no "your links"
// to list without knowing who you are. `deleted` switches to the disjoint "what's gone" view
// (DashboardPage's second tab) - see LinksController.GetLinks's own comment for why it's a
// boolean toggle, not a third "show everything" mode.
export function getLinks(page = 1, deleted = false): Promise<LinksPage> {
  return apiFetch<LinksPage>(LINK_API_URL, `/links?page=${page}&deleted=${deleted}`, { auth: true })
}

// Owner-only on the backend (403 otherwise) - DashboardPage only ever calls this for the signed-in
// user's own links, but the backend check is what actually matters.
export function deleteLink(hash: string): Promise<void> {
  return apiFetch<void>(LINK_API_URL, `/links/${hash}`, { method: 'DELETE', auth: true })
}
