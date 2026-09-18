import { useEffect, useMemo } from 'react'
import { ReactFlow, Background, Controls, MarkerType, useNodesState, type Node, type Edge } from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { ServiceNode, type ServiceNodeData } from './ServiceNode'
import { resolveServiceId } from '../utils/resolveServiceId'
import { isTrafficFlowEdge } from '../utils/trafficFlow'
import type { ArchitectureData } from '../types/architecture'
import type { ManagedContainer } from '../types/controlApi'

interface Props {
  data: ArchitectureData
  containers: Record<string, ManagedContainer[]>
  trafficActive: boolean
  onSelectComponent: (componentId: string) => void
  onSelectConnection: (index: number) => void
}

const nodeTypes = { service: ServiceNode }

// All nodes and connections are always shown - there used to be a "scenario" teaching-progression
// filter here (a "1. Minimal stack" / "2. Click tracking" switcher hiding parts of the graph), but
// it turned out to add more confusion (what's hidden right now?) than it removed, so the graph now
// just always draws the whole real topology.
export function Diagram({ data, containers, trafficActive, onSelectComponent, onSelectConnection }: Props) {
  const knownServiceIds = useMemo(() => new Set(Object.keys(containers)), [containers])

  const computedNodes: Node[] = useMemo(
    () =>
      data.components.map((component) => {
        const serviceId = resolveServiceId(component, knownServiceIds)
        const instances = serviceId ? (containers[serviceId] ?? []) : []
        return {
          id: component.id,
          type: 'service',
          position: component.position,
          // Explicit dimensions skip React Flow's async ResizeObserver-based measurement step -
          // a reasonable perf win regardless, and edges need a node's size to compute a path.
          width: 170,
          height: 40,
          data: {
            label: component.name,
            icon: component.icon,
            state: instances[0]?.state,
            instanceCount: instances.length,
          } satisfies ServiceNodeData,
        }
      }),
    [data.components, containers, knownServiceIds],
  )

  const [nodes, setNodes, onNodesChange] = useNodesState<Node>(computedNodes)

  // Re-applies live data (status dot, replica badge) onto whatever's currently rendered without
  // clobbering a position the user dragged - only positions carry over from the previous state,
  // everything else always comes from the fresh computation.
  useEffect(() => {
    setNodes((current) => {
      const positionById = new Map(current.map((n) => [n.id, n.position]))
      return computedNodes.map((n) => ({ ...n, position: positionById.get(n.id) ?? n.position }))
    })
  }, [computedNodes, setNodes])

  const edges: Edge[] = useMemo(
    () =>
      data.connections.map((connection, index) => {
        const isFlowing = trafficActive && isTrafficFlowEdge(connection.from, connection.to)
        return {
          id: `${connection.from}-${connection.to}-${index}`,
          source: connection.from,
          target: connection.to,
          label: connection.label,
          animated: isFlowing,
          zIndex: isFlowing ? 1 : 0,
          style: {
            stroke: isFlowing ? 'var(--accent)' : 'var(--text)',
            strokeWidth: isFlowing ? 3 : 2,
          },
          markerEnd: { type: MarkerType.ArrowClosed, color: isFlowing ? 'var(--accent)' : 'var(--text)' },
        }
      }),
    [data.connections, trafficActive],
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
