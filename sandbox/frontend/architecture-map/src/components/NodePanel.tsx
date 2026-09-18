import { useEffect, useState } from 'react'
import { ComponentCard } from './ComponentCard'
import { ServiceControls } from './ServiceControls'
import { ScaleControl } from './ScaleControl'
import { FlushCacheControl } from './FlushCacheControl'
import { Sparkline } from './Sparkline'
import { controlApi } from '../api/controlApi'
import { statusColor } from '../utils/statusColor'
import type { ArchComponent } from '../types/architecture'
import type { ManagedContainer, ResourceSample } from '../types/controlApi'

interface Props {
  component: ArchComponent
  serviceId: string | null
  instances: ManagedContainer[]
  resourceHistory: ResourceSample[]
  onClose: () => void
}

export function NodePanel({ component, serviceId, instances, resourceHistory, onClose }: Props) {
  const [scalable, setScalable] = useState<string[]>([])
  const cpuValues = resourceHistory.map((s) => s.cpuPercent)
  const memValuesMb = resourceHistory.map((s) => s.memoryUsageBytes / (1024 * 1024))
  const cpuMax = Math.max(5, ...cpuValues) * 1.4
  const memMax = Math.max(64, ...memValuesMb) * 1.3
  const primary = instances[0]

  useEffect(() => {
    controlApi.listScalableServices().then(setScalable).catch(() => {})
  }, [])

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
