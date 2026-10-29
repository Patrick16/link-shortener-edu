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

// A sidebar width the user can drag to adjust, remembered per-browser via localStorage (a
// per-viewer convenience, not state that needs to be shared or read back by anything else) so it
// survives a reload. `direction` flips which way the handle's drag delta should grow the panel -
// +1 for a left sidebar (handle sits on its right edge), -1 for a right sidebar (handle on its left).
export function useResizableWidth(storageKey: string, defaultWidth: number, min: number, max: number, direction: 1 | -1) {
  const [width, setWidth] = useState(() => readStored(storageKey, defaultWidth))

  function onPointerDown(e: React.PointerEvent) {
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

  return { width, onPointerDown }
}
