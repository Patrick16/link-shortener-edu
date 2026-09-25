import { useMemo, useState } from 'react'
import './App.css'
import architectureData from './data/architecture.json'
import { useLiveStack } from './hooks/useLiveStack'
import { useTrafficRun } from './hooks/useTrafficRun'
import { useTrafficConfig } from './hooks/useTrafficConfig'
import { useResizableWidth } from './hooks/useResizableWidth'
import { useInfraTopology } from './hooks/useInfraTopology'
import { usePinnedMetrics } from './hooks/usePinnedMetrics'
import { Diagram } from './components/Diagram'
import { NodePanel } from './components/NodePanel'
import { PinnedMetrics } from './components/PinnedMetrics'
import { ConnectionDetail } from './components/ConnectionDetail'
import { K6ConfigPanel } from './components/K6ConfigPanel'
import { TrafficResultPanel } from './components/TrafficResultPanel'
import { RunHistoryPanel } from './components/RunHistoryPanel'
import { resolveServiceId } from './utils/resolveServiceId'
import type { ArchitectureData } from './types/architecture'

const data = architectureData as unknown as ArchitectureData
const metaById = new Map(data.components.map((c) => [c.id, c]))

type Selection = { kind: 'component'; id: string } | { kind: 'connection'; index: number } | null

// Layout: the header shows whichever run is live or just finished, the graph owns the center, and
// the (single, left) sidebar is a selection detail panel - a clicked node's own controls, a clicked
// connection's info, or every past run when nothing's selected, since only one of those is ever
// useful at a time. There's no permanently-visible config sidebar any more: k6 has no live
// status/controls of its own (see K6ConfigPanel), so selecting it is how you configure the next
// run instead - the same "click the node" pattern every other control here already uses.
//
// Collapsed by default on a fresh visit (the graph itself is the point on first load, not a panel
// with nothing selected yet) - but selecting anything on the graph force-expands it via
// sidebar.expand(), so the very next click after landing on an empty graph still shows that node's
// detail immediately instead of silently updating an unopened panel.
function App() {
  const { containers, resourceHistory, connected, loading, error } = useLiveStack()
  const roles = useInfraTopology()
  const trafficRun = useTrafficRun()
  const trafficConfig = useTrafficConfig()
  const pins = usePinnedMetrics()
  const [selection, setSelection] = useState<Selection>(null)
  const sidebar = useResizableWidth('sidebar-width-left', 360, 260, 640, 1, true)

  const knownServiceIds = useMemo(() => new Set(Object.keys(containers)), [containers])

  const selectedComponent = selection?.kind === 'component' ? metaById.get(selection.id) : undefined
  const selectedConnection = selection?.kind === 'connection' ? data.connections[selection.index] : undefined
  const selectedServiceId = selectedComponent ? resolveServiceId(selectedComponent, knownServiceIds) : null
  const selectedInstances = selectedServiceId ? (containers[selectedServiceId] ?? []) : []

  return (
    <div className="app-shell">
      <PinnedMetrics
        pinnedIds={pins.pinnedIds}
        metaById={metaById}
        knownServiceIds={knownServiceIds}
        resourceHistory={resourceHistory}
        onUnpin={pins.togglePin}
      />
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
        <aside className="app-sidebar-left" style={{ width: sidebar.width, padding: sidebar.collapsed ? 0 : undefined }}>
          {!sidebar.collapsed && (
            <>
              {selectedComponent?.id === 'k6' && (
                <K6ConfigPanel
                  component={selectedComponent}
                  config={trafficConfig}
                  disabled={trafficRun.running}
                  running={trafficRun.running}
                  onRun={() => trafficRun.start(trafficConfig.buildRequest())}
                  onClose={() => setSelection(null)}
                />
              )}
              {selectedComponent && selectedComponent.id !== 'k6' && selectedServiceId && (
                <NodePanel
                  component={selectedComponent}
                  serviceId={selectedServiceId}
                  instances={selectedInstances}
                  resourceHistory={resourceHistory[selectedServiceId] ?? []}
                  onClose={() => setSelection(null)}
                  pinned={pins.isPinned(selectedComponent.id)}
                  onTogglePin={() => pins.togglePin(selectedComponent.id)}
                />
              )}
              {selectedComponent && selectedComponent.id !== 'k6' && !selectedServiceId && (
                <NodePanel component={selectedComponent} serviceId={null} instances={[]} resourceHistory={[]} onClose={() => setSelection(null)} />
              )}
              {selectedConnection && <ConnectionDetail connection={selectedConnection} onClose={() => setSelection(null)} />}
              {!selectedComponent && !selectedConnection && <RunHistoryPanel lastSavedRunId={trafficRun.lastSavedRunId} />}
            </>
          )}
        </aside>
        <div className="app-resizer" onPointerDown={sidebar.onPointerDown}>
          <button
            type="button"
            className="app-resizer-toggle"
            onClick={(e) => {
              e.stopPropagation()
              sidebar.toggleCollapsed()
            }}
            onPointerDown={(e) => e.stopPropagation()}
            aria-label={sidebar.collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            title={sidebar.collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          >
            {sidebar.collapsed ? '›' : '‹'}
          </button>
        </div>

        <main className="app-center">
          <Diagram
            data={data}
            containers={containers}
            roles={roles}
            trafficActive={trafficRun.running}
            selectedConnectionIndex={selection?.kind === 'connection' ? selection.index : null}
            onSelectComponent={(id) => {
              setSelection({ kind: 'component', id })
              sidebar.expand()
            }}
            onSelectConnection={(index) => {
              setSelection({ kind: 'connection', index })
              sidebar.expand()
            }}
          />
        </main>
      </div>
    </div>
  )
}

export default App
