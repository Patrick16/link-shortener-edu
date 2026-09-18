import { ComponentCard } from './ComponentCard'
import { ServiceControls } from './ServiceControls'
import { Sparkline } from './Sparkline'
import type { ArchComponent } from '../types/architecture'
import type { ManagedContainer, ResourceSample } from '../types/controlApi'

interface Props {
  component: ArchComponent
  container: ManagedContainer | null
  resourceHistory: ResourceSample[]
  onClose: () => void
}

export function NodePanel({ component, container, resourceHistory, onClose }: Props) {
  const cpuValues = resourceHistory.map((s) => s.cpuPercent)
  const memValuesMb = resourceHistory.map((s) => s.memoryUsageBytes / (1024 * 1024))
  const cpuMax = Math.max(5, ...cpuValues) * 1.4
  const memMax = Math.max(64, ...memValuesMb) * 1.3

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

      {container ? (
        <>
          <div className="service-card-status">
            <span
              className="status-dot"
              style={{ background: container.state === 'running' ? '#22c55e' : container.state === 'exited' ? '#ef4444' : '#f59e0b' }}
            />
            {container.status}
          </div>

          <ServiceControls serviceId={container.serviceId} state={container.state} />

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
