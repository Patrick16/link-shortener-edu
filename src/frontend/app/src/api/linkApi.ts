import { apiFetch } from './client'
import type { LinkCreateRequest, LinkResponse } from '../types'

const LINK_API_URL = import.meta.env.VITE_LINK_API_URL as string
const REDIRECT_API_URL = import.meta.env.VITE_REDIRECT_API_URL as string

// The hash resolves through RedirectApi, not LinkApi — this is the URL a user actually visits.
export function buildShortUrl(hash: string): string {
  return `${REDIRECT_API_URL}/${hash}`
}

export function createLink(originalLink: string): Promise<LinkResponse> {
  const request: LinkCreateRequest = { originalLink }
  return apiFetch<LinkResponse>(LINK_API_URL, '/links', { method: 'POST', body: request })
}

export function getLink(hash: string): Promise<LinkResponse> {
  return apiFetch<LinkResponse>(LINK_API_URL, `/links/${encodeURIComponent(hash)}`)
}
