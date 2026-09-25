import { useEffect, useState } from 'react'
import { AccessInfoModal } from './AccessInfoModal'
import { ComponentCard } from './ComponentCard'
import { ServiceControls } from './ServiceControls'
import { ScaleControl } from './ScaleControl'
import { FlushCacheControl } from './FlushCacheControl'
import { InfraToggleControl } from './InfraToggleControl'
import { MongoReadPreferenceControl } from './MongoReadPreferenceControl'
import { NpgsqlPoolSizeControl } from './NpgsqlPoolSizeControl'
import { PgcatPoolControl } from './PgcatPoolControl'
import { RabbitMqPrefetchControl } from './RabbitMqPrefetchControl'
import { ReplicationLagControl } from './ReplicationLagControl'
import { SentinelConfigControl } from './SentinelConfigControl'
import { Sparkline } from './Sparkline'
import { controlApi, ControlApiError } from '../api/controlApi'
import { statusColor } from '../utils/statusColor'
import { getQuickLink } from '../utils/quickLink'
import { getAccessInfo } from '../utils/accessInfo'
import type { ArchComponent } from '../types/architecture'
import type { InfraStatus, ManagedContainer, PgcatConnectionStats, PostgresConnectionStats, ResourceSample } from '../types/controlApi'

const CONNECTIONS_POLL_MS = 3000

interface Props {
  component: ArchComponent
  serviceId: string | null
  instances: ManagedContainer[]
  resourceHistory: ResourceSample[]
  onClose: () => void
  // Both omitted for a node with no controllable container (see the !serviceId branch in App) -
  // there's no live CPU/RAM/TCP reading to pin in that case.
  pinned?: boolean
  onTogglePin?: () => void
}

