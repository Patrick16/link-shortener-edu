import { useState } from 'react'
import { AccessInfoModal } from './AccessInfoModal'
import { ComponentCard } from './ComponentCard'
import { ServiceControls } from './ServiceControls'
import { Sparkline } from './Sparkline'
import { renderCapabilityControls } from '../utils/controlRegistry'
import { statusColor } from '../utils/statusColor'
import { getQuickLinks } from '../utils/quickLink'
import { getAccessInfo } from '../utils/accessInfo'
import type { ArchComponent } from '../types/architecture'
import type { ManagedContainer, ResourceSample } from '../types/controlApi'

interface Props {
  component: ArchComponent
  serviceId: string | null
  instances: ManagedContainer[]
  // Keyed by containerId, not serviceId - a scaled service's replicas each have their own history
  // (see useLiveStack), so every instance below gets its own chart instead of all of them sharing
  // one arbitrary instance's numbers.
  resourceHistoryByContainer: Record<string, ResourceSample[]>
  onClose: () => void
  // Both omitted for a node with no controllable container (see the !serviceId branch in App) -
  // there's no live CPU/RAM/TCP reading to pin in that case.
  pinned?: boolean
  onTogglePin?: () => void
}

export function NodePanel({ component, serviceId, instances, resourceHistoryByContainer, onClose, pinned, onTogglePin }: Props) {
  const primary = instances[0]
  const hasCharts = instances.some((instance) => (resourceHistoryByContainer[instance.containerId]?.length ?? 0) > 0)
  const quickLinks = getQuickLinks(component)
  const accessInfo = getAccessInfo(component)
  const [showAccessInfo, setShowAccessInfo] = useState(false)

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

      {(quickLinks.length > 0 || accessInfo) && (
        <div className="node-panel-link-row">
          {quickLinks.map((link) => (
            <a key={link.url} className="node-panel-quick-link" href={link.url} target="_blank" rel="noreferrer">
              {link.label} ↗
            </a>
          ))}
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

          {serviceId && renderCapabilityControls(component, serviceId, instances)}

          {hasCharts &&
            instances.map((instance) => {
              const history = resourceHistoryByContainer[instance.containerId] ?? []
              if (history.length === 0) {
                return null
              }

              const cpuValues = history.map((s) => s.cpuPercent)
              const memValuesMb = history.map((s) => s.memoryUsageBytes / (1024 * 1024))
              const cpuMax = Math.max(5, ...cpuValues) * 1.4
              const memMax = Math.max(64, ...memValuesMb) * 1.3
              const instanceLabel = instances.length > 1 ? ` #${instance.containerNumber}` : ''

              return (
                <div className="node-panel-charts" key={instance.containerId}>
                  <Sparkline label={`CPU${instanceLabel}`} values={cpuValues} max={cpuMax} formatValue={(v) => `${v.toFixed(1)}%`} />
                  <Sparkline label={`Memory${instanceLabel}`} values={memValuesMb} max={memMax} formatValue={(v) => `${v.toFixed(0)} MB`} />
                </div>
              )
            })}
        </>
      ) : (
        <p className="component-card-note">Not a controllable container (not part of the docker-compose stack).</p>
      )}

      <ComponentCard component={component} />
    </div>
  )
}
