import { useEffect, useMemo, useRef } from 'react'
import { ReactFlow, ReactFlowProvider, Background, Controls, Panel, MarkerType, useNodesState, useReactFlow, type Node, type Edge } from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { ServiceNode, type ServiceNodeData } from './ServiceNode'
import { RegionNode, type RegionNodeData } from './RegionNode'
import { OrthogonalEdge, type OrthogonalEdgeData } from './OrthogonalEdge'
import { PinnedMetrics } from './PinnedMetrics'
import { resolveServiceId } from '../utils/resolveServiceId'
import { isTrafficFlowEdge } from '../utils/trafficFlow'
import { computeLayout, edgeKey, NODE_HEIGHT, NODE_WIDTH } from '../utils/layoutGraph'
import { filterArchitecture, type VisibleConnection } from '../utils/topologyFilter'
import { getRoleBadges } from '../utils/roleBadges'
import type { ArchComponent, ArchitectureData } from '../types/architecture'
import type { InfraStatus, ManagedContainer, ResourceSample } from '../types/controlApi'

interface Props {
  data: ArchitectureData
  containers: Record<string, ManagedContainer[]>
  roles: Record<string, string>
  trafficActive: boolean
  infraStatus: InfraStatus | null
  selectedConnectionIndex: number | null
  onSelectComponent: (componentId: string) => void
  onSelectConnection: (index: number) => void
  metaById: Map<string, ArchComponent>
  pinnedIds: string[]
  resourceHistoryByContainer: Record<string, ResourceSample[]>
  onUnpinMetric: (componentId: string) => void
}

const nodeTypes = { service: ServiceNode, region: RegionNode }
const edgeTypes = { orthogonal: OrthogonalEdge }

