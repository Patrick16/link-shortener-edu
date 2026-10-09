import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { PgcatInstanceConnections } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const CONNECTIONS_POLL_MS = 3000

// Polled, not fetch-once - "how many connections are open" is exactly the kind of number that's
// stale the moment it's read, unlike the standing infra toggles. pgcat is 3 identical replicas
// behind haproxy (Pooler Scaling) drawn as one graph node, so this shows every replica's own
// pools, not a single instance's or a merged aggregate - the per-replica split is exactly the
// thing worth seeing here.
export function PgcatConnectionsPanel(_props: CapabilityControlProps) {
  const [instances, setInstances] = useState<PgcatInstanceConnections[] | null>(null)

  useEffect(() => {
    let cancelled = false
    function refresh() {
      controlApi.getPgcatConnections().then((s) => !cancelled && setInstances(s)).catch(() => {})
    }
    refresh()
    const interval = setInterval(refresh, CONNECTIONS_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [])

  if (!instances) {
    return null
  }

  return (
    <div className="connection-stats">
      <h4>Connections (per replica)</h4>
      {instances.map((instance) => (
        <div key={instance.instanceName}>
          <p className="connection-stats-instance-label">{instance.instanceName}</p>
          <div className="connection-stats-header">
            <span />
            <span>clients</span>
            <span>servers</span>
          </div>
          {instance.stats.pools.map((pool) => (
            <div className="connection-stats-row" key={pool.database}>
              <span className="connection-stats-db">{pool.database}</span>
              <span className="connection-stats-value">{pool.clientIdle + pool.clientActive + pool.clientWaiting}</span>
              <span className="connection-stats-value">{pool.serverActive + pool.serverIdle + pool.serverUsed}</span>
            </div>
          ))}
        </div>
      ))}
      <p className="connection-stats-hint">clients: apps → haproxy → pgcat · servers: pgcat → postgres (the pooling itself)</p>
    </div>
  )
}
