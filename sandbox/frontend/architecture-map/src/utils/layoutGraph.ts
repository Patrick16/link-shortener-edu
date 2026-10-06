import dagre from '@dagrejs/dagre'
import type { ArchComponent, ArchConnection } from '../types/architecture'

// Matches the fixed size Diagram.tsx gives every node (see the comment there on why - skips
// React Flow's async ResizeObserver measurement step). Exported so Diagram.tsx imports these
// instead of duplicating the literals - the two used to just say "keep in sync" in a comment on
// each side, with nothing enforcing it: a resize on one side with no matching edit on the other
// left dagre laying out with a stale half-width, so every node rendered visibly off-center from
// the edges dagre routed for it, with no compiler or test error to flag the mismatch.
export const NODE_WIDTH = 180
export const NODE_HEIGHT = 46

// Hand-picked x/y per component stopped scaling once the graph passed ~15 nodes - every new HA
// cluster (Postgres replicas, Redis Sentinel, Mongo's replica set) meant re-eyeballing the whole
// layout to avoid a fresh overlap. dagre lays the graph out as a layered DAG instead: rankdir "LR"
// reads left-to-right the same direction the request flow already does, and each fan-out (e.g.
// pgcat's three Postgres targets, or redis-master's replicas+sentinels) lands in its own column
// automatically, sized to whatever's actually in the graph.
export function computeLayout(components: ArchComponent[], connections: ArchConnection[]): Record<string, { x: number; y: number }> {
  const graph = new dagre.graphlib.Graph()
  // Compact on purpose: labels are hidden until hover/selection now (see Diagram.tsx's edge CSS),
  // so they no longer need standing room between columns, and a smaller overall canvas means
  // fitView doesn't have to zoom out as far - every per-node Handle offset (see ServiceNode's
  // multi-handle fan-out) stays a bigger fraction of a screen pixel at the zoom level people
  // actually look at the graph at, instead of shrinking into an indistinguishable single line the
  // way it did at the previous, more zoomed-out size.
  graph.setGraph({ rankdir: 'LR', nodesep: 55, ranksep: 150, marginx: 20, marginy: 20 })
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
