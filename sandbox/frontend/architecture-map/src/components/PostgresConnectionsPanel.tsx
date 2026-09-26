import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { PostgresConnectionStats } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const CONNECTIONS_POLL_MS = 3000

// Polled, not fetch-once - see PgcatConnectionsPanel.
export function PostgresConnectionsPanel({ component }: CapabilityControlProps) {
  const [postgresConnections, setPostgresConnections] = useState<PostgresConnectionStats | null>(null)

  useEffect(() => {
    let cancelled = false
    function refresh() {
      controlApi.getPostgresConnections().then((s) => !cancelled && setPostgresConnections(s)).catch(() => {})
    }
    refresh()
    const interval = setInterval(refresh, CONNECTIONS_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [])

  if (!postgresConnections) {
    return null
  }

  return (
    <div className="connection-stats">
      <h4>Connections</h4>
      <div className="connection-stats-row">
        <span className="connection-stats-db">{component.name}</span>
        <span className="connection-stats-value">{postgresConnections.connectionsByDatabase[component.name] ?? 0} backend connections</span>
      </div>
    </div>
  )
}
