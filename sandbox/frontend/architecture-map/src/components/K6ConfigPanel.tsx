import { TrafficConfigPanel } from './TrafficConfigPanel'
import { ComponentCard } from './ComponentCard'
import type { ArchComponent } from '../types/architecture'
import type { TrafficConfigState } from '../hooks/useTrafficConfig'

interface Props {
  component: ArchComponent
  config: TrafficConfigState
  disabled: boolean
  running: boolean
  onRun: () => void
  onClose: () => void
}

// k6 isn't a docker-compose service - it's a one-off container control-api creates per run (see
// DockerService.RunTrafficAsync), and that container is never labeled com.docker.compose.service,
// so ListContainersAsync can never see it - selecting k6 never has live status/controls to show the
// way every other node does. Clicking it is instead how you configure the *next* run: this is the
// same TrafficConfigPanel that used to sit in its own permanently-visible sidebar, now surfaced only
// when k6 itself is selected, in the same side-panel chrome every other node's detail uses.
export function K6ConfigPanel({ component, config, disabled, running, onRun, onClose }: Props) {
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

      <TrafficConfigPanel config={config} disabled={disabled} running={running} onRun={onRun} />

      <ComponentCard component={component} />
    </div>
  )
}
