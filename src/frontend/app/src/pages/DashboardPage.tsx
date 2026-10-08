import { useEffect, useRef, useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import { buildShortUrl, getLinks } from '../api/linkApi'
import { getClickSummary } from '../api/statsApi'
import { ApiError } from '../api/client'
import { ClicksByDayChart } from '../components/ClicksByDayChart'
import type { ClickSummary, LinksPage } from '../types'

// Keyed by hash. 'loading'/'error' are distinct from "not yet requested" (absent key) so toggling
// a panel closed and back open doesn't refetch - this is a CQRS read model fed asynchronously from
// ClickHouse, not a live query, so re-fetching on every toggle wouldn't even show something newer
// most of the time, just extra round trips.
type SummaryState = ClickSummary | 'loading' | 'error'

function StatsPanel({ summary }: { summary: SummaryState }) {
  if (summary === 'loading') return <p className="hint">Loading stats…</p>
  if (summary === 'error') return <p className="error">Could not load stats for this link.</p>

  if (summary.totalClicks === 0) {
    return <p className="hint">No clicks yet.</p>
  }

  const topBucket = (buckets: typeof summary.byCountry) =>
    buckets.length === 0 ? '—' : buckets.map((b) => `${b.key} (${b.count})`).join(', ')

  return (
    <dl className="stats-panel">
      <dt>Total clicks</dt>
      <dd>{summary.totalClicks}</dd>
      <dt>By day</dt>
      <dd>{summary.byDay.length > 0 ? <ClicksByDayChart buckets={summary.byDay} /> : '—'}</dd>
      <dt>Top countries</dt>
      <dd>{topBucket(summary.byCountry)}</dd>
      <dt>Top devices</dt>
      <dd>{topBucket(summary.byDevice)}</dd>
      <dt>Top browsers</dt>
      <dd>{topBucket(summary.byBrowser)}</dd>
    </dl>
  )
}

export default function DashboardPage() {
  const { user } = useAuth()
  const [page, setPage] = useState(1)
  const [data, setData] = useState<LinksPage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [expandedHash, setExpandedHash] = useState<string | null>(null)
  const [summaries, setSummaries] = useState<Record<string, SummaryState>>({})

  // Guards the stats fetch below against setting state after unmount, same reason the getLinks
  // effect just below uses its own local `cancelled` flag - a ref (not state) since toggleStats is
  // a plain event handler, not an effect, so it has no cleanup function of its own to flip a flag in.
  // Must set mountedRef.current = true in the effect body itself, not just via useRef(true)'s
  // initial value - StrictMode (see main.tsx) double-invokes every effect on mount (mount ->
  // cleanup -> mount again) specifically to catch bugs like this one. A cleanup-only effect left
  // the ref permanently false after that very first simulated unmount, even though the component
  // was genuinely still mounted - every subsequent stats fetch then silently dropped its own
  // result, stuck on "Loading stats..." forever (found live: response arrived, panel never
  // updated).
  const mountedRef = useRef(true)
  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

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

  function toggleStats(hash: string) {
    if (expandedHash === hash) {
      setExpandedHash(null)
      return
    }

    setExpandedHash(hash)
    // Skip refetching an already-loaded or in-flight summary, but NOT a previously failed one -
    // otherwise a transient failure (ReportingApi/ClickHouse briefly down) would lock that link's
    // panel into "Could not load stats" for the rest of the session with no way to retry short of a
    // full page reload.
    const existing = summaries[hash]
    if (existing !== undefined && existing !== 'error') return

    setSummaries((prev) => ({ ...prev, [hash]: 'loading' }))
    getClickSummary(hash)
      .then((summary) => {
        if (!mountedRef.current) return
        setSummaries((prev) => ({ ...prev, [hash]: summary }))
      })
      .catch(() => {
        if (!mountedRef.current) return
        setSummaries((prev) => ({ ...prev, [hash]: 'error' }))
      })
  }

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
                <button type="button" className="link-stats-toggle" onClick={() => toggleStats(item.shortenLink)}>
                  {expandedHash === item.shortenLink ? 'Hide stats' : 'Stats'}
                </button>
                {expandedHash === item.shortenLink && (
                  <StatsPanel summary={summaries[item.shortenLink] ?? 'loading'} />
                )}
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
