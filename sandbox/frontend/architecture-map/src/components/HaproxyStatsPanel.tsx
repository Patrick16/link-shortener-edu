import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { HaproxyStats } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const HAPROXY_POLL_MS = 3000

// Polled, not fetch-once - same reasoning as PgcatConnectionsPanel/DlqStatsPanel. Shows all 3
// pgcat instances' live up/down + session counts in one place, the load-balancing story in a
// single panel - see sandbox/infra/grafana/.../infrastructure.json's identical-purpose panel for
// the same data over a longer time window.
export function HaproxyStatsPanel(_props: CapabilityControlProps) {
  const [stats, setStats] = useState<HaproxyStats | null>(null)
  const [isStale, setIsStale] = useState(false)

  useEffect(() => {
    let cancelled = false
    function refresh() {
      controlApi
        .getHaproxyStats()
        .then((s) => {
          if (cancelled) return
          setStats(s)
          setIsStale(false)
        })
        .catch(() => !cancelled && setIsStale(true))
    }
    refresh()
    const interval = setInterval(refresh, HAPROXY_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [])

  if (!stats) {
    return isStale ? <p className="connection-stats-hint">HAProxy stats unavailable.</p> : null
  }

  return (
    <div className="connection-stats">
      <h4>Pooler backends {isStale && <span className="connection-stats-stale">(stale)</span>}</h4>
      <div className="connection-stats-header">
        <span />
        <span>status</span>
        <span>sessions</span>
      </div>
      {stats.servers.map((server) => (
        <div className="connection-stats-row" key={server.name}>
          <span className="connection-stats-db">{server.name}</span>
          <span className="connection-stats-value">{server.up ? 'UP' : 'DOWN'}</span>
          <span className="connection-stats-value">{server.currentSessions}</span>
        </div>
      ))}
      <p className="connection-stats-hint">
        With balance leastconn, sessions should track each other closely across all 3 under any sustained load.
      </p>
    </div>
  )
}
