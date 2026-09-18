import './App.css'
import architectureData from './data/architecture.json'
import { useContainerStatus } from './hooks/useContainerStatus'
import { ServiceCard } from './components/ServiceCard'
import { TrafficPanel } from './components/TrafficPanel'
import type { ArchitectureData } from './types/architecture'

const data = architectureData as unknown as ArchitectureData
const metaById = new Map(data.components.map((c) => [c.id, c]))

// This is the control panel for the real running sandbox stack - not the node-graph diagram yet
// (that's still TODO, see .notes/ARCHITECTURE_MAP_PLAN.md). Cards are driven by what's actually
// running (GET /api/containers via useContainerStatus), not by architecture.json - the logical
// "-db" components in that data all live in the single `postgres` container, so they intentionally
// don't get their own card here.
function App() {
  const { containers, connected, loading, error } = useContainerStatus()
  const services = Object.values(containers).sort((a, b) => a.serviceId.localeCompare(b.serviceId))

  return (
    <main>
      <h1>Architecture Map - Control Panel</h1>
      <p className="connection-status">{connected ? 'Live' : 'Connecting...'}</p>

      <TrafficPanel />

      {loading && <p>Loading containers...</p>}
      {error && <p className="service-card-error">{error}</p>}

      <div className="service-grid">
        {services.map((container) => (
          <ServiceCard key={container.serviceId} container={container} meta={metaById.get(container.serviceId)} />
        ))}
      </div>
    </main>
  )
}

export default App
