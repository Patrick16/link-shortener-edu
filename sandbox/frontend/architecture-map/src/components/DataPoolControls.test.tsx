import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { clampDataPoolCount, DataPoolControls } from './DataPoolControls'
import type { DataSourceDefinition } from '../types/controlApi'

const sources: DataSourceDefinition[] = [
  { id: 'link-api.links', serviceId: 'link-api', producesVar: 'hash', description: 'Real links from the app' },
]

function renderControls(count: number, onCountChange: (count: number) => void) {
  render(
    <DataPoolControls
      disabled={false}
      sources={sources}
      enabled={true}
      onEnabledChange={() => {}}
      sourceId="link-api.links"
      onSourceIdChange={() => {}}
      count={count}
      onCountChange={onCountChange}
      mode="sequential"
      onModeChange={() => {}}
    />,
  )
}

describe('DataPoolControls Count input', () => {
  it('clamps a valid number into [1, 20000]', () => {
    const onCountChange = vi.fn()
    renderControls(100, onCountChange)

    fireEvent.change(screen.getByLabelText('Count'), { target: { value: '50000' } })

    expect(onCountChange).toHaveBeenCalledWith(20_000)
  })
})

describe('clampDataPoolCount', () => {
  // Regression: Number('-') is NaN, and Math.max/Math.min propagate NaN through unchanged (no
  // clamping applied) - onCountChange used to be called with NaN, which JSON.stringify serializes
  // as `null`, bypassing the backend's friendlier count-range validation message entirely. A native
  // <input type="number">'s own value-sanitization masks this exact input in a DOM-driven test (it
  // strips "-" to "" before onChange fires, and Number("") is 0, not NaN), so this is tested
  // directly against the pure function instead - confirmed by first reproducing the DOM-level
  // masking before extracting it, the same lesson as StageGraphEditor's clampPoint.
  it('returns null for a non-numeric interim value instead of NaN', () => {
    expect(clampDataPoolCount('-')).toBeNull()
  })

  it('clamps a value above the max down to 20000', () => {
    expect(clampDataPoolCount('50000')).toBe(20_000)
  })

  it('clamps a value below the min up to 1', () => {
    expect(clampDataPoolCount('0')).toBe(1)
  })

  it('passes a value already in range through unchanged', () => {
    expect(clampDataPoolCount('500')).toBe(500)
  })
})