export function NodePanel({ component, serviceId, instances, resourceHistory, onClose, pinned, onTogglePin }: Props) {
  const [scalable, setScalable] = useState<string[]>([])
  const [infraStatus, setInfraStatus] = useState<InfraStatus | null>(null)
  const [infraBusy, setInfraBusy] = useState(false)
  const [infraError, setInfraError] = useState<string | null>(null)
  const cpuValues = resourceHistory.map((s) => s.cpuPercent)
  const memValuesMb = resourceHistory.map((s) => s.memoryUsageBytes / (1024 * 1024))
  const cpuMax = Math.max(5, ...cpuValues) * 1.4
  const memMax = Math.max(64, ...memValuesMb) * 1.3
  const primary = instances[0]
  const quickLink = getQuickLink(component)
  const accessInfo = getAccessInfo(component)
  const [showAccessInfo, setShowAccessInfo] = useState(false)
  const showsInfraToggle = serviceId === 'nginx' || serviceId === 'pgcat' || serviceId === 'redis-master'
  const showsPgcatConnections = serviceId === 'pgcat'
  const showsPostgresConnections = component.type === 'database'
  const [pgcatConnections, setPgcatConnections] = useState<PgcatConnectionStats | null>(null)
  const [postgresConnections, setPostgresConnections] = useState<PostgresConnectionStats | null>(null)

  useEffect(() => {
    controlApi.listScalableServices().then(setScalable).catch(() => {})
  }, [])

  useEffect(() => {
    if (!showsInfraToggle) return
    controlApi.getInfraStatus().then(setInfraStatus).catch(() => {})
  }, [showsInfraToggle])

  // Polled, not fetch-once - "how many connections are open" is exactly the kind of number that's
  // stale the moment it's read, unlike the standing infra toggles above.
  useEffect(() => {
    if (!showsPgcatConnections && !showsPostgresConnections) return
    let cancelled = false
    function refresh() {
      if (showsPgcatConnections) controlApi.getPgcatConnections().then((s) => !cancelled && setPgcatConnections(s)).catch(() => {})
      if (showsPostgresConnections) controlApi.getPostgresConnections().then((s) => !cancelled && setPostgresConnections(s)).catch(() => {})
    }
    refresh()
    const interval = setInterval(refresh, CONNECTIONS_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [showsPgcatConnections, showsPostgresConnections])

  async function toggleInfra(apply: (enabled: boolean) => Promise<InfraStatus>, enabled: boolean) {
    setInfraBusy(true)
    setInfraError(null)
    try {
      setInfraStatus(await apply(enabled))
    } catch (err) {
      setInfraError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setInfraBusy(false)
    }
  }

  return (
    <div className="side-panel">
      <div className="side-panel-header">
        <h2>
          {component.icon} {component.name}
        </h2>
        <button onClick={onClose} aria-label="Close">
          &times;
        </button>
      </div>

      {(quickLink || accessInfo) && (
        <div className="node-panel-link-row">
          {quickLink && (
            <a className="node-panel-quick-link" href={quickLink.url} target="_blank" rel="noreferrer">
              {quickLink.label} ↗
            </a>
          )}
          {accessInfo && (
            <button className="node-panel-quick-link node-panel-access-btn" onClick={() => setShowAccessInfo(true)}>
              🔑 Access info
            </button>
          )}
        </div>
      )}

      {showAccessInfo && accessInfo && (
        <AccessInfoModal title={component.name} info={accessInfo} onClose={() => setShowAccessInfo(false)} />
      )}

      {serviceId && onTogglePin && (
        <button
          className={pinned ? 'node-panel-pin-btn node-panel-pin-btn-active' : 'node-panel-pin-btn'}
          onClick={onTogglePin}
        >
          {pinned ? '📌 Unpin metrics' : '📌 Pin metrics'}
        </button>
      )}

      {primary ? (
        <>
          {instances.length > 1 ? (
            <ul className="instance-list">
              {instances.map((instance) => (
                <li key={instance.containerId}>
                  <span className="status-dot" style={{ background: statusColor(instance.state) }} />
                  #{instance.containerNumber} - {instance.status}
                </li>
              ))}
            </ul>
          ) : (
            <div className="service-card-status">
              <span className="status-dot" style={{ background: statusColor(primary.state) }} />
              {primary.status}
            </div>
          )}

          <ServiceControls serviceId={primary.serviceId} state={primary.state} />

          {serviceId && scalable.includes(serviceId) && <ScaleControl serviceId={serviceId} currentReplicas={instances.length} />}
          {serviceId === 'redis-master' && <FlushCacheControl />}

          {infraStatus && serviceId === 'nginx' && (
            <InfraToggleControl
              label="Load balancing"
              description="Off routes load-test traffic straight to a single link-api/redirect-api container, bypassing nginx - shows the system without balancing across replicas. nginx itself keeps running, so the app UI is unaffected."
              enabled={!infraStatus.nginxBypassed}
              busy={infraBusy}
              onToggle={(enabled) => toggleInfra(controlApi.setNginxEnabled, enabled)}
            />
          )}
          {infraStatus && serviceId === 'pgcat' && (
            <InfraToggleControl
              label="Connection pooling"
              description="Off reconnects every DB-touching service straight to Postgres, bypassing pgcat - shows the system without connection pooling. Recreates 5 containers, takes a few seconds."
              enabled={infraStatus.pgcatEnabled}
              busy={infraBusy}
              onToggle={(enabled) => toggleInfra(controlApi.setPgcatEnabled, enabled)}
            />
          )}
          {infraStatus && serviceId === 'redis-master' && (
            <InfraToggleControl
              label="Caching"
              description="Off makes LinkApi/RedirectApi skip Redis entirely and always read Postgres - shows the system without caching. Recreates 2 containers, takes a few seconds."
              enabled={infraStatus.cacheEnabled}
              busy={infraBusy}
              onToggle={(enabled) => toggleInfra(controlApi.setCacheEnabled, enabled)}
            />
          )}
          {infraError && <p className="service-card-error">{infraError}</p>}

          {serviceId === 'pgcat' && <PgcatPoolControl />}
          {serviceId === 'pgcat' && <NpgsqlPoolSizeControl />}
          {(serviceId === 'postgres-replica1' || serviceId === 'postgres-replica2') && <ReplicationLagControl serviceId={serviceId} />}
          {serviceId?.startsWith('redis-sentinel') && <SentinelConfigControl />}
          {serviceId === 'rabbitmq' && <RabbitMqPrefetchControl />}
          {serviceId?.startsWith('mongo') && <MongoReadPreferenceControl />}

          {pgcatConnections && showsPgcatConnections && (
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
          )}

          {postgresConnections && showsPostgresConnections && (
            <div className="connection-stats">
              <h4>Connections</h4>
              <div className="connection-stats-row">
                <span className="connection-stats-db">{component.name}</span>
                <span className="connection-stats-value">{postgresConnections.connectionsByDatabase[component.name] ?? 0} backend connections</span>
              </div>
            </div>
          )}

          {resourceHistory.length > 0 && (
            <div className="node-panel-charts">
              <Sparkline label="CPU" values={cpuValues} max={cpuMax} formatValue={(v) => `${v.toFixed(1)}%`} />
              <Sparkline label="Memory" values={memValuesMb} max={memMax} formatValue={(v) => `${v.toFixed(0)} MB`} />
            </div>
          )}
        </>
      ) : (
        <p className="component-card-note">Not a controllable container (not part of the docker-compose stack).</p>
      )}

      <ComponentCard component={component} />
    </div>
  )
}
