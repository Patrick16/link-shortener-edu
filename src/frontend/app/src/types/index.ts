// Mirrors AuthApi/LinkApi request/response shapes (see src/Services/AuthApi/Models,
// src/Services/LinkApi/Models). Keep these in sync by hand for now — no shared schema yet.

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
