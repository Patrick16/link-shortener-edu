import { Handle, Position, type NodeProps } from '@xyflow/react'
import { statusColor } from '../utils/statusColor'

export interface ServiceNodeData {
  label: string
  icon: string
  state?: string
  instanceCount: number
  [key: string]: unknown
}

// Deliberately minimal: icon + name + a status-colored dot + a "xN" badge when scaled to more
// than one replica. Per-instance detail (which replica, its own CPU/memory) lives in NodePanel
// once a node is clicked - keeping the node itself uncluttered is the point (see dataviz
// guidance: declutter by default, reveal on interaction).
export function ServiceNode({ data, selected }: NodeProps) {
  const nodeData = data as ServiceNodeData
  return (
    <div className={selected ? 'service-node selected' : 'service-node'}>
      <Handle type="target" position={Position.Left} />
      <span className="service-node-icon">{nodeData.icon}</span>
      <span className="service-node-label">{nodeData.label}</span>
      {nodeData.instanceCount > 1 && <span className="service-node-badge">&times;{nodeData.instanceCount}</span>}
      <span className="status-dot" style={{ background: statusColor(nodeData.state) }} />
      <Handle type="source" position={Position.Right} />
    </div>
  )
}
