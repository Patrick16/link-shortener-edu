import { apiFetch } from './client'
import type { LinkCreateRequest, LinkResponse, LinksPage } from '../types'

const LINK_API_URL = import.meta.env.VITE_LINK_API_URL as string
const REDIRECT_API_URL = import.meta.env.VITE_REDIRECT_API_URL as string

// The hash resolves through RedirectApi, not LinkApi — this is the URL a user actually visits.
export function buildShortUrl(hash: string): string {
  return `${REDIRECT_API_URL}/${hash}`
}

export function createLink(originalLink: string): Promise<LinkResponse> {
  const request: LinkCreateRequest = { originalLink }
  return apiFetch<LinkResponse>(LINK_API_URL, '/links', { method: 'POST', body: request, auth: true })
}

export function getLink(hash: string): Promise<LinkResponse> {
  return apiFetch<LinkResponse>(LINK_API_URL, `/links/${encodeURIComponent(hash)}`)
}

// Requires auth: the backend rejects anonymous callers with 401, since there's no "your links"
// to list without knowing who you are.
export function getLinks(page = 1): Promise<LinksPage> {
  return apiFetch<LinksPage>(LINK_API_URL, `/links?page=${page}`, { auth: true })
}
