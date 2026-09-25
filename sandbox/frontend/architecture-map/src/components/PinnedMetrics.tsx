import { Fragment, useState } from 'react'
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

type SortKey = 'cpuPercent' | 'ramMb' | 'tcp'

const COLLAPSE_KEY = 'pinned-metrics-collapsed'

function readCollapsed(): boolean {
  try {
    return localStorage.getItem(COLLAPSE_KEY) === '1'
  } catch {
    return false
  }
}

function persistCollapsed(next: boolean) {
  try {
    localStorage.setItem(COLLAPSE_KEY, next ? '1' : '0')
  } catch {
    // Best-effort only - a private window or blocked storage just means it resets next time.
  }
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
// scan and compare at a glance. The column headers double as sort buttons - clicking one sorts by
// just that column (clicking the active one flips direction), replacing the old fixed
// cpu-then-ram-then-tcp tiebreak chain with whichever single reading the user actually wants ranked.
export function PinnedMetrics({ pinnedIds, metaById, knownServiceIds, containers, resourceHistoryByContainer, onUnpin }: Props) {
  const [collapsed, setCollapsed] = useState(readCollapsed)
  const [sortKey, setSortKey] = useState<SortKey>('cpuPercent')
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc')

  if (pinnedIds.length === 0) {
    return null
  }

  function toggleCollapsed() {
    setCollapsed((prev) => {
      const next = !prev
      persistCollapsed(next)
      return next
    })
  }

  function handleSortClick(key: SortKey) {
    if (key === sortKey) {
      setSortDir((prev) => (prev === 'desc' ? 'asc' : 'desc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  function sortIndicator(key: SortKey) {
    if (key !== sortKey) {
      return null
    }
    return <span className="pinned-metrics-sort-arrow">{sortDir === 'desc' ? '▾' : '▴'}</span>
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
    // A row with no live sample yet (null) always sorts last, regardless of direction - there's
    // nothing to rank it by, so it shouldn't jump to the top just because "asc" was picked.
    .sort((a, b) => {
      const av = a[sortKey]
      const bv = b[sortKey]
      if (av === null && bv === null) return 0
      if (av === null) return 1
      if (bv === null) return -1
      return sortDir === 'desc' ? bv - av : av - bv
    })

  return (
    <div className="pinned-metrics">
      <div className="pinned-metrics-toolbar">
        <span className="pinned-metrics-count">{rows.length} pinned</span>
        <button
          className="pinned-metrics-collapse-btn"
          onClick={toggleCollapsed}
          aria-label={collapsed ? 'Expand pinned metrics' : 'Collapse pinned metrics'}
          title={collapsed ? 'Expand' : 'Collapse'}
        >
          {collapsed ? '▸' : '▾'}
        </button>
      </div>

      {!collapsed && (
        <div className="pinned-metrics-grid">
          <span className="pinned-metrics-col-header" />
          <button className="pinned-metrics-col-header pinned-metrics-sort-btn" onClick={() => handleSortClick('cpuPercent')}>
            CPU %{sortIndicator('cpuPercent')}
          </button>
          <button className="pinned-metrics-col-header pinned-metrics-sort-btn" onClick={() => handleSortClick('ramMb')}>
            RAM MB{sortIndicator('ramMb')}
          </button>
          <button className="pinned-metrics-col-header pinned-metrics-sort-btn" onClick={() => handleSortClick('tcp')}>
            TCP{sortIndicator('tcp')}
          </button>
          <span className="pinned-metrics-col-header" />

          {rows.map((row) => (
            <Fragment key={row.key}>
              <span className="pinned-metrics-name">
                {row.component.icon} {row.component.name}
                {row.instanceLabel}
              </span>
              <span className="pinned-metrics-value">{row.cpuPercent === null ? '—' : row.cpuPercent.toFixed(1)}</span>
              <span className="pinned-metrics-value">{row.ramMb === null ? '—' : row.ramMb.toFixed(0)}</span>
              <span className="pinned-metrics-value">{row.tcp === null ? '—' : row.tcp}</span>
              <button
                className="pinned-metrics-unpin-btn"
                onClick={() => onUnpin(row.componentId)}
                aria-label={`Unpin ${row.component.name}${row.instanceLabel}`}
                title="Unpin"
              >
                &times;
              </button>
            </Fragment>
          ))}
        </div>
      )}
    </div>
  )
}
