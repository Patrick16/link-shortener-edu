import { useState } from 'react'

function readStored(key: string, fallback: number): number {
  try {
    const raw = localStorage.getItem(key)
    const parsed = raw ? Number(raw) : NaN
    return Number.isFinite(parsed) ? parsed : fallback
  } catch {
    return fallback
  }
}

function readStoredBool(key: string): boolean {
  try {
    return localStorage.getItem(key) === '1'
  } catch {
    return false
  }
}

// A sidebar width the user can drag to adjust, remembered per-browser via localStorage (a
// per-viewer convenience, not state that needs to be shared or read back by anything else) so it
// survives a reload. `direction` flips which way the handle's drag delta should grow the panel -
// +1 for a left sidebar (handle sits on its right edge), -1 for a right sidebar (handle on its left).
//
// `collapsed` is separate from width itself rather than just setting width to 0 - the last dragged
// width is remembered underneath so expanding again restores it exactly, instead of snapping back
// to defaultWidth. `width` already reports 0 while collapsed so callers don't need to branch on
// both values just to size the element.
export function useResizableWidth(storageKey: string, defaultWidth: number, min: number, max: number, direction: 1 | -1) {
  const [width, setWidth] = useState(() => readStored(storageKey, defaultWidth))
  const [collapsed, setCollapsed] = useState(() => readStoredBool(`${storageKey}-collapsed`))

  function onPointerDown(e: React.PointerEvent) {
    if (collapsed) return
    e.preventDefault()
    const startX = e.clientX
    const startWidth = width

    function onMove(ev: PointerEvent) {
      const delta = (ev.clientX - startX) * direction
      setWidth(Math.min(max, Math.max(min, startWidth + delta)))
    }

    function onUp() {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      setWidth((current) => {
        try {
          localStorage.setItem(storageKey, String(current))
        } catch {
          // Best-effort only - a private window or blocked storage just means it resets next time.
        }
        return current
      })
    }

    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
  }

  function toggleCollapsed() {
    setCollapsed((prev) => {
      const next = !prev
      try {
        localStorage.setItem(`${storageKey}-collapsed`, next ? '1' : '0')
      } catch {
        // Best-effort only - a private window or blocked storage just means it resets next time.
      }
      return next
    })
  }

  return { width: collapsed ? 0 : width, collapsed, toggleCollapsed, onPointerDown }
}
