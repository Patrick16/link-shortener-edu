import dagre from '@dagrejs/dagre'
import type { ArchComponent, ArchConnection } from '../types/architecture'

// Matches the fixed size Diagram.tsx gives every node (see the comment there on why - skips
// React Flow's async ResizeObserver measurement step).
const NODE_WIDTH = 170
const NODE_HEIGHT = 40

// Hand-picked x/y per component stopped scaling once the graph passed ~15 nodes - every new HA
// cluster (Postgres replicas, Redis Sentinel, Mongo's replica set) meant re-eyeballing the whole
// layout to avoid a fresh overlap. dagre lays the graph out as a layered DAG instead: rankdir "LR"
// reads left-to-right the same direction the request flow already does, and each fan-out (e.g.
// pgcat's three Postgres targets, or redis-master's replicas+sentinels) lands in its own column
// automatically, sized to whatever's actually in the graph.
export function computeLayout(components: ArchComponent[], connections: ArchConnection[]): Record<string, { x: number; y: number }> {
  const graph = new dagre.graphlib.Graph()
  // ranksep is generous specifically because edge labels (e.g. "Streaming replication (WAL)") sit
  // along the horizontal segment between two columns - too little space and the label overlaps the
  // next column's nodes instead of the connector it's actually labeling.
  graph.setGraph({ rankdir: 'LR', nodesep: 70, ranksep: 220, marginx: 20, marginy: 20 })
  graph.setDefaultEdgeLabel(() => ({}))

  for (const component of components) {
    graph.setNode(component.id, { width: NODE_WIDTH, height: NODE_HEIGHT })
  }

  for (const connection of connections) {
    // dagre's graphlib is a plain (non-multi) graph - a second edge between the same pair just
    // overwrites the first for layout purposes, which is fine here: layout only needs to know the
    // two nodes are connected, not how many distinct labeled connections React Flow will draw
    // between them.
    graph.setEdge(connection.from, connection.to)
  }

  dagre.layout(graph)

  const positions: Record<string, { x: number; y: number }> = {}
  for (const component of components) {
    const node = graph.node(component.id)
    // dagre positions are the node's center; React Flow positions are the top-left corner.
    positions[component.id] = { x: node.x - NODE_WIDTH / 2, y: node.y - NODE_HEIGHT / 2 }
  }

  return positions
}
