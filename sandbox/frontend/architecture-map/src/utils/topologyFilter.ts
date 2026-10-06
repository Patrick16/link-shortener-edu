import type { ActiveWhen, ArchComponent, ArchConnection, ArchitectureData } from '../types/architecture'
import type { InfraStatus } from '../types/controlApi'

function matchesGroup(group: Partial<InfraStatus>, status: InfraStatus): boolean {
  return (Object.keys(group) as (keyof InfraStatus)[]).every((key) => group[key] === status[key])
}

// status === null means "haven't fetched InfraStatus yet" - everything stays visible rather than
// flashing an empty graph on first load. Otherwise an array of groups is an OR (any group
// matching is enough, e.g. nginx's two independent jobs), a single group is an AND of its keys.
export function isActive(activeWhen: ActiveWhen | undefined, status: InfraStatus | null): boolean {
  if (!activeWhen || !status) return true
  const groups = Array.isArray(activeWhen) ? activeWhen : [activeWhen]
  return groups.some((group) => matchesGroup(group, status))
}

export interface VisibleConnection extends ArchConnection {
  // Position in the original (unfiltered) data.connections - Diagram's edge ids and
  // ConnectionDetail's selection both index into that array, not into this filtered one, so this
  // must survive the filter instead of being re-derived from the filtered array's own position.
  originalIndex: number
}

export interface VisibleArchitecture {
  components: ArchComponent[]
  connections: VisibleConnection[]
}

// Hiding a node also hides every edge touching it, even one whose own activeWhen (if any) would
// otherwise say it's active - this backstop means a node-level activeWhen change can never leave
// a dangling edge pointing at a hidden node.
export function filterArchitecture(data: ArchitectureData, status: InfraStatus | null): VisibleArchitecture {
  const components = data.components.filter((c) => isActive(c.activeWhen, status))
  const visibleIds = new Set(components.map((c) => c.id))

  const connections: VisibleConnection[] = []
  data.connections.forEach((connection, originalIndex) => {
    if (!isActive(connection.activeWhen, status)) return
    if (!visibleIds.has(connection.from) || !visibleIds.has(connection.to)) return
    connections.push({ ...connection, originalIndex })
  })

  return { components, connections }
}
