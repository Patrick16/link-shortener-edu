import { useMemo, useState } from 'react'
import './App.css'
import architectureData from './data/architecture.json'
import { useLiveStack } from './hooks/useLiveStack'
import { useTrafficRun } from './hooks/useTrafficRun'
import { useTrafficConfig } from './hooks/useTrafficConfig'
import { useResizableWidth } from './hooks/useResizableWidth'
import { Diagram } from './components/Diagram'
import { NodePanel } from './components/NodePanel'
import { ConnectionDetail } from './components/ConnectionDetail'
import { TrafficConfigPanel } from './components/TrafficConfigPanel'
import { TrafficResultPanel } from './components/TrafficResultPanel'
import { RunHistoryPanel } from './components/RunHistoryPanel'
import { resolveServiceId } from './utils/resolveServiceId'
import type { ArchitectureData } from './types/architecture'

const data = architectureData as unknown as ArchitectureData
const metaById = new Map(data.components.map((c) => [c.id, c]))

type Selection = { kind: 'component'; id: string } | { kind: 'connection'; index: number } | null

// Layout: left sidebar configures the next run, the header shows whichever run is live or just
// finished, the graph owns the center, and the right sidebar is every past run - unless a node or
// connection is selected, in which case it takes over that same slot instead. Only one of "past
// runs" and "this node's detail" is useful at a time, so they share the space rather than each
// getting a fixed spot that's empty half the time.
function App() {
  const { containers, resourceHistory, connected, loading, error } = useLiveStack()
  const trafficRun = useTrafficRun()
  const trafficConfig = useTrafficConfig()
  const [selection, setSelection] = useState<Selection>(null)
  const leftSidebar = useResizableWidth('sidebar-width-left', 360, 260, 640, 1)
  const rightSidebar = useResizableWidth('sidebar-width-right', 360, 260, 640, -1)

  const knownServiceIds = useMemo(() => new Set(Object.keys(containers)), [containers])

  const selectedComponent = selection?.kind === 'component' ? metaById.get(selection.id) : undefined
  const selectedConnection = selection?.kind === 'connection' ? data.connections[selection.index] : undefined
  const selectedServiceId = selectedComponent ? resolveServiceId(selectedComponent, knownServiceIds) : null
  const selectedInstances = selectedServiceId ? (containers[selectedServiceId] ?? []) : []

  return (
    <div className="app-shell">
      <header className="app-header">
        <div className="app-header-title">
          <h1>Architecture Map</h1>
          <p className="connection-status">{connected ? 'Live' : 'Connecting...'}</p>
          {loading && <p>Loading containers...</p>}
          {error && <p className="service-card-error">{error}</p>}
        </div>
        <TrafficResultPanel {...trafficRun} fallbackTotalSeconds={trafficConfig.totalDuration} />
      </header>

      <div className="app-body">
        <aside className="app-sidebar-left" style={{ width: leftSidebar.width }}>
          <TrafficConfigPanel config={trafficConfig} disabled={trafficRun.running} running={trafficRun.running} onRun={() => trafficRun.start(trafficConfig.buildRequest())} />
        </aside>
        <div className="app-resizer" onPointerDown={leftSidebar.onPointerDown} />

        <main className="app-center">
          <Diagram
            data={data}
            containers={containers}
            trafficActive={trafficRun.running}
            selectedConnectionIndex={selection?.kind === 'connection' ? selection.index : null}
            onSelectComponent={(id) => setSelection({ kind: 'component', id })}
            onSelectConnection={(index) => setSelection({ kind: 'connection', index })}
          />
        </main>

        <div className="app-resizer" onPointerDown={rightSidebar.onPointerDown} />
        <aside className="app-sidebar-right" style={{ width: rightSidebar.width }}>
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
          {!selectedComponent && !selectedConnection && <RunHistoryPanel lastSavedRunId={trafficRun.lastSavedRunId} />}
        </aside>
      </div>
    </div>
  )
}

export default App
