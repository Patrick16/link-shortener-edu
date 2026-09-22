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
}

export type LinksPage = {
  items: LinkListItem[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}
