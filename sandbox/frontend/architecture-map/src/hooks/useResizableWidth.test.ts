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

  it('starts expanded when nothing is stored', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    expect(result.current.collapsed).toBe(false)
  })

  it('starts collapsed when a previous collapse was persisted', () => {
    localStorage.setItem('sidebar-width-collapsed', '1')

    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    expect(result.current.collapsed).toBe(true)
    expect(result.current.width).toBe(0)
  })

  it('toggleCollapsed reports width as 0 while collapsed, and restores the remembered width on expand', () => {
    localStorage.setItem('sidebar-width', '350')
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    act(() => result.current.toggleCollapsed())
    expect(result.current.collapsed).toBe(true)
    expect(result.current.width).toBe(0)

    act(() => result.current.toggleCollapsed())
    expect(result.current.collapsed).toBe(false)
    expect(result.current.width).toBe(350)
  })

  it('persists the collapsed flag to localStorage', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    act(() => result.current.toggleCollapsed())
    expect(localStorage.getItem('sidebar-width-collapsed')).toBe('1')

    act(() => result.current.toggleCollapsed())
    expect(localStorage.getItem('sidebar-width-collapsed')).toBe('0')
  })

  it('ignores a drag start while collapsed', () => {
    const { result } = renderHook(() => useResizableWidth('sidebar-width', 300, 200, 500, 1))

    act(() => result.current.toggleCollapsed())
    act(() => {
      result.current.onPointerDown({ preventDefault: () => {}, clientX: 100 } as React.PointerEvent)
    })
    act(() => firePointer('pointermove', 150))

    expect(result.current.width).toBe(0)
  })
})
