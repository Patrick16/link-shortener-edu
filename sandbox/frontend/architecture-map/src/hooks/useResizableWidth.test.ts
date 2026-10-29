import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'
import { useResizableWidth } from './useResizableWidth'

function firePointer(type: 'pointermove' | 'pointerup', clientX = 0) {
  window.dispatchEvent(new PointerEvent(type, { clientX }))
}

describe('useResizableWidth', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('starts at the default width when nothing is stored', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    expect(result.current.width).toBe(300)
  })

  it('starts from a previously stored width when present', () => {
    localStorage.setItem('sidebar-width', '350')

    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    expect(result.current.width).toBe(350)
  })

  it('ignores a corrupt stored value and falls back to the default', () => {
    localStorage.setItem('sidebar-width', 'not-a-number')

    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    expect(result.current.width).toBe(300)
  })

  it('grows the width as the pointer moves right, for a left-sidebar handle (direction +1)', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    act(() => {
      result.current.onPointerDown({ preventDefault: () => {}, clientX: 100 } as React.PointerEvent)
    })
    act(() => firePointer('pointermove', 150))

    expect(result.current.width).toBe(350)
  })

  it('shrinks the width as the pointer moves right, for a right-sidebar handle (direction -1)', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, -1))

    act(() => {
      result.current.onPointerDown({ preventDefault: () => {}, clientX: 100 } as React.PointerEvent)
    })
    act(() => firePointer('pointermove', 150))

    expect(result.current.width).toBe(250)
  })

  it('clamps the width to the given max', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    act(() => {
      result.current.onPointerDown({ preventDefault: () => {}, clientX: 100 } as React.PointerEvent)
    })
    act(() => firePointer('pointermove', 10000))

    expect(result.current.width).toBe(500)
  })

  it('clamps the width to the given min', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    act(() => {
      result.current.onPointerDown({ preventDefault: () => {}, clientX: 100 } as React.PointerEvent)
    })
    act(() => firePointer('pointermove', -10000))

    expect(result.current.width).toBe(200)
  })

  it('persists the final width to localStorage on pointer up', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    act(() => {
      result.current.onPointerDown({ preventDefault: () => {}, clientX: 100 } as React.PointerEvent)
    })
    act(() => firePointer('pointermove', 150))
    act(() => firePointer('pointerup'))

    expect(localStorage.getItem('sidebar-width')).toBe('350')
  })
})
