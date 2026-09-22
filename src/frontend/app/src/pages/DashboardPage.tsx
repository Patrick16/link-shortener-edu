import { useEffect, useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import { buildShortUrl, getLinks } from '../api/linkApi'
import { ApiError } from '../api/client'
import type { LinksPage } from '../types'

export default function DashboardPage() {
  const { user } = useAuth()
  const [page, setPage] = useState(1)
  const [data, setData] = useState<LinksPage | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!user) return

    let cancelled = false
    getLinks(page)
      .then((response) => {
        if (cancelled) return
        setData(response)
        setError(null)
      })
      .catch((err) => {
        if (cancelled) return
        setError(err instanceof ApiError ? err.message : 'Could not load your links. Please try again.')
      })

    return () => {
      cancelled = true
    }
  }, [user, page])

  if (!user) return null

  const loading = !data && !error

  return (
    <div className="card dashboard">
      <h2>My links</h2>

      {loading && <p className="hint">Loading…</p>}
      {error && <p className="error">{error}</p>}

      {data && data.items.length === 0 && <p className="hint">You haven't created any links yet.</p>}

      {data && data.items.length > 0 && (
        <>
          <ul className="link-list">
            {data.items.map((item) => (
              <li key={item.shortenLink} className="link-list-item">
                <a href={buildShortUrl(item.shortenLink)} target="_blank" rel="noreferrer">
                  {buildShortUrl(item.shortenLink)}
                </a>
                <span className="link-list-original">{item.originalLink}</span>
                <span className="link-list-clicks">
                  {item.clickCount} {item.clickCount === 1 ? 'click' : 'clicks'}
                </span>
              </li>
            ))}
          </ul>

          {data.totalPages > 1 && (
            <div className="pagination">
              <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
                Previous
              </button>
              <span>
                Page {data.page} of {data.totalPages}
              </span>
              <button type="button" disabled={page >= data.totalPages} onClick={() => setPage((p) => p + 1)}>
                Next
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
