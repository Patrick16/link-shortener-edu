import type { NodeProps } from '@xyflow/react'

export interface RegionNodeData {
  label: string
  [key: string]: unknown
}

// Purely a visual backdrop - not draggable/selectable/connectable (see Diagram.tsx's node list,
// where these are built with those all set to false), so it never intercepts a click meant for a
// real node or edge sitting on top of it. Sized and positioned from layoutGraph.ts's dagre compound
// (cluster) layout, which already computed a bounding box around every node belonging to this
// region - this component just draws that box and its label, nothing else.
export function RegionNode({ data }: NodeProps) {
  const regionData = data as RegionNodeData
  return (
    <div className="region-node">
      <span className="region-node-label">{regionData.label}</span>
    </div>
  )
}