// There used to be a "scenario" teaching-progression filter here (a "1. Minimal stack" /
// "2. Click tracking" switcher hiding parts of the graph), and it was removed because hiding
// future-curriculum steps caused "what's hidden right now?" confusion. The filter below is a
// different axis, not a reincarnation of that one: it reflects the *actual current infra config*
// (nginx bypass / pgcat enabled / cache enabled / messaging transport), driven entirely by the
// Topology panel's own checkboxes (see TopologyPanel.tsx) - there's never ambiguity about what's
// hidden, since the panel's visible state is the answer. See `activeWhen` in architecture.json and
// utils/topologyFilter.ts for the actual filtering logic.
//
// A node hidden by this filter loses its dragged position - it's absent from useNodesState's
// position-preservation map (below) while hidden, so it snaps to a fresh dagre position if later
// re-shown rather than remembering where it was dragged to. The same reset also applies to a node
// that stays visible but whose own edge set changes (nginx is the one case today - it can lose its
// HTTP edges while keeping its gRPC fan-out ones, or vice versa): dragged-position preservation is
// keyed off whether the dagre layout itself changed, not just whether the node was ever hidden -
// see the layoutRef comment below. Acceptable for a filter that's meant to declutter, not to
// preserve manual layout across toggles.
//
// Wrapped below in its own ReactFlowProvider (Diagram, the exported component) because this inner
// component calls useReactFlow() to imperatively re-fit the viewport on a filter-driven node-count
// change - that hook needs a provider ABOVE the <ReactFlow> element, not just the one <ReactFlow>
// establishes internally for its own descendants.
function DiagramInner({
  data,
  containers,
  roles,
  trafficActive,
  infraStatus,
  selectedConnectionIndex,
  onSelectComponent,
  onSelectConnection,
  metaById,
  pinnedIds,
  resourceHistoryByContainer,
  onUnpinMetric,
}: Props) {
  const knownServiceIds = useMemo(() => new Set(Object.keys(containers)), [containers])
  const { fitView } = useReactFlow()

  const visible = useMemo(() => filterArchitecture(data, infraStatus), [data, infraStatus])

  // Groups each node's edges by which side they attach to, in the same order they'll be drawn in
  // - ServiceNode renders one Handle per entry here, spread evenly across the node's height,
  // instead of every edge converging on one shared point. That convergence was the main reason
  // edges visually merged into an undifferentiated bundle wherever several of them ran parallel
  // for a stretch (e.g. pgcat's 5 incoming + 5 outgoing, or redis-master's sentinel/replica fan).
  const edgesByNode = useMemo(() => {
    const outgoing = new Map<string, VisibleConnection[]>()
    const incoming = new Map<string, VisibleConnection[]>()
    for (const connection of visible.connections) {
      const fromList = outgoing.get(connection.from)
      if (fromList) fromList.push(connection)
      else outgoing.set(connection.from, [connection])
      const toList = incoming.get(connection.to)
      if (toList) toList.push(connection)
      else incoming.set(connection.to, [connection])
    }
    return { outgoing, incoming }
  }, [visible.connections])

  // Positions come from dagre, not hand-authored coordinates in architecture.json - see
  // layoutGraph.ts for why hand-picking x/y stopped scaling once the graph passed ~15 nodes. This
  // only depends on the currently-visible component/connection lists, so a toggle-driven filter
  // change re-lays-out the remaining nodes without leaving gaps where hidden ones used to be.
  const layout = useMemo(() => computeLayout(visible.components, visible.connections), [visible.components, visible.connections])

  // Background swimlane boxes (see layoutGraph.ts's compound dagre layout + utils/regions.ts) -
  // zIndex -1 and non-interactive so they never intercept a click meant for a real node sitting on
  // top, and listed first in computedNodes below so they paint behind everything else regardless.
  const regionNodes: Node[] = useMemo(
    () =>
      layout.regions.map((region) => ({
        id: region.id,
        type: 'region',
        position: { x: region.x, y: region.y },
        width: region.width,
        height: region.height,
        zIndex: -1,
        draggable: false,
        selectable: false,
        connectable: false,
        data: { label: region.label } satisfies RegionNodeData,
      })),
    [layout.regions],
  )

  const serviceNodes: Node[] = useMemo(
    () =>
      visible.components.map((component) => {
        const serviceId = resolveServiceId(component, knownServiceIds)
        const instances = serviceId ? (containers[serviceId] ?? []) : []
        return {
          id: component.id,
          type: 'service',
          position: layout.positions[component.id] ?? { x: 0, y: 0 },
          // Explicit dimensions skip React Flow's async ResizeObserver-based measurement step -
          // a reasonable perf win regardless, and edges need a node's size to compute a path.
          // Shared with layoutGraph.ts's own NODE_WIDTH/NODE_HEIGHT, not duplicated - dagre lays
          // out using the exact same size React Flow renders each node at.
          width: NODE_WIDTH,
          height: NODE_HEIGHT,
          data: {
            label: component.name,
            icon: component.icon,
            state: instances[0]?.state,
            instanceCount: instances.length,
            role: roles[component.id],
            roleBadges: getRoleBadges(component.id, infraStatus),
            sourceHandleCount: edgesByNode.outgoing.get(component.id)?.length ?? 1,
            targetHandleCount: edgesByNode.incoming.get(component.id)?.length ?? 1,
          } satisfies ServiceNodeData,
        }
      }),
    [visible.components, containers, knownServiceIds, layout, roles, infraStatus, edgesByNode],
  )

  const computedNodes: Node[] = useMemo(() => [...regionNodes, ...serviceNodes], [regionNodes, serviceNodes])

  const [nodes, setNodes, onNodesChange] = useNodesState<Node>(computedNodes)

  // Tracks the dagre layout actually in effect, not just whether computedNodes changed -
  // computedNodes also changes on every live container/role update, which must NOT reset a
  // dragged position, but a topology change (a toggle in the Topology panel hiding one node's
  // edges without hiding the node itself, e.g. nginx keeping only its gRPC fan-out edges once
  // nginxBypassed flips) recomputes `layout` with a different reference, and that case SHOULD
  // override a stale dragged position - otherwise a node that stays visible across a filter
  // change keeps rendering wherever dagre last put it for its OLD edge set, nowhere near its
  // new neighbors.
  const layoutRef = useRef(layout)

  // Re-applies live data (status dot, replica badge, live role badge) onto whatever's currently
  // rendered without clobbering a position the user dragged - only positions carry over from the
  // previous state, everything else always comes from the fresh computation. A node the filter
  // just hid is simply absent from computedNodes and drops out here too; one re-shown later is
  // absent from `current`, so it has no previous position to carry over (see header comment).
  useEffect(() => {
    const layoutChanged = layoutRef.current !== layout
    layoutRef.current = layout
    setNodes((current) => {
      if (layoutChanged) return computedNodes
      const positionById = new Map(current.map((n) => [n.id, n.position]))
      return computedNodes.map((n) => ({ ...n, position: positionById.get(n.id) ?? n.position }))
    })
  }, [computedNodes, layout, setNodes])

  // The visible node count only changes when a toggle in the Topology panel flips (not on every
  // live container-status poll, since that never adds/removes components) - re-fit the viewport
  // then, since React Flow's own `fitView` prop only fits once on initial mount.
  useEffect(() => {
    fitView({ duration: 300 })
  }, [visible.components.length, fitView])

  const edges: Edge[] = useMemo(
    () =>
      visible.connections.map((connection) => {
        const index = connection.originalIndex
        const isFlowing = trafficActive && isTrafficFlowEdge(connection.from, connection.to, roles)
        const isSelected = selectedConnectionIndex === index
        const isHighlighted = isFlowing || isSelected
        // Which of the node's several Handles (see edgesByNode above) this specific edge attaches
        // to - its position within the same from/to-grouped list ServiceNode used to decide how
        // many Handles to render, so the two always agree on indices.
        const outgoingSiblings = edgesByNode.outgoing.get(connection.from) ?? [connection]
        const incomingSiblings = edgesByNode.incoming.get(connection.to) ?? [connection]
        const sourceHandleIndex = outgoingSiblings.indexOf(connection)
        const targetHandleIndex = incomingSiblings.indexOf(connection)
        // Only matters for a cross-region edge (OrthogonalEdge falls back to a synthetic midpoint
        // x when it has no dagre waypoints of its own - see that file) - nudges that midpoint so
        // several such edges converging on the same node don't all land on the exact same x and
        // merge back into one line. Keyed off whichever end actually has more than one sibling
        // edge (usually the target, since fan-in is the common shape here) so a plain 1-in-1-out
        // edge gets no nudge at all.
        const laneCount = incomingSiblings.length > 1 ? incomingSiblings.length : outgoingSiblings.length
        const laneIndex = incomingSiblings.length > 1 ? targetHandleIndex : sourceHandleIndex
        const laneOffset = laneCount > 1 ? (laneIndex - (laneCount - 1) / 2) * 16 : 0
        return {
          id: `${connection.from}-${connection.to}-${index}`,
          source: connection.from,
          target: connection.to,
          sourceHandle: `source-${sourceHandleIndex}`,
          targetHandle: `target-${targetHandleIndex}`,
          // Custom orthogonal routing via dagre's own interior waypoints (see
          // layoutGraph.ts's computeLayout and OrthogonalEdge.tsx) instead of React Flow's
          // built-in 'step' type, which only ever looks at one edge's own two endpoints - several
          // edges running through the same column gap in parallel used to collapse into one
          // indistinguishable bundle for most of their length because of that; dagre's own
          // edge-routing points already keep them in separate lanes.
          type: 'orthogonal',
          data: { wayPoints: layout.edgePaths[edgeKey(connection.from, connection.to)], laneOffset } satisfies OrthogonalEdgeData,
          label: connection.label,
          animated: isFlowing,
          // Drives the CSS `.selected` class react-flow adds to the edge's own <g> (not used for
          // react-flow's built-in multi-select, which this app doesn't use) - that class plus
          // `.animated` (already set via `animated` above) are what the edge-label CSS in App.css
          // hooks into to reveal a label on hover/selection/traffic-flow. See that CSS for why:
          // with ~40 edges, every label visible all the time buried the lines themselves under a
          // wall of overlapping text pills - far more readable to show a label only for the one
          // edge you're actually looking at.
          selected: isSelected,
          // A selected edge always renders above every other edge (including a flowing one it may
          // overlap with) so clicking it in a dense tangle actually brings it forward, not just
          // marks it.
          zIndex: isSelected ? 2 : isFlowing ? 1 : 0,
          style: {
            stroke: isHighlighted ? 'var(--accent)' : 'var(--text)',
            strokeWidth: isSelected ? 4 : isFlowing ? 3 : 1.75,
            filter: isSelected ? 'drop-shadow(0 0 4px var(--accent-border))' : undefined,
          },
          markerEnd: { type: MarkerType.ArrowClosed, width: 18, height: 18, color: isHighlighted ? 'var(--accent)' : 'var(--text)' },
          // React Flow's edge label default is an opaque white pill - replace it with the app's own
          // dark surface + hairline border so it reads on the dark canvas instead of standing out as
          // a stray light rectangle. A selected edge's label gets the same accent treatment as its
          // line so the two read as one highlighted unit.
          labelStyle: { fill: isSelected ? 'var(--accent)' : 'var(--text-h)', fontSize: 15, fontWeight: isSelected ? 700 : 500 },
          labelBgStyle: {
            fill: 'var(--code-bg)',
            fillOpacity: 0.92,
            stroke: isSelected ? 'var(--accent)' : 'var(--border)',
            strokeWidth: isSelected ? 1.5 : 1,
          },
          labelBgPadding: [7, 5] as [number, number],
          labelBgBorderRadius: 4,
        }
      }),
    [visible.connections, trafficActive, selectedConnectionIndex, roles, edgesByNode, layout],
  )

  return (
    <div className="diagram">
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={onNodesChange}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        onNodeClick={(_, node) => {
          // Region backdrops are selectable: false (see regionNodes above), which already stops
          // react-flow's own click-to-select behavior, but onNodeClick still fires for any node -
          // guard explicitly rather than relying on onSelectComponent to no-op for an unknown id.
          if (node.type === 'region') return
          onSelectComponent(node.id)
        }}
        onEdgeClick={(_, edge) => {
          const index = data.connections.findIndex((c, i) => `${c.from}-${c.to}-${i}` === edge.id)
          if (index >= 0) onSelectConnection(index)
        }}
        fitView
      >
        <Background />
        <Controls />
        {/* A React Flow Panel, not position: fixed on the page - it's anchored to this graph pane
            specifically (survives pan/zoom, scrolls with the pane on narrow layouts) rather than
            floating over the header, where it used to collide with TrafficResultPanel's own
            top-right "Show details" toggle. */}
        <Panel position="top-right">
          <PinnedMetrics
            pinnedIds={pinnedIds}
            metaById={metaById}
            knownServiceIds={knownServiceIds}
            containers={containers}
            resourceHistoryByContainer={resourceHistoryByContainer}
            onUnpin={onUnpinMetric}
          />
        </Panel>
      </ReactFlow>
    </div>
  )
}

export function Diagram(props: Props) {
  return (
    <ReactFlowProvider>
      <DiagramInner {...props} />
    </ReactFlowProvider>
  )
}
