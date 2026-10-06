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
  // Which capability-driven job(s) this node is currently doing - see utils/roleBadges.ts. Empty
  // for every node except nginx today.
  roleBadges?: string[]
  // How many distinct Handles to render per side, spread evenly across the node's height instead
  // of every edge converging on one shared point - see Diagram.tsx's outgoing/incoming grouping.
  // Always at least 1 so a node with no edges on a side still has its default attachment point.
  sourceHandleCount?: number
  targetHandleCount?: number
  [key: string]: unknown
}

// Evenly spaced top-offset percentages for `count` handles along a node's height, inset from the
// very top/bottom edge (20%..80% for 2, etc.) so the outermost dots don't sit flush on the
// node's rounded corners.
function handleOffsets(count: number): number[] {
  if (count <= 1) return [50]
  return Array.from({ length: count }, (_, i) => ((i + 1) / (count + 1)) * 100)
}

// Deliberately minimal: icon + name + a status-colored dot + a "xN" badge when scaled to more
// than one replica. Per-instance detail (which replica, its own CPU/memory) lives in NodePanel
// once a node is clicked - keeping the node itself uncluttered is the point (see dataviz
// guidance: declutter by default, reveal on interaction).
export function ServiceNode({ data, selected }: NodeProps) {
  const nodeData = data as ServiceNodeData
  const targetOffsets = handleOffsets(nodeData.targetHandleCount ?? 1)
  const sourceOffsets = handleOffsets(nodeData.sourceHandleCount ?? 1)
  return (
    <div className={selected ? 'service-node selected' : 'service-node'}>
      {targetOffsets.map((top, i) => (
        <Handle key={`target-${i}`} type="target" position={Position.Left} id={`target-${i}`} style={{ top: `${top}%` }} />
      ))}
      <span className="service-node-icon">{nodeData.icon}</span>
      <span className="service-node-label">{nodeData.label}</span>
      {nodeData.instanceCount > 1 && <span className="service-node-badge">&times;{nodeData.instanceCount}</span>}
      {nodeData.role && <span className={`service-node-role service-node-role-${categorizeRole(nodeData.role)}`}>{nodeData.role}</span>}
      {nodeData.roleBadges?.map((badge) => (
        <span key={badge} className="service-node-capability-badge">
          {badge}
        </span>
      ))}
      <span className="status-dot" style={{ background: statusColor(nodeData.state) }} />
      {sourceOffsets.map((top, i) => (
        <Handle key={`source-${i}`} type="source" position={Position.Right} id={`source-${i}`} style={{ top: `${top}%` }} />
      ))}
    </div>
  )
}
