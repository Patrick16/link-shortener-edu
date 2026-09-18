import { useEffect, useState } from 'react'
import { ComponentCard } from './ComponentCard'
import { ServiceControls } from './ServiceControls'
import { ScaleControl } from './ScaleControl'
import { FlushCacheControl } from './FlushCacheControl'
import { InfraToggleControl } from './InfraToggleControl'
import { Sparkline } from './Sparkline'
import { controlApi, ControlApiError } from '../api/controlApi'
import { statusColor } from '../utils/statusColor'
import type { ArchComponent } from '../types/architecture'
import type { InfraStatus, ManagedContainer, ResourceSample } from '../types/controlApi'

interface Props {
  component: ArchComponent
  serviceId: string | null
  instances: ManagedContainer[]
  resourceHistory: ResourceSample[]
  onClose: () => void
}

export function NodePanel({ component, serviceId, instances, resourceHistory, onClose }: Props) {
  const [scalable, setScalable] = useState<string[]>([])
  const [infraStatus, setInfraStatus] = useState<InfraStatus | null>(null)
  const [infraBusy, setInfraBusy] = useState(false)
  const [infraError, setInfraError] = useState<string | null>(null)
  const cpuValues = resourceHistory.map((s) => s.cpuPercent)
  const memValuesMb = resourceHistory.map((s) => s.memoryUsageBytes / (1024 * 1024))
  const cpuMax = Math.max(5, ...cpuValues) * 1.4
  const memMax = Math.max(64, ...memValuesMb) * 1.3
  const primary = instances[0]
  const showsInfraToggle = serviceId === 'nginx' || serviceId === 'pgcat' || serviceId === 'redis'

  useEffect(() => {
    controlApi.listScalableServices().then(setScalable).catch(() => {})
  }, [])

  useEffect(() => {
    if (!showsInfraToggle) return
    controlApi.getInfraStatus().then(setInfraStatus).catch(() => {})
  }, [showsInfraToggle])

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
          {serviceId === 'redis' && <FlushCacheControl />}

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
          {infraStatus && serviceId === 'redis' && (
            <InfraToggleControl
              label="Caching"
              description="Off makes LinkApi/RedirectApi skip Redis entirely and always read Postgres - shows the system without caching. Recreates 2 containers, takes a few seconds."
              enabled={infraStatus.cacheEnabled}
              busy={infraBusy}
              onToggle={(enabled) => toggleInfra(controlApi.setCacheEnabled, enabled)}
            />
          )}
          {infraError && <p className="service-card-error">{infraError}</p>}

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
