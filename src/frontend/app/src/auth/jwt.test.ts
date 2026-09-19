import { describe, expect, it } from 'vitest'
import { decodeJwt } from './jwt'

function encodeSegment(value: unknown): string {
  const json = JSON.stringify(value)
  return btoa(json).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function fakeJwt(payload: Record<string, unknown>): string {
  const header = encodeSegment({ alg: 'HS256', typ: 'JWT' })
  const body = encodeSegment(payload)
  return `${header}.${body}.fake-signature`
}

describe('decodeJwt', () => {
  it('decodes the claims from a well-formed token', () => {
    const token = fakeJwt({ sub: 'user-1', email: 'alice@example.com', name: 'Alice', exp: 123 })

    const claims = decodeJwt(token)

    expect(claims).toEqual({ sub: 'user-1', email: 'alice@example.com', name: 'Alice', exp: 123 })
  })

  it('handles base64url payloads that need padding', () => {
    // Deliberately pick a payload whose base64 length isn't a multiple of 4, to exercise the
    // padEnd path.
    const token = fakeJwt({ sub: 'x', email: 'a@b.co', name: 'A', exp: 1 })

    expect(decodeJwt(token)).not.toBeNull()
  })

  it('returns null for a malformed token', () => {
    expect(decodeJwt('not-a-jwt')).toBeNull()
  })

  it('returns null for an empty string', () => {
    expect(decodeJwt('')).toBeNull()
  })

  it('returns null when the payload segment is not valid base64', () => {
    expect(decodeJwt('header.***.signature')).toBeNull()
  })

  it('returns null when the payload segment is not valid JSON', () => {
    const notJson = btoa('this is not json')
    expect(decodeJwt(`header.${notJson}.signature`)).toBeNull()
  })
})
