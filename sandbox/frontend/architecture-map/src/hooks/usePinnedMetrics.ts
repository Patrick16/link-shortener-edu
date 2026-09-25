import { useState } from 'react'

const STORAGE_KEY = 'pinned-metrics'

function readStored(): string[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    const parsed = raw ? JSON.parse(raw) : []
    return Array.isArray(parsed) ? parsed.filter((id): id is string => typeof id === 'string') : []
  } catch {
    return []
  }
}

function persist(ids: string[]) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(ids))
  } catch {
    // Best-effort only - a private window or blocked storage just means it resets next time.
  }
}

// Which component ids have their live CPU/RAM/TCP reading pinned to the top-right overlay (see
// PinnedMetrics) - a component id rather than a resolved serviceId so a pin survives independently
// of container churn (a scale-down/up, a recreate) the same way node selection already does.
export function usePinnedMetrics() {
  const [pinnedIds, setPinnedIds] = useState<string[]>(readStored)

  function isPinned(componentId: string) {
    return pinnedIds.includes(componentId)
  }

  function togglePin(componentId: string) {
    setPinnedIds((prev) => {
      const next = prev.includes(componentId) ? prev.filter((id) => id !== componentId) : [...prev, componentId]
      persist(next)
      return next
    })
  }

  return { pinnedIds, isPinned, togglePin }
}
