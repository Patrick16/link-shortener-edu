import { useState } from 'react'

const STORAGE_KEY = 'edge-labels-pinned'

function readStored(): number[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    const parsed = raw ? JSON.parse(raw) : []
    return Array.isArray(parsed) ? parsed.filter((n): n is number => typeof n === 'number') : []
  } catch {
    return []
  }
}

function persist(indices: number[]) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(indices))
  } catch {
    // Best-effort only - a private window or blocked storage just means it resets next time.
  }
}

export interface EdgeLabelsApi {
  isVisible: (connectionIndex: number) => boolean
  toggle: (connectionIndex: number) => void
  showAll: () => void
  hideAll: () => void
}

// Which connections (keyed by their stable originalIndex into data.connections - see
// topologyFilter.ts's VisibleConnection - not the filtered/visible array's own position) have
// their edge label pinned visible on the graph. Labels default to hidden (see App.css) since ~40
// of them on screen at once buries the lines themselves under overlapping text, so a label only
// shows once a user deliberately clicks that edge, and hides again on a second click - the
// Topology panel's "Show all / Hide all" pair (see TopologyPanel.tsx) are just bulk versions of
// the same per-edge click, not a separate mode: an individual click still works normally after
// either one.
export function useEdgeLabels(totalConnections: number): EdgeLabelsApi {
  const [pinned, setPinned] = useState<number[]>(readStored)

  function isVisible(connectionIndex: number): boolean {
    return pinned.includes(connectionIndex)
  }

  function toggle(connectionIndex: number) {
    setPinned((prev) => {
      const next = prev.includes(connectionIndex) ? prev.filter((i) => i !== connectionIndex) : [...prev, connectionIndex]
      persist(next)
      return next
    })
  }

  function showAll() {
    const all = Array.from({ length: totalConnections }, (_, i) => i)
    persist(all)
    setPinned(all)
  }

  function hideAll() {
    persist([])
    setPinned([])
  }

  return { isVisible, toggle, showAll, hideAll }
}
