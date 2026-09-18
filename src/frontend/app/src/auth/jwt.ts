// Just enough JWT decoding to read the claims AuthApi puts in the token for display purposes
// (email, name) — not a dependency, this app never needs to verify the signature client-side.

type JwtClaims = {
  sub: string
  email: string
  name: string
  exp: number
}

export function decodeJwt(token: string): JwtClaims | null {
  try {
    const payload = token.split('.')[1]
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/')
    const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=')
    return JSON.parse(atob(padded)) as JwtClaims
  } catch {
    return null
  }
}
