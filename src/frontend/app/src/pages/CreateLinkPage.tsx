import { useEffect, useRef, useState, type FormEvent } from 'react'
import { buildShortUrl, createLink } from '../api/linkApi'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import type { LinkResponse } from '../types'

export default function CreateLinkPage() {
  const { user } = useAuth()
  const [originalLink, setOriginalLink] = useState('')
  const [alias, setAlias] = useState('')
  const [result, setResult] = useState<LinkResponse | null>(null)
  // A custom alias is created synchronously - by the time the response comes back, the row is
  // already durably persisted, unlike the plain-hash path. Tracked separately from `result` so the
  // "it can take a moment" hint below only shows when it's actually true.
  const [resultWasAlias, setResultWasAlias] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [copied, setCopied] = useState(false)
  const copiedTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Without this, a copy right before navigating away leaves the timeout armed - it still fires
  // 2s later and calls setCopied on an unmounted component (a React "state update on an unmounted
  // component" warning; a real leak if this page ever grows more such one-shot timers).
  useEffect(() => {
    return () => {
      if (copiedTimeoutRef.current !== null) {
        clearTimeout(copiedTimeoutRef.current)
      }
    }
  }, [])

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setResult(null)
    setSubmitting(true)
    // Only a signed-in user's alias is ever sent - an anonymous caller's field is disabled (see
    // below) and can't hold a value, but guard here too rather than trusting that alone, since
    // this reads component state, not the DOM.
    const usedAlias = user ? alias.trim() : ''
    try {
      const response = await createLink(originalLink, usedAlias || undefined)
      setResult(response)
      setResultWasAlias(usedAlias !== '')
      setOriginalLink('')
      setAlias('')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not shorten that link. Please try again.')
    } finally {
      setSubmitting(false)
    }
  }

  async function handleCopy(url: string) {
    try {
      await navigator.clipboard.writeText(url)
      setCopied(true)
      if (copiedTimeoutRef.current !== null) {
        clearTimeout(copiedTimeoutRef.current)
      }
      copiedTimeoutRef.current = setTimeout(() => setCopied(false), 2000)
    } catch {
      // Clipboard API can be unavailable (permissions, non-secure context) — not worth surfacing.
    }
  }

  return (
    <div className="create-link-page">
      <form className="card" onSubmit={handleSubmit}>
        <h1>Shorten a link</h1>
        <label>
          URL to shorten
          <input
            type="url"
            placeholder="https://example.com/a/very/long/path"
            value={originalLink}
            onChange={(e) => setOriginalLink(e.target.value)}
            required
          />
        </label>
        <label>
          Custom alias (optional)
          <input
            type="text"
            placeholder="my-cool-link"
            value={alias}
            onChange={(e) => setAlias(e.target.value)}
            disabled={!user}
            minLength={3}
            maxLength={32}
            pattern="[a-zA-Z0-9][a-zA-Z0-9_-]{2,31}"
            title="3-32 characters, letters/digits/dashes/underscores, starting with a letter or digit"
          />
        </label>
        {!user && <p className="hint">Sign in to pick your own short link instead of a random one.</p>}
        {error && <p className="error">{error}</p>}
        <button type="submit" disabled={submitting}>
          {submitting ? 'Shortening…' : 'Shorten'}
        </button>
      </form>

      {result && (
        <div className="card result">
          {resultWasAlias && <span className="alias-badge">Custom alias</span>}
          <a href={buildShortUrl(result.shortenLink)} target="_blank" rel="noreferrer">
            {buildShortUrl(result.shortenLink)}
          </a>
          <button type="button" onClick={() => handleCopy(buildShortUrl(result.shortenLink))}>
            {copied ? 'Copied!' : 'Copy'}
          </button>
          {resultWasAlias ? (
            // A custom alias is created synchronously (see CreateLinkPage's own comment on
            // resultWasAlias) - by the time this renders, the row is already durably persisted, so
            // there's no "wait a moment" caveat to give here, unlike the plain-hash path below.
            <p className="hint">Ready to use right away.</p>
          ) : (
            // Creation publishes an event and returns immediately; ShortenerService persists it
            // asynchronously. In the rare case you click within that window, the redirect may 404 —
            // retrying a moment later will work. This app doesn't hide that; it's the point of the demo.
            <p className="hint">
              It can take a brief moment to become active — the link is created asynchronously.
            </p>
          )}
        </div>
      )}
    </div>
  )
}
