import dagre from '@dagrejs/dagre'
import type { ArchComponent, ArchConnection } from '../types/architecture'
import { REGIONS, getRegionId } from './regions'

// Matches the fixed size Diagram.tsx gives every node (see the comment there on why - skips
// React Flow's async ResizeObserver measurement step). Exported so Diagram.tsx imports these
// instead of duplicating the literals - the two used to just say "keep in sync" in a comment on
// each side, with nothing enforcing it: a resize on one side with no matching edit on the other
// left dagre laying out with a stale half-width, so every node rendered visibly off-center from
// the edges dagre routed for it, with no compiler or test error to flag the mismatch.
export const NODE_WIDTH = 200
export const NODE_HEIGHT = 72

export interface RegionBox {
  id: string
  label: string
  x: number
  y: number
  width: number
  height: number
}

export interface LayoutResult {
  positions: Record<string, { x: number; y: number }>
  regions: RegionBox[]
  // Dagre's own full routing points for each edge (keyed "from->to"), including its own guess at
  // the source/target endpoints - OrthogonalEdge.tsx prepends/appends the edge's real per-handle
  // coordinates rather than relying on those. Only populated (non-empty) for an edge whose two
  // ends share a region (see the big comment below for why) - [] for everything else.
  edgePaths: Record<string, { x: number; y: number }[]>
}

// Extra room around a region's own content, on top of the nodesep/ranksep already between its
// members - this is what turns each region into a visibly separate box instead of its boundary
// touching the nodes right at the edge. Unlike an earlier version of this function, these no
// longer need to stay under nodesep/ranksep to avoid two regions overlapping - see the two-pass
// layout below, which makes that impossible by construction instead of by careful tuning.
const REGION_PADDING = 20
const REGION_LABEL_HEIGHT = 24

export function edgeKey(from: string, to: string): string {
  return `${from}->${to}`
}

interface SubLayout {
  // Relative to this sub-layout's own (0, 0) - the caller offsets these by wherever the macro
  // pass below decided this group's box goes.
  positions: Record<string, { x: number; y: number }>
  edgePaths: Record<string, { x: number; y: number }[]>
  width: number
  height: number
}

// Lays out one group's own members and internal edges in total isolation from the rest of the
// graph - no other group's nodes exist in this dagre call at all, which is what guarantees this
// group's bounding box is exactly its own content plus margins, nothing more. Cross-group edges
// are handled entirely separately (see computeLayout) since they can't be laid out until every
// group's own size - and so its macro position - is known.
function layoutSubgraph(members: ArchComponent[], internalConnections: ArchConnection[]): SubLayout {
  const graph = new dagre.graphlib.Graph()
  graph.setGraph({ rankdir: 'LR', nodesep: 36, ranksep: 90, marginx: 0, marginy: 0 })
  graph.setDefaultEdgeLabel(() => ({}))

  for (const member of members) {
    graph.setNode(member.id, { width: NODE_WIDTH, height: NODE_HEIGHT })
  }
  for (const connection of internalConnections) {
    graph.setEdge(connection.from, connection.to)
  }

  dagre.layout(graph)

  // dagre centers the whole layout around (marginx, marginy) regardless of what those are, so
  // normalize to this sub-layout's own top-left being (0, 0) - the caller adds back whatever
  // absolute offset this group ends up at.
  let minX = Infinity
  let minY = Infinity
  let maxX = -Infinity
  let maxY = -Infinity
  for (const member of members) {
    const node = graph.node(member.id)
    minX = Math.min(minX, node.x - NODE_WIDTH / 2)
    minY = Math.min(minY, node.y - NODE_HEIGHT / 2)
    maxX = Math.max(maxX, node.x + NODE_WIDTH / 2)
    maxY = Math.max(maxY, node.y + NODE_HEIGHT / 2)
  }
  if (members.length === 0) {
    minX = minY = maxX = maxY = 0
  }

  const positions: Record<string, { x: number; y: number }> = {}
  for (const member of members) {
    const node = graph.node(member.id)
    positions[member.id] = { x: node.x - NODE_WIDTH / 2 - minX, y: node.y - NODE_HEIGHT / 2 - minY }
  }

  const edgePaths: Record<string, { x: number; y: number }[]> = {}
  for (const connection of internalConnections) {
    const edge = graph.edge({ v: connection.from, w: connection.to })
    // The full point list, endpoints included - not just the interior ones. Dropping dagre's own
    // first/last points looked right (they're close to the node boundary, which Diagram.tsx
    // substitutes with the edge's actual per-handle coordinate anyway) but for a short edge
    // (adjacent ranks, 3 points total) the *last* point before the target is also exactly where
    // dagre put the lane-separating divergence for a converging fan-in - dropping it collapsed
    // several edges back down to sharing one x right where they needed to differ most. Keeping
    // every point and letting OrthogonalEdge prepend/append the real handle coordinates instead
    // just adds one small extra corner near each end, not a problem.
    edgePaths[edgeKey(connection.from, connection.to)] = edge.points.map((p: { x: number; y: number }) => ({
      x: p.x - minX,
      y: p.y - minY,
    }))
  }

  return { positions, edgePaths, width: maxX - minX, height: maxY - minY }
}

