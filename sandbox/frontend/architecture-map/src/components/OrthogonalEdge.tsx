import { BaseEdge, type EdgeProps } from '@xyflow/react'

export interface OrthogonalEdgeData {
  // Dagre's own interior routing points for this edge (see layoutGraph.ts's big comment on why) -
  // empty for a cross-region edge (dagre never saw it - lane separation only applies within one
  // region/group) or a trivial single-rank edge with no interior points of its own.
  wayPoints?: { x: number; y: number }[]
  // Nudges the synthetic midpoint x used when wayPoints is empty, so several same-region-less
  // edges converging on (or fanning out of) the same node don't all pick the exact same x and
  // collapse back into one line for their shared vertical stretch - see Diagram.tsx for how this
  // is computed (handle index within whichever end actually has more than one edge).
  laneOffset?: number
  [key: string]: unknown
}

type Point = { x: number; y: number }

// Minimum length (px) of the mandatory straight stub right at each end of the path - see the two
// forced-direction blocks at the end of buildOrthogonalPoints for why this has to be enforced
// rather than trusted to come out right on its own.
const MIN_STUB = 12

// Builds a fully orthogonal (right-angle only) polyline through `points`, with two directional
// constraints that aren't just aesthetic - they're what makes the arrowhead point the right way:
//   - it must LEAVE the first point moving in +x (the source handle is on the node's right edge,
//     Position.Right - a path leaving any other direction looks like it's coming out of nowhere)
//   - it must ARRIVE at the last point moving in +x too (the target handle is on the node's LEFT
//     edge, Position.Left - SVG's `orient="auto"` rotates the arrowhead to match the path's
//     tangent at that endpoint, so an approach from any direction other than "from the left" spins
//     the arrowhead to point up/down/backwards instead of cleanly into the node).
// A naive "insert one corner per consecutive pair, horizontal-first leaving / vertical-first
// arriving" rule gets both directions right MOST of the time, but dagre's own interior points
// (see layoutGraph.ts) aren't computed to land exactly on a node's boundary - they're placed for
// dagre's own smooth-curve rendering, and can end up a few px to the *wrong* side of the real
// source/target x (even past it). That produced a near-zero or outright backwards exit/entry
// segment - a technically-horizontal sliver pointing the wrong way - which is what actually
// rotated the arrowhead, not a missing corner. Fixed by never trusting dagre's points for the
// stub right at either end: an unconditional, fixed-length stub is inserted leaving the source and
// another entering the target, each guaranteed to move the correct direction by at least
// MIN_STUB regardless of where dagre's (or the synthetic 2-point Z-shape's) own points land -
// whatever interior lane-separating points dagre provided are kept exactly as they were,
// connected to by that stub, not replaced by it.
function buildOrthogonalPoints(points: Point[], laneOffset: number): Point[] {
  const source = points[0]
  const target = points[points.length - 1]
  const interior = points.slice(1, -1)

  const expanded: Point[] = [source, { x: source.x + MIN_STUB, y: source.y }]
  function stepTo(next: Point) {
    const prev = expanded[expanded.length - 1]
    if (prev.x !== next.x && prev.y !== next.y) {
      expanded.push({ x: next.x, y: prev.y })
    }
    expanded.push(next)
  }

  if (interior.length > 0) {
    for (const point of interior) stepTo(point)
  } else if (source.y !== target.y) {
    // No dagre lane data (cross-region edge, or this edge's rank gap has no dummy nodes of its
    // own) - jog through a synthetic middle x, nudged by laneOffset so several such edges sharing
    // a target/source don't all pick the same x and collapse back into one line.
    stepTo({ x: (source.x + target.x) / 2 + laneOffset, y: source.y })
  }

  const beforeEntry = expanded[expanded.length - 1]
  const approachX = Math.min(beforeEntry.x, target.x - MIN_STUB)
  if (beforeEntry.x !== approachX) expanded.push({ x: approachX, y: beforeEntry.y })
  if (expanded[expanded.length - 1].y !== target.y) expanded.push({ x: approachX, y: target.y })
  expanded.push(target)

  return expanded
}

// Label position: the midpoint of the longest straight segment in the path, not just the path's
// bounding-box center - a corner or a short jog near a node is a bad place to drop a label, the
// longest run is usually the one segment actually worth annotating.
function longestSegmentMidpoint(points: Point[]): Point {
  let best = points[0]
  let bestLength = -1
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1]
    const b = points[i]
    const length = Math.abs(a.x - b.x) + Math.abs(a.y - b.y)
    if (length > bestLength) {
      bestLength = length
      best = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
    }
  }
  return best
}

// Custom edge type used everywhere instead of React Flow's built-in 'step' - see
// layoutGraph.ts's computeLayout for why (dagre's own edge-routing points are what keep several
// parallel edges through the same column gap visually distinct instead of collapsing into one
// bundle, something the built-in step algorithm has no way to do since it only ever sees one edge
// at a time).
export function OrthogonalEdge({ sourceX, sourceY, targetX, targetY, data, ...rest }: EdgeProps) {
  const edgeData = data as OrthogonalEdgeData | undefined
  const wayPoints = edgeData?.wayPoints ?? []
  const points = [{ x: sourceX, y: sourceY }, ...wayPoints, { x: targetX, y: targetY }]
  const expanded = buildOrthogonalPoints(points, edgeData?.laneOffset ?? 0)

  let path = `M ${expanded[0].x} ${expanded[0].y}`
  for (let i = 1; i < expanded.length; i++) path += ` L ${expanded[i].x} ${expanded[i].y}`

  const label = longestSegmentMidpoint(expanded)

  return <BaseEdge path={path} labelX={label.x} labelY={label.y} {...rest} />
}
