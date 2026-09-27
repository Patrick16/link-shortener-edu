import { useState } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { clampPoint, StageGraphEditor, type StagePoint } from './StageGraphEditor'

// A real parent keeps points/totalDurationSeconds in state and feeds updated props back in on
// every onChange - StageGraphEditor's own "Total (s)" input is controlled, so without this wrapper
// updating state between keystrokes, the input never reflects what was actually typed (it snaps
// back to whatever fixed value a non-stateful test double would pass in).
function ControlledEditor({ initialPoints, initialTotal }: { initialPoints: StagePoint[]; initialTotal: number }) {
  const [points, setPoints] = useState(initialPoints)
  const [total, setTotal] = useState(initialTotal)
  return (
    <StageGraphEditor
      points={points}
      onChange={setPoints}
      totalDurationSeconds={total}
      onTotalDurationChange={setTotal}
    />
  )
}

function renderEditor(points: StagePoint[], totalDurationSeconds: number) {
  render(<ControlledEditor initialPoints={points} initialTotal={totalDurationSeconds} />)
}

function readPointTimes(): number[] {
  return screen.getAllByLabelText('t (s)').map((input) => Number((input as HTMLInputElement).value))
}

function isStrictlyIncreasing(values: number[]): boolean {
  return values.every((v, i) => i === 0 || v > values[i - 1])
}

describe('StageGraphEditor total-duration rescale', () => {
  it('squeezing the total keeps middle points distinct and ordered when there is room for them, not all collapsed to the same timestamp', () => {
    // Regression: with 2+ middle points, shrinking "Total (s)" used to clamp every middle point to
    // exactly t=1 independently, producing duplicate timestamps regardless of how much room the new
    // total actually had - pointsToStages then emits a 0-duration stage, which control-api's own
    // validation rejects.
    renderEditor(
      [
        { t: 0, vus: 0 },
        { t: 3, vus: 50 },
        { t: 13, vus: 50 },
        { t: 20, vus: 0 },
      ],
      20,
    )

    // A single change event (a paste, or the value after a blur) rather than typing digit-by-digit -
    // typing one keystroke at a time into a live-clamped controlled input has its own, unrelated
    // interaction with the min=2 clamp that isn't what this test is about.
    fireEvent.change(screen.getByLabelText('Total (s)'), { target: { value: '5' } })

    const times = readPointTimes()
    expect(times[0]).toBe(0)
    expect(times.at(-1)).toBe(5)
    expect(isStrictlyIncreasing(times)).toBe(true)
  })

  it('squeezing far below the room the middle points need still produces a valid, in-range graph', () => {
    // The genuinely-impossible case: 2 middle points need at least 3 seconds of total room (1s
    // minimum gap each) to stay fully distinct, squeezed here into a 2s window. There's no ordering
    // that keeps every point distinct, but the result must still be in-range and non-negative
    // instead of the old bug's behavior (both middle points landing on the same wrong timestamp) or
    // this fix's own edge case (a middle point pushed below 0 while cascading the too-small
    // constraint back through the chain).
    renderEditor(
      [
        { t: 0, vus: 0 },
        { t: 3, vus: 50 },
        { t: 13, vus: 50 },
        { t: 20, vus: 0 },
      ],
      20,
    )

    fireEvent.change(screen.getByLabelText('Total (s)'), { target: { value: '2' } })

    const times = readPointTimes()
    expect(times[0]).toBe(0)
    expect(times.at(-1)).toBe(2)
    expect(times.every((t) => t >= 0 && t <= 2)).toBe(true)
  })

  it('stretching the total spreads middle points out proportionally', () => {
    renderEditor(
      [
        { t: 0, vus: 0 },
        { t: 5, vus: 50 },
        { t: 10, vus: 0 },
      ],
      10,
    )

    fireEvent.change(screen.getByLabelText('Total (s)'), { target: { value: '100' } })

    expect(readPointTimes()).toEqual([0, 50, 100])
  })
})

describe('clampPoint NaN guard', () => {
  // Regression: a lone "-" (a normal first keystroke for a negative number), "1e", or a cleared
  // field all parse via Number(...) to NaN. Before this guard, NaN was written straight into
  // state - and because maxVus is computed from every point's vus, one NaN anywhere made every
  // point's y-coordinate NaN, collapsing the whole rendered graph (not just the edited point).
  // A native <input type="number">'s own value-sanitization masks this exact input in a
  // DOM/jsdom-driven test (it strips most invalid interim strings to "" before onChange fires), so
  // this is tested directly against the pure function instead.
  const points: StagePoint[] = [
    { t: 0, vus: 0 },
    { t: 5, vus: 40 },
    { t: 10, vus: 0 },
  ]

  it('a NaN vus falls back to the point current value instead of corrupting it', () => {
    const result = clampPoint(points, 1, { t: 5, vus: NaN }, 10)

    expect(result.vus).toBe(40)
  })

  it('a NaN t falls back to the point current value instead of corrupting it', () => {
    const result = clampPoint(points, 1, { t: NaN, vus: 40 }, 10)

    expect(result.t).toBe(5)
  })

  it('a valid t and vus are still clamped normally', () => {
    const result = clampPoint(points, 1, { t: 6, vus: 55 }, 10)

    expect(result).toEqual({ t: 6, vus: 55 })
  })
})

describe('StageGraphEditor label collision handling', () => {
  it('staggers a crowded point label to a different height than a label it would otherwise overlap', () => {
    // Two points 1s apart in a 300s window are under a pixel apart on screen, even though the 1s
    // gap itself is perfectly valid - their default same-height labels would fully overlap.
    render(
      <StageGraphEditor
        points={[
          { t: 0, vus: 0 },
          { t: 100, vus: 40 },
          { t: 101, vus: 60 },
          { t: 300, vus: 0 },
        ]}
        onChange={() => {}}
        totalDurationSeconds={300}
        onTotalDurationChange={() => {}}
      />,
    )

    const labels = Array.from(document.querySelectorAll('text.stage-graph-point-label'))
    const crowdedPair = [labels[1], labels[2]]
    expect(crowdedPair[0].getAttribute('y')).not.toBe(crowdedPair[1].getAttribute('y'))
  })

  it('does not stagger points that are far enough apart to already avoid overlap', () => {
    render(
      <StageGraphEditor
        points={[
          { t: 0, vus: 0 },
          { t: 100, vus: 40 },
          { t: 200, vus: 40 },
          { t: 300, vus: 0 },
        ]}
        onChange={() => {}}
        totalDurationSeconds={300}
        onTotalDurationChange={() => {}}
      />,
    )

    const labels = Array.from(document.querySelectorAll('text.stage-graph-point-label'))
    expect(labels[1].getAttribute('y')).toBe(labels[2].getAttribute('y'))
  })
})
