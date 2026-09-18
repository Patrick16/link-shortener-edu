import { useMemo, useState } from 'react'
import './App.css'
import architectureData from './data/architecture.json'
import { useLiveStack } from './hooks/useLiveStack'
import { useTrafficRun } from './hooks/useTrafficRun'
import { Diagram } from './components/Diagram'
import { NodePanel } from './components/NodePanel'
import { ConnectionDetail } from './components/ConnectionDetail'
import { ScenarioSwitcher } from './components/ScenarioSwitcher'
import { TrafficPanel } from './components/TrafficPanel'
import { resolveServiceId } from './utils/resolveServiceId'
import type { ArchitectureData } from './types/architecture'

const data = architectureData as unknown as ArchitectureData
const metaById = new Map(data.components.map((c) => [c.id, c]))

type Selection = { kind: 'component'; id: string } | { kind: 'connection'; index: number } | null

function App() {
  const { containers, resourceHistory, connected, loading, error } = useLiveStack()
  const trafficRun = useTrafficRun()
  const [scenario, setScenario] = useState(data.scenarios[0]?.id ?? '1')
  const [selection, setSelection] = useState<Selection>(null)

  const knownServiceIds = useMemo(() => new Set(Object.keys(containers)), [containers])
  const visibleConnections = useMemo(
    () => data.connections.filter((c) => c.scenarios.includes(scenario)),
    [scenario],
  )

  const selectedComponent = selection?.kind === 'component' ? metaById.get(selection.id) : undefined
  const selectedConnection = selection?.kind === 'connection' ? visibleConnections[selection.index] : undefined
  const selectedServiceId = selectedComponent ? resolveServiceId(selectedComponent, knownServiceIds) : null
  const selectedInstances = selectedServiceId ? (containers[selectedServiceId] ?? []) : []

  return (
    <main>
      <h1>Architecture Map</h1>
      <p className="connection-status">{connected ? 'Live' : 'Connecting...'}</p>

      <ScenarioSwitcher scenarios={data.scenarios} selected={scenario} onSelect={setScenario} />

      <TrafficPanel {...trafficRun} />

      {loading && <p>Loading containers...</p>}
      {error && <p className="service-card-error">{error}</p>}

      <div className="diagram-layout">
        <Diagram
          data={data}
          scenario={scenario}
          containers={containers}
          trafficActive={trafficRun.running}
          onSelectComponent={(id) => setSelection({ kind: 'component', id })}
          onSelectConnection={(index) => setSelection({ kind: 'connection', index })}
        />

        {selectedComponent && selectedServiceId && (
          <NodePanel
            component={selectedComponent}
            serviceId={selectedServiceId}
            instances={selectedInstances}
            resourceHistory={resourceHistory[selectedServiceId] ?? []}
            onClose={() => setSelection(null)}
          />
        )}
        {selectedComponent && !selectedServiceId && (
          <NodePanel component={selectedComponent} serviceId={null} instances={[]} resourceHistory={[]} onClose={() => setSelection(null)} />
        )}

        {selectedConnection && <ConnectionDetail connection={selectedConnection} onClose={() => setSelection(null)} />}
      </div>
    </main>
  )
}

export default App
