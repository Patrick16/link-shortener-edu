import { useEffect, useRef, useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import { buildShortUrl, deleteLink, getLinks } from '../api/linkApi'
import { getClickSummary, getRecentClicks } from '../api/statsApi'
import { ApiError } from '../api/client'
import { ClicksByDayChart } from '../components/ClicksByDayChart'
import { TopLinksPanel } from '../components/TopLinksPanel'
import { useTopLinks } from '../hooks/useTopLinks'
import type { ClickDetail, ClickSummary, LinkListItem, LinksPage } from '../types'

type Tab = 'active' | 'deleted'

// Both a manual delete and a TTL expiry just set DeletedAt - there's no separate "reason" field on
// the row itself (only the Redis tombstone the backend writes has one, and that's not exposed
// here). Approximates it for display: if the link had a TTL and that TTL had already passed by
// the time it was marked deleted, call it "Expired", otherwise "Deleted". Good enough for a label,
// not meant to be exact to the millisecond.
function deletionLabel(item: LinkListItem): string {
  if (item.expiresAt && item.deletedAt && new Date(item.expiresAt).getTime() <= new Date(item.deletedAt).getTime()) {
    return 'Expired'
  }
  return 'Deleted'
}

// Keyed by hash. 'loading'/'error' are distinct from "not yet requested" (absent key) so toggling
// a panel closed and back open doesn't refetch - this is a CQRS read model fed asynchronously from
// ClickHouse, not a live query, so re-fetching on every toggle wouldn't even show something newer
// most of the time, just extra round trips.
type SummaryState = ClickSummary | 'loading' | 'error'

// Same "distinct from not-yet-requested" shape as SummaryState, for the Mongo-backed recent-clicks
// list fetched alongside it.
type RecentClicksState = ClickDetail[] | 'loading' | 'error'

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

// Mongo-backed counterpart to StatsPanel - individual raw click rows (exact city, full device
// info) instead of pre-aggregated ClickHouse buckets. "No recent clicks." deliberately isn't the
// same copy as StatsPanel's "No clicks yet." - the two panels can be on screen at once, and
// getByText-style lookups (tests, screen readers) need the text to stay unambiguous.
function RecentClicksPanel({ clicks }: { clicks: RecentClicksState }) {
  if (clicks === 'loading') return <p className="hint">Loading recent clicks…</p>
  if (clicks === 'error') return <p className="error">Could not load recent clicks for this link.</p>

  if (clicks.length === 0) {
    return <p className="hint">No recent clicks.</p>
  }

  const location = (click: ClickDetail) => [click.city, click.country].filter(Boolean).join(', ') || '—'
  const device = (click: ClickDetail) => [click.browser, click.os, click.deviceType].filter(Boolean).join(' · ') || '—'

  return (
    <div className="recent-clicks-panel">
      <h4>Recent clicks</h4>
      <ul className="recent-clicks-list">
        {clicks.map((click) => (
          <li key={click.id} className="recent-clicks-item">
            <span className="recent-clicks-time">{new Date(click.clickedAt).toLocaleString()}</span>
            <span className="recent-clicks-location">{location(click)}</span>
            <span className="recent-clicks-device">{device(click)}</span>
          </li>
        ))}
      </ul>
    </div>
  )
}

export default function DashboardPage() {
  const { user } = useAuth()
  const [tab, setTab] = useState<Tab>('active')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<LinksPage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [expandedHash, setExpandedHash] = useState<string | null>(null)
  const [summaries, setSummaries] = useState<Record<string, SummaryState>>({})
  const [recentClicks, setRecentClicks] = useState<Record<string, RecentClicksState>>({})
  const [deletingHash, setDeletingHash] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const topLinks = useTopLinks(Boolean(user))

  function switchTab(next: Tab) {
    if (next === tab) return
    setTab(next)
    // Each tab is its own disjoint list (see linkApi.getLinks's own comment) - carrying over the
    // other tab's page number would silently 404-equivalent into an empty page more often than not.
    setPage(1)
  }

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
    getLinks(page, tab === 'deleted')
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
  }, [user, page, tab])

  async function handleDelete(hash: string) {
    // Irreversible (a deleted hash is never reissued) - a plain confirm() is the simplest guard
    // against a stray click, consistent with this being the only destructive action on this page.
    if (!window.confirm('Delete this link? This cannot be undone.')) return

    setDeletingHash(hash)
    setDeleteError(null)
    try {
      await deleteLink(hash)
      // Optimistic local removal rather than a refetch - the backend's own delete flow is
      // asynchronous (DeleteLink dispatches an event; ShortenerService persists DeletedAt a beat
      // later), so an immediate refetch could still show the link as active for a moment. Removing
      // it from this tab's list directly reflects what the user just asked for; it'll show up in
      // the Deleted tab whenever that tab is next loaded.
      setData((prev) => (prev ? { ...prev, items: prev.items.filter((i) => i.shortenLink !== hash), totalCount: Math.max(0, prev.totalCount - 1) } : prev))
    } catch (err) {
      setDeleteError(err instanceof ApiError ? err.message : 'Could not delete that link. Please try again.')
    } finally {
      setDeletingHash(null)
    }
  }

  // `force` skips the "already loaded" guard below - used by the manual Refresh button (found
  // missing in review: summaries/recentClicks cache for the rest of the session once loaded, with
  // no way to see newer data short of a full page reload, even though both are genuinely live data
  // that keeps changing as new clicks arrive). Toggling the panel closed and back open still only
  // fetches once per hash, same as before - a refresh is an explicit action, not automatic.
  function fetchStatsFor(hash: string, force: boolean) {
    // Skip refetching an already-loaded or in-flight summary, but NOT a previously failed one -
    // otherwise a transient failure (ReportingApi/ClickHouse briefly down) would lock that link's
    // panel into "Could not load stats" for the rest of the session with no way to retry short of a
    // full page reload.
    const existing = summaries[hash]
    if (force || existing === undefined || existing === 'error') {
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

    // Independent fetch, same skip/retry rule as the summary above - Mongo and ClickHouse are two
    // separate read models fed by two separate consumers of the same event, so one failing doesn't
    // imply anything about the other.
    const existingRecent = recentClicks[hash]
    if (force || existingRecent === undefined || existingRecent === 'error') {
      setRecentClicks((prev) => ({ ...prev, [hash]: 'loading' }))
      getRecentClicks(hash)
        .then((clicks) => {
          if (!mountedRef.current) return
          setRecentClicks((prev) => ({ ...prev, [hash]: clicks }))
        })
        .catch(() => {
          if (!mountedRef.current) return
          setRecentClicks((prev) => ({ ...prev, [hash]: 'error' }))
        })
    }
  }

  function toggleStats(hash: string) {
    if (expandedHash === hash) {
      setExpandedHash(null)
      return
    }

    setExpandedHash(hash)
    fetchStatsFor(hash, false)
  }

  if (!user) return null

  const loading = !data && !error

  return (
    <div className="card dashboard">
      <TopLinksPanel items={topLinks.items} status={topLinks.status} />

      <h2>My links</h2>

      <div className="tab-bar">
        <button type="button" className={`tab-button${tab === 'active' ? ' tab-button-active' : ''}`} onClick={() => switchTab('active')}>
          Active
        </button>
        <button type="button" className={`tab-button${tab === 'deleted' ? ' tab-button-active' : ''}`} onClick={() => switchTab('deleted')}>
          Deleted
        </button>
      </div>

      {loading && <p className="hint">Loading…</p>}
      {error && <p className="error">{error}</p>}
      {deleteError && <p className="error">{deleteError}</p>}

      {data && data.items.length === 0 && (
        <p className="hint">
          {tab === 'active' ? "You haven't created any links yet." : 'No deleted or expired links.'}
        </p>
      )}

      {data && data.items.length > 0 && (
        <>
          <ul className="link-list">
            {data.items.map((item) => (
              <li key={item.shortenLink} className="link-list-item">
                <a href={buildShortUrl(item.shortenLink)} target="_blank" rel="noreferrer">
                  {buildShortUrl(item.shortenLink)}
                </a>
                {item.hasCustomAlias && <span className="alias-badge">Custom alias</span>}
                {tab === 'active' && item.expiresAt && (
                  <span className="ttl-badge">Expires {new Date(item.expiresAt).toLocaleString()}</span>
                )}
                {tab === 'deleted' && (
                  <span className="deleted-badge">
                    {deletionLabel(item)} {item.deletedAt && new Date(item.deletedAt).toLocaleString()}
                  </span>
                )}
                <span className="link-list-original">{item.originalLink}</span>
                <span className="link-list-clicks">
                  {item.clickCount} {item.clickCount === 1 ? 'click' : 'clicks'}
                </span>
                <button type="button" className="link-stats-toggle" onClick={() => toggleStats(item.shortenLink)}>
                  {expandedHash === item.shortenLink ? 'Hide stats' : 'Stats'}
                </button>
                {tab === 'active' && (
                  <button
                    type="button"
                    className="link-delete-button"
                    disabled={deletingHash === item.shortenLink}
                    onClick={() => handleDelete(item.shortenLink)}
                  >
                    {deletingHash === item.shortenLink ? 'Deleting…' : 'Delete'}
                  </button>
                )}
                {expandedHash === item.shortenLink && (
                  <>
                    <button
                      type="button"
                      className="link-stats-refresh"
                      onClick={() => fetchStatsFor(item.shortenLink, true)}
                      // || not && (feature-review F1): summary and recent-clicks are independent
                      // fetches that typically resolve at different times - with &&, the button
                      // re-enabled as soon as either one resolved while the other was still
                      // in-flight, letting a second click fire an overlapping fetch for the same
                      // field whose out-of-order response could silently overwrite fresher data.
                      disabled={summaries[item.shortenLink] === 'loading' || recentClicks[item.shortenLink] === 'loading'}
                    >
                      Refresh stats
                    </button>
                    <StatsPanel summary={summaries[item.shortenLink] ?? 'loading'} />
                    <RecentClicksPanel clicks={recentClicks[item.shortenLink] ?? 'loading'} />
                  </>
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
