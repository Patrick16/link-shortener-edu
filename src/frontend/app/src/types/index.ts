// Mirrors AuthApi/LinkApi request/response shapes (see src/backend/Services/AuthApi/Models,
// src/backend/Services/LinkApi/Models). Keep these in sync by hand for now — no shared schema yet.

export type RegisterRequest = {
  name: string
  email: string
  password: string
}

export type LoginRequest = {
  email: string
  password: string
}

export type AuthResponse = {
  token: string
  expiresAt: string
}

export type LinkCreateRequest = {
  originalLink: string
  alias?: string
  ttlSeconds?: number
}

export type LinkResponse = {
  shortenLink: string
  createdAt: string
}

export type LinkListItem = {
  shortenLink: string
  originalLink: string
  createdAt: string
  clickCount: number
  hasCustomAlias: boolean
  expiresAt: string | null
  deletedAt: string | null
}

export type LinksPage = {
  items: LinkListItem[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

// Mirrors ReportingApi's ClickSummary/CountBucket (src/backend/Shared/Common/Models/ClickSummary.cs).
export type CountBucket = {
  key: string
  count: number
}

export type ClickSummary = {
  hash: string
  totalClicks: number
  byDay: CountBucket[]
  byCountry: CountBucket[]
  byDevice: CountBucket[]
  byBrowser: CountBucket[]
}

// Mirrors ReportingApi's TopLinkEntry (src/backend/Shared/Infrastructure/TopLinks.cs). The hash is the
// short link's code; alias/original-URL display comes later with link aliases.
export type TopLinkEntry = {
  hash: string
  clicks: number
}

// Mirrors ReportingApi's ClickDetail (src/backend/Shared/Common/Models/ClickDetail.cs) - raw
// per-click rows read from Mongo, the flexible-document counterpart to ClickSummary's pre-
// aggregated ClickHouse buckets. No ipAddress field - the backend deliberately withholds it.
export type ClickDetail = {
  id: string
  clickedAt: string
  userAgent: string
  referrer: string
  browser: string | null
  os: string | null
  deviceType: string | null
  country: string | null
  city: string | null
}