// Hand-picked x/y per component stopped scaling once the graph passed ~15 nodes - every new HA
// cluster (Postgres replicas, Redis Sentinel, Mongo's replica set) meant re-eyeballing the whole
// layout to avoid a fresh overlap. dagre lays the graph out as a layered DAG instead: rankdir "LR"
// reads left-to-right the same direction the request flow already does, and each fan-out (e.g.
// pgcat's three Postgres targets, or redis-master's replicas+sentinels) lands in its own column
// automatically, sized to whatever's actually in the graph.
//
// Two-pass layout, not one big compound/clustered dagre graph: a single compound graph was tried
// first, but dagre's clustering only pulls a cluster's members closer together in *crossing order*
// - it does nothing to stop a region whose members span several ranks (redis-master's sentinels,
// master and replicas are three different rank depths, not one) from spatially overlapping a
// neighboring region that happens to share part of that rank range. No amount of padding/nodesep
// tuning fixes that, since the two regions' dagre-computed boxes can overlap by more than any
// reasonable margin regardless of spacing (verified empirically up to nodesep=250, still
// overlapping). The fix is structural instead:
//   1. layoutSubgraph (above) lays out each region's own members+internal edges in complete
//      isolation, giving an exact size for that region's content alone.
//   2. A small second dagre pass below treats each region as a single opaque box of that size (no
//      compound/clustering involved) and positions those boxes relative to each other, same as it
//      would position any other set of same-sized rectangles - which is what actually guarantees
//      no two boxes can overlap, by construction, not by hoping the margins were generous enough.
//   3. Every real node's final position is its region's macro offset plus its own position from
//      step 1.
// Cross-region edges (e.g. link-api in API to redis-master in Cache) don't get dagre's own
// lane-separating interior points this way - only an edge whose both ends share a region does (see
// edgePaths below) - but every reported case of several edges visually merging together was
// exactly that: edges staying entirely inside one region (pgcat → its own DBs/replicas,
// sentinels → master), never a cross-region fan-out, so this covers what actually needed it.
export function computeLayout(components: ArchComponent[], connections: ArchConnection[]): LayoutResult {
  const presentComponentIds = new Set(components.map((c) => c.id))
  const visibleConnections = connections.filter(
    (connection) => presentComponentIds.has(connection.from) && presentComponentIds.has(connection.to),
  )

  // Group every component by region - anything without one (shouldn't happen for a real
  // architecture.json component, but kept generic rather than assuming) becomes its own
  // single-member "group" with no visible box, so the two-pass machinery below has no special
  // case for an unregioned node.
  const groupMembers = new Map<string, ArchComponent[]>()
  const groupIdOf = new Map<string, string>()
  for (const component of components) {
    const groupId = getRegionId(component.id) ?? component.id
    groupIdOf.set(component.id, groupId)
    const members = groupMembers.get(groupId)
    if (members) members.push(component)
    else groupMembers.set(groupId, [component])
  }

  const subLayouts = new Map<string, SubLayout>()
  for (const [groupId, members] of groupMembers) {
    const memberIds = new Set(members.map((m) => m.id))
    const internal = visibleConnections.filter((c) => memberIds.has(c.from) && memberIds.has(c.to))
    subLayouts.set(groupId, layoutSubgraph(members, internal))
  }

  // A group that corresponds to a real region gets padding + label clearance reserved in its
  // macro box; a single-member pseudo-group for an unregioned component doesn't (there's no
  // visible RegionNode for it to draw, see the regions loop near the end).
  const regionIds = new Set(REGIONS.map((r) => r.id))
  function macroBoxSize(groupId: string): { width: number; height: number } {
    const sub = subLayouts.get(groupId)!
    if (!regionIds.has(groupId)) return { width: sub.width, height: sub.height }
    return { width: sub.width + REGION_PADDING * 2, height: sub.height + REGION_PADDING * 2 + REGION_LABEL_HEIGHT }
  }

  const macroGraph = new dagre.graphlib.Graph()
  macroGraph.setGraph({ rankdir: 'LR', nodesep: 36, ranksep: 70, marginx: 20, marginy: 20 })
  macroGraph.setDefaultEdgeLabel(() => ({}))
  for (const groupId of groupMembers.keys()) {
    macroGraph.setNode(groupId, macroBoxSize(groupId))
  }
  for (const connection of visibleConnections) {
    const from = groupIdOf.get(connection.from)!
    const to = groupIdOf.get(connection.to)!
    if (from !== to) macroGraph.setEdge(from, to)
  }
  dagre.layout(macroGraph)

  const positions: Record<string, { x: number; y: number }> = {}
  const regions: RegionBox[] = []
  for (const [groupId, members] of groupMembers) {
    const macroNode = macroGraph.node(groupId)
    const isRegion = regionIds.has(groupId)
    const boxX = macroNode.x - macroNode.width / 2
    const boxY = macroNode.y - macroNode.height / 2
    const contentOffsetX = boxX + (isRegion ? REGION_PADDING : 0)
    const contentOffsetY = boxY + (isRegion ? REGION_PADDING + REGION_LABEL_HEIGHT : 0)

    const sub = subLayouts.get(groupId)!
    for (const member of members) {
      const rel = sub.positions[member.id]
      positions[member.id] = { x: contentOffsetX + rel.x, y: contentOffsetY + rel.y }
    }

    if (isRegion) {
      const region = REGIONS.find((r) => r.id === groupId)!
      regions.push({ id: region.id, label: region.label, x: boxX, y: boxY, width: macroNode.width, height: macroNode.height })
    }
  }
  // Keep REGIONS' own declared order (clients → lb → api → ... ) rather than Map iteration order,
  // so consumers that care about reading order (none today, but cheap to guarantee) get it.
  regions.sort((a, b) => REGIONS.findIndex((r) => r.id === a.id) - REGIONS.findIndex((r) => r.id === b.id))

  const edgePaths: Record<string, { x: number; y: number }[]> = {}
  for (const connection of visibleConnections) {
    const key = edgeKey(connection.from, connection.to)
    const groupId = groupIdOf.get(connection.from)!
    // Cross-group edges get no interior waypoints - an explicit [], not a missing key, so
    // Diagram.tsx/OrthogonalEdge can treat "this connection" and "this connection has no lane
    // data" uniformly rather than needing an extra has-own-property check.
    if (groupId !== groupIdOf.get(connection.to)) {
      edgePaths[key] = []
      continue
    }
    const macroNode = macroGraph.node(groupId)
    const isRegion = regionIds.has(groupId)
    const contentOffsetX = macroNode.x - macroNode.width / 2 + (isRegion ? REGION_PADDING : 0)
    const contentOffsetY = macroNode.y - macroNode.height / 2 + (isRegion ? REGION_PADDING + REGION_LABEL_HEIGHT : 0)
    const relPoints = subLayouts.get(groupId)!.edgePaths[key] ?? []
    edgePaths[key] = relPoints.map((p) => ({ x: p.x + contentOffsetX, y: p.y + contentOffsetY }))
  }

  return { positions, regions, edgePaths }
}
