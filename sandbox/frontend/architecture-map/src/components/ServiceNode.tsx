import { Handle, Position, type NodeProps } from '@xyflow/react'
import { statusColor } from '../utils/statusColor'
import { categorizeRole } from '../utils/liveRole'

export interface ServiceNodeData {
  label: string
  icon: string
  state?: string
  instanceCount: number
  // Live role for a Redis/Mongo node (see useInfraTopology) - undefined for every other node,
  // which has no such role to report. Deliberately not derived from the node's own static id/label:
  // Sentinel/replica-set failover can make architecture.json's "redis-master" node stop being the
  // actual master without this app doing anything, so the badge has to come from asking the
  // cluster itself, not from assuming the name is still true.
  role?: string
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
      {nodeData.role && <span className={`service-node-role service-node-role-${categorizeRole(nodeData.role)}`}>{nodeData.role}</span>}
      <span className="status-dot" style={{ background: statusColor(nodeData.state) }} />
      <Handle type="source" position={Position.Right} />
    </div>
  )
}
