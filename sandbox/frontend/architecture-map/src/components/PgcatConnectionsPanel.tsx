import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { PgcatConnectionStats } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const CONNECTIONS_POLL_MS = 3000

// Polled, not fetch-once - "how many connections are open" is exactly the kind of number that's
// stale the moment it's read, unlike the standing infra toggles.
export function PgcatConnectionsPanel(_props: CapabilityControlProps) {
  const [pgcatConnections, setPgcatConnections] = useState<PgcatConnectionStats | null>(null)

  useEffect(() => {
    let cancelled = false
    function refresh() {
      controlApi.getPgcatConnections().then((s) => !cancelled && setPgcatConnections(s)).catch(() => {})
    }
    refresh()
    const interval = setInterval(refresh, CONNECTIONS_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [])

  if (!pgcatConnections) {
    return null
  }

  return (
    <div className="connection-stats">
      <h4>Connections</h4>
      <div className="connection-stats-header">
        <span />
        <span>clients</span>
        <span>servers</span>
      </div>
      {pgcatConnections.pools.map((pool) => (
        <div className="connection-stats-row" key={pool.database}>
          <span className="connection-stats-db">{pool.database}</span>
          <span className="connection-stats-value">{pool.clientIdle + pool.clientActive + pool.clientWaiting}</span>
          <span className="connection-stats-value">{pool.serverActive + pool.serverIdle + pool.serverUsed}</span>
        </div>
      ))}
      <p className="connection-stats-hint">clients: apps → pgcat · servers: pgcat → postgres (the pooling itself)</p>
    </div>
  )
}
