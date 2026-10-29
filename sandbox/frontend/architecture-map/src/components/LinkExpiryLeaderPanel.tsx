import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { LinkExpiryLeaderStatus } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const POLL_MS = 2000

// Polled, not fetch-once - same reasoning as DlqStatsPanel/PgcatConnectionsPanel. A plain re-fetch
// every 2s (not a locally ticking countdown between polls) is deliberate: this is meant to make a
// kill-and-watch failover demo legible (stop a replica, watch the leader name change and the TTL
// reset to a fresh lease once another replica's claim wins), not to render a smooth clock - a
// locally ticking countdown would drift from the real lease the moment a renew lands early.
export function LinkExpiryLeaderPanel(_props: CapabilityControlProps) {
  const [status, setStatus] = useState<LinkExpiryLeaderStatus | null>(null)
  const [isStale, setIsStale] = useState(false)

  useEffect(() => {
    let cancelled = false
    function refresh() {
      controlApi
        .getLinkExpiryLeader()
        .then((s) => {
          if (cancelled) return
          setStatus(s)
          setIsStale(false)
        })
        .catch(() => !cancelled && setIsStale(true))
    }
    refresh()
    const interval = setInterval(refresh, POLL_MS)
    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [])

  if (!status) {
    return isStale ? <p className="connection-stats-hint">Link-expiry leader status unavailable.</p> : null
  }

  return (
    <div className="connection-stats">
      <h4>Link-expiry leader {isStale && <span className="connection-stats-stale">(stale)</span>}</h4>
      {status.leader === null ? (
        <p className="connection-stats-hint">No replica currently holds the lease.</p>
      ) : (
        <div className="connection-stats-row">
          <span className="connection-stats-db">{status.leader}</span>
          <span className="connection-stats-value">{status.leaseTtlMs !== null ? `${Math.round(status.leaseTtlMs / 1000)}s left` : '?'}</span>
        </div>
      )}
      <p className="connection-stats-hint">
        Elected via a renewed Redis lease, not a per-tick Postgres advisory lock - stop this
        container to watch another ShortenerService replica take over once the lease expires.
      </p>
    </div>
  )
}
