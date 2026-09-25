import { Fragment } from 'react'
import { resolveServiceId } from '../utils/resolveServiceId'
import type { ArchComponent } from '../types/architecture'
import type { ManagedContainer, ResourceSample } from '../types/controlApi'

interface Props {
  pinnedIds: string[]
  metaById: Map<string, ArchComponent>
  knownServiceIds: Set<string>
  containers: Record<string, ManagedContainer[]>
  // Keyed by containerId, not serviceId - see useLiveStack. A scaled service pins out to one row
  // per replica below instead of one row sharing/averaging every replica's numbers.
  resourceHistoryByContainer: Record<string, ResourceSample[]>
  onUnpin: (componentId: string) => void
}

interface Row {
  key: string
  componentId: string
  component: ArchComponent
  instanceLabel: string
  cpuPercent: number | null
  ramMb: number | null
  tcp: number | null
}

// Fixed top-right overlay, independent of the (single, left) sidebar and whatever's currently
// selected there - pinning a node's reading is exactly for keeping it visible while looking at, or
// controlling, something else entirely. Renders directly off resourceHistoryByContainer/containers
// (the same live SignalR state as NodePanel's own sparklines), so every row updates in real time
// with no polling of its own.
//
// One row per running instance, not one row per pinned node - a scaled service (N containers behind
// one pin) expands into N rows here, each labelled "#<containerNumber>", since a single averaged or
// arbitrarily-chosen reading would hide which particular replica is actually busy.
//
// One CSS grid across every row (not a card per row) - that's what makes the CPU/RAM/TCP columns
// line up at the same width regardless of how long each node's own name is, so readings are easy to
// scan and compare at a glance.
export function PinnedMetrics({ pinnedIds, metaById, knownServiceIds, containers, resourceHistoryByContainer, onUnpin }: Props) {
  if (pinnedIds.length === 0) {
    return null
  }

  const rows: Row[] = pinnedIds
    .flatMap((id): Row[] => {
      const component = metaById.get(id)
      if (!component) {
        return []
      }

      const serviceId = resolveServiceId(component, knownServiceIds)
      const instances = serviceId ? (containers[serviceId] ?? []) : []
      if (instances.length === 0) {
        return [{ key: id, componentId: id, component, instanceLabel: '', cpuPercent: null, ramMb: null, tcp: null }]
      }

      return instances.map((instance) => {
        const latest = resourceHistoryByContainer[instance.containerId]?.at(-1)
        return {
          key: `${id}:${instance.containerId}`,
          componentId: id,
          component,
          instanceLabel: instances.length > 1 ? ` #${instance.containerNumber}` : '',
          cpuPercent: latest ? latest.cpuPercent : null,
          ramMb: latest ? latest.memoryUsageBytes / (1024 * 1024) : null,
          tcp: latest ? latest.tcpConnections : null,
        }
      })
    })
    // Busiest first - CPU is the primary signal, RAM then TCP break ties between rows reading the
    // same CPU%. A row with no live sample yet (null) sorts after every row that has one.
    .sort(
      (a, b) =>
        (b.cpuPercent ?? -1) - (a.cpuPercent ?? -1) || (b.ramMb ?? -1) - (a.ramMb ?? -1) || (b.tcp ?? -1) - (a.tcp ?? -1),
    )

  return (
    <div className="pinned-metrics">
      {rows.map((row) => (
        <Fragment key={row.key}>
          <span className="pinned-metrics-name">
            {row.component.icon} {row.component.name}
            {row.instanceLabel}
          </span>
          <span className="pinned-metrics-value">{row.cpuPercent === null ? '—' : `${row.cpuPercent.toFixed(1)}%`}</span>
          <span className="pinned-metrics-value">{row.ramMb === null ? '—' : `${row.ramMb.toFixed(0)} MB`}</span>
          <span className="pinned-metrics-value">{row.tcp === null ? '—' : `${row.tcp} TCP`}</span>
          <button onClick={() => onUnpin(row.componentId)} aria-label={`Unpin ${row.component.name}${row.instanceLabel}`} title="Unpin">
            &times;
          </button>
        </Fragment>
      ))}
    </div>
  )
}
