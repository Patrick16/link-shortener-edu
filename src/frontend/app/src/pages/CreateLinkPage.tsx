import { useState, type FormEvent } from 'react'
import { buildShortUrl, createLink } from '../api/linkApi'
import { ApiError } from '../api/client'
import type { LinkResponse } from '../types'

export default function CreateLinkPage() {
  const [originalLink, setOriginalLink] = useState('')
  const [result, setResult] = useState<LinkResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [copied, setCopied] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setResult(null)
    setSubmitting(true)
    try {
      const response = await createLink(originalLink)
      setResult(response)
      setOriginalLink('')
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
      setTimeout(() => setCopied(false), 2000)
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
        {error && <p className="error">{error}</p>}
        <button type="submit" disabled={submitting}>
          {submitting ? 'Shortening…' : 'Shorten'}
        </button>
      </form>

      {result && (
        <div className="card result">
          <a href={buildShortUrl(result.shortenLink)} target="_blank" rel="noreferrer">
            {buildShortUrl(result.shortenLink)}
          </a>
          <button type="button" onClick={() => handleCopy(buildShortUrl(result.shortenLink))}>
            {copied ? 'Copied!' : 'Copy'}
          </button>
          {/* Creation publishes an event and returns immediately; ShortenerService persists it
              asynchronously. In the rare case you click within that window, the redirect may 404 —
              retrying a moment later will work. This app doesn't hide that; it's the point of the demo. */}
          <p className="hint">
            It can take a brief moment to become active — the link is created asynchronously.
          </p>
        </div>
      )}
    </div>
  )
}
