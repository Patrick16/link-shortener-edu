import { useEffect, useMemo } from 'react'
import { ReactFlow, Background, Controls, MarkerType, useNodesState, type Node, type Edge } from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { ServiceNode, type ServiceNodeData } from './ServiceNode'
import { resolveServiceId } from '../utils/resolveServiceId'
import { isTrafficFlowEdge } from '../utils/trafficFlow'
import { computeLayout } from '../utils/layoutGraph'
import type { ArchitectureData } from '../types/architecture'
import type { ManagedContainer } from '../types/controlApi'

interface Props {
  data: ArchitectureData
  containers: Record<string, ManagedContainer[]>
  roles: Record<string, string>
  trafficActive: boolean
  selectedConnectionIndex: number | null
  onSelectComponent: (componentId: string) => void
  onSelectConnection: (index: number) => void
}

const nodeTypes = { service: ServiceNode }

// All nodes and connections are always shown - there used to be a "scenario" teaching-progression
// filter here (a "1. Minimal stack" / "2. Click tracking" switcher hiding parts of the graph), but
// it turned out to add more confusion (what's hidden right now?) than it removed, so the graph now
// just always draws the whole real topology.
export function Diagram({ data, containers, roles, trafficActive, selectedConnectionIndex, onSelectComponent, onSelectConnection }: Props) {
  const knownServiceIds = useMemo(() => new Set(Object.keys(containers)), [containers])

  // Positions come from dagre, not hand-authored coordinates in architecture.json - see
  // layoutGraph.ts for why hand-picking x/y stopped scaling once the graph passed ~15 nodes. This
  // only depends on the static component/connection lists, not live container data, so it computes
  // once per mount in practice rather than on every status poll.
  const layout = useMemo(() => computeLayout(data.components, data.connections), [data.components, data.connections])

  const computedNodes: Node[] = useMemo(
    () =>
      data.components.map((component) => {
        const serviceId = resolveServiceId(component, knownServiceIds)
        const instances = serviceId ? (containers[serviceId] ?? []) : []
        return {
          id: component.id,
          type: 'service',
          position: layout[component.id] ?? { x: 0, y: 0 },
          // Explicit dimensions skip React Flow's async ResizeObserver-based measurement step -
          // a reasonable perf win regardless, and edges need a node's size to compute a path.
          width: 170,
          height: 40,
          data: {
            label: component.name,
            icon: component.icon,
            state: instances[0]?.state,
            instanceCount: instances.length,
            role: roles[component.id],
          } satisfies ServiceNodeData,
        }
      }),
    [data.components, containers, knownServiceIds, layout, roles],
  )

  const [nodes, setNodes, onNodesChange] = useNodesState<Node>(computedNodes)

  // Re-applies live data (status dot, replica badge, live role badge) onto whatever's currently
  // rendered without clobbering a position the user dragged - only positions carry over from the
  // previous state, everything else always comes from the fresh computation.
  useEffect(() => {
    setNodes((current) => {
      const positionById = new Map(current.map((n) => [n.id, n.position]))
      return computedNodes.map((n) => ({ ...n, position: positionById.get(n.id) ?? n.position }))
    })
  }, [computedNodes, setNodes])

  const edges: Edge[] = useMemo(
    () =>
      data.connections.map((connection, index) => {
        const isFlowing = trafficActive && isTrafficFlowEdge(connection.from, connection.to, roles)
        const isSelected = selectedConnectionIndex === index
        const isHighlighted = isFlowing || isSelected
        return {
          id: `${connection.from}-${connection.to}-${index}`,
          source: connection.from,
          target: connection.to,
          // Orthogonal, right-angle routing instead of the default bezier curve - with this many
          // nodes, curved edges crossing at odd angles were a big part of why the graph read as a
          // tangle rather than a topology.
          type: 'step',
          label: connection.label,
          animated: isFlowing,
          // A selected edge always renders above every other edge (including a flowing one it may
          // overlap with) so clicking it in a dense tangle actually brings it forward, not just
          // marks it.
          zIndex: isSelected ? 2 : isFlowing ? 1 : 0,
          style: {
            stroke: isHighlighted ? 'var(--accent)' : 'var(--text)',
            strokeWidth: isSelected ? 4 : isFlowing ? 3 : 2,
            filter: isSelected ? 'drop-shadow(0 0 4px var(--accent-border))' : undefined,
          },
          markerEnd: { type: MarkerType.ArrowClosed, color: isHighlighted ? 'var(--accent)' : 'var(--text)' },
          // React Flow's edge label default is an opaque white pill - replace it with the app's own
          // dark surface + hairline border so it reads on the dark canvas instead of standing out as
          // a stray light rectangle. A selected edge's label gets the same accent treatment as its
          // line so the two read as one highlighted unit.
          labelStyle: { fill: isSelected ? 'var(--accent)' : 'var(--text-h)', fontSize: 12, fontWeight: isSelected ? 700 : 500 },
          labelBgStyle: {
            fill: 'var(--code-bg)',
            fillOpacity: 0.92,
            stroke: isSelected ? 'var(--accent)' : 'var(--border)',
            strokeWidth: isSelected ? 1.5 : 1,
          },
          labelBgPadding: [6, 4] as [number, number],
          labelBgBorderRadius: 4,
        }
      }),
    [data.connections, trafficActive, selectedConnectionIndex, roles],
  )

  return (
    <div className="diagram">
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={onNodesChange}
        nodeTypes={nodeTypes}
        onNodeClick={(_, node) => onSelectComponent(node.id)}
        onEdgeClick={(_, edge) => {
          const index = data.connections.findIndex((c, i) => `${c.from}-${c.to}-${i}` === edge.id)
          if (index >= 0) onSelectConnection(index)
        }}
        fitView
      >
        <Background />
        <Controls />
      </ReactFlow>
    </div>
  )
}
