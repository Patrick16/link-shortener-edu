import { Fragment } from 'react'
import { resolveServiceId } from '../utils/resolveServiceId'
import type { ArchComponent } from '../types/architecture'
import type { ResourceSample } from '../types/controlApi'

interface Props {
  pinnedIds: string[]
  metaById: Map<string, ArchComponent>
  knownServiceIds: Set<string>
  resourceHistory: Record<string, ResourceSample[]>
  onUnpin: (componentId: string) => void
}

interface Row {
  id: string
  component: ArchComponent
  cpuPercent: number | null
  ramMb: number | null
  tcp: number | null
}

// Fixed top-right overlay, independent of the (single, left) sidebar and whatever's currently
// selected there - pinning a node's reading is exactly for keeping it visible while looking at, or
// controlling, something else entirely. Renders directly off resourceHistory/containers (the same
// live SignalR state as NodePanel's own sparklines), so every row updates in real time with no
// polling of its own.
//
// One CSS grid across every row (not a card per row) - that's what makes the CPU/RAM/TCP columns
// line up at the same width regardless of how long each node's name is, so readings are easy to
// scan and compare at a glance.
export function PinnedMetrics({ pinnedIds, metaById, knownServiceIds, resourceHistory, onUnpin }: Props) {
  if (pinnedIds.length === 0) {
    return null
  }

  const rows: Row[] = pinnedIds
    .map((id) => {
      const component = metaById.get(id)
      if (!component) {
        return null
      }

      const serviceId = resolveServiceId(component, knownServiceIds)
      const latest = serviceId ? resourceHistory[serviceId]?.at(-1) : undefined
      return {
        id,
        component,
        cpuPercent: latest ? latest.cpuPercent : null,
        ramMb: latest ? latest.memoryUsageBytes / (1024 * 1024) : null,
        tcp: latest ? latest.tcpConnections : null,
      }
    })
    .filter((row): row is Row => row !== null)
    // Busiest first - CPU is the primary signal, RAM then TCP break ties between rows reading the
    // same CPU%. A row with no live sample yet (null) sorts after every row that has one.
    .sort(
      (a, b) =>
        (b.cpuPercent ?? -1) - (a.cpuPercent ?? -1) || (b.ramMb ?? -1) - (a.ramMb ?? -1) || (b.tcp ?? -1) - (a.tcp ?? -1),
    )

  return (
    <div className="pinned-metrics">
      {rows.map((row) => (
        <Fragment key={row.id}>
          <span className="pinned-metrics-name">
            {row.component.icon} {row.component.name}
          </span>
          <span className="pinned-metrics-value">{row.cpuPercent === null ? '—' : `${row.cpuPercent.toFixed(1)}%`}</span>
          <span className="pinned-metrics-value">{row.ramMb === null ? '—' : `${row.ramMb.toFixed(0)} MB`}</span>
          <span className="pinned-metrics-value">{row.tcp === null ? '—' : `${row.tcp} TCP`}</span>
          <button onClick={() => onUnpin(row.id)} aria-label={`Unpin ${row.component.name}`} title="Unpin">
            &times;
          </button>
        </Fragment>
      ))}
    </div>
  )
}
