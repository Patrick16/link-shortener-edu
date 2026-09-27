import { useRef, useState } from 'react'

export interface StagePoint {
  t: number
  vus: number
}

interface Props {
  points: StagePoint[]
  onChange: (points: StagePoint[]) => void
  totalDurationSeconds: number
  onTotalDurationChange: (seconds: number) => void
  disabled?: boolean
}

const WIDTH = 640
const HEIGHT = 200
const PAD_LEFT = 40
const PAD_BOTTOM = 24
const PAD_TOP = 14
const PAD_RIGHT = 16
const POINT_RADIUS = 6
const MIN_GAP_SECONDS = 1
// A "Ns, N VUs" label is roughly 55-70px wide at this font size - under this horizontal distance
// from a neighboring point's label, they'd otherwise overlap (see labelFor).
const LABEL_COLLISION_PX = 70
const LABEL_STAGGER_PX = 14

// A number input's onChange can fire with a syntactically incomplete value while the user is still
// typing (a lone "-", "1e", clearing the field...) - Number(...) of that is NaN, and NaN
// propagates through every Math.max/Math.min clamp below (and, for vus, through the shared
// `maxVus = Math.max(10, ...points.map(p => p.vus)) * 1.25` used to render every point) making the
// entire graph render as NaN,NaN until a syntactically complete number is typed. Falling back to
// the point's current value keeps mid-edit keystrokes visually inert instead of corrupting the
// whole graph. Exported as a pure function (not just inlined in the component) so this guard is
// directly unit-testable - a native <input type="number">'s own value-sanitization already strips
// most invalid interim strings down to "" before a test-level onChange ever sees them, which masks
// this exact bug in a jsdom/RTL test driven through the DOM.
export function clampPoint(points: StagePoint[], index: number, next: StagePoint, totalDurationSeconds: number): StagePoint {
  const current = points[index]
  const isFirst = index === 0
  const isLast = index === points.length - 1
  const prev = points[index - 1]
  const following = points[index + 1]

  const rawT = Number.isFinite(next.t) ? next.t : current.t
  const rawVus = Number.isFinite(next.vus) ? next.vus : current.vus

  const clampedT = isFirst
    ? 0
    : isLast
      ? totalDurationSeconds
      : Math.max(prev.t + MIN_GAP_SECONDS, Math.min(following.t - MIN_GAP_SECONDS, rawT))

  return { t: clampedT, vus: rawVus }
}

// A point graph is the actual configuration surface for a load run: drag a point up/down to set
// how many VUs are active at that moment, drag it left/right to move when that happens, click empty
// space to add a ramp/hold point, double-click one to remove it. This is converted to k6's own
// --stage flags (one per pair of adjacent points) right before a run starts - see
// TrafficPanel.pointsToStages - so the model here only needs to be visually/interactively sound,
// not know anything about k6 itself.
export function StageGraphEditor({ points, onChange, totalDurationSeconds, onTotalDurationChange, disabled }: Props) {
  const svgRef = useRef<SVGSVGElement>(null)
  const [dragIndex, setDragIndex] = useState<number | null>(null)

  const maxVus = Math.max(10, ...points.map((p) => p.vus)) * 1.25
  const plotW = WIDTH - PAD_LEFT - PAD_RIGHT
  const plotH = HEIGHT - PAD_TOP - PAD_BOTTOM

  const toX = (t: number) => PAD_LEFT + (totalDurationSeconds > 0 ? (t / totalDurationSeconds) * plotW : 0)
  const toY = (vus: number) => PAD_TOP + plotH - (vus / maxVus) * plotH
  const fromXY = (clientX: number, clientY: number) => {
    const rect = svgRef.current!.getBoundingClientRect()
    const svgX = ((clientX - rect.left) / rect.width) * WIDTH
    const svgY = ((clientY - rect.top) / rect.height) * HEIGHT
    const t = Math.round(((svgX - PAD_LEFT) / plotW) * totalDurationSeconds)
    const vus = Math.round(((PAD_TOP + plotH - svgY) / plotH) * maxVus)
    return { t: Math.max(0, Math.min(totalDurationSeconds, t)), vus: Math.max(0, vus) }
  }

  function updatePoint(index: number, next: StagePoint) {
    const updated = [...points]
    updated[index] = clampPoint(points, index, next, totalDurationSeconds)
    onChange(updated)
  }

  function handlePointerDown(index: number, e: React.PointerEvent) {
    if (disabled) return
    e.stopPropagation()
    ;(e.target as Element).setPointerCapture(e.pointerId)
    setDragIndex(index)
  }

  function handlePointerMove(e: React.PointerEvent) {
    if (dragIndex === null || disabled) return
    updatePoint(dragIndex, fromXY(e.clientX, e.clientY))
  }

  function handlePointerUp() {
    setDragIndex(null)
  }

  function handleBackgroundClick(e: React.MouseEvent) {
    if (disabled || dragIndex !== null) return
    const { t, vus } = fromXY(e.clientX, e.clientY)
    // Insert in time order; nudge away from an exact clash with an existing point so every stage
    // keeps a non-zero duration.
    const withoutClash = points.filter((p) => Math.abs(p.t - t) >= MIN_GAP_SECONDS)
    if (withoutClash.length !== points.length || t <= 0 || t >= totalDurationSeconds) {
      return
    }
    onChange([...points, { t, vus }].sort((a, b) => a.t - b.t))
  }

  function handlePointDoubleClick(index: number, e: React.MouseEvent) {
    if (disabled || index === 0 || index === points.length - 1) return
    e.stopPropagation()
    onChange(points.filter((_, i) => i !== index))
  }

  function removePoint(index: number) {
    if (disabled || index === 0 || index === points.length - 1) return
    onChange(points.filter((_, i) => i !== index))
  }

  const path = points.map((p) => `${toX(p.t).toFixed(1)},${toY(p.vus).toFixed(1)}`).join(' ')
  const xTicks = [0, Math.round(totalDurationSeconds / 2), totalDurationSeconds]

  // A label per point so its time/VUs are readable while dragging or right after a click adds it,
  // not just on hover (the <title> tooltip needs a pause to appear). Anchored to the point's own
  // side at the ends so the text doesn't run past the plot edge, and flipped below the point when
  // it's too close to the top edge to fit a label above.
  function labelFor(p: StagePoint, index: number) {
    const x = toX(p.t)

    // Points are only guaranteed a 1-second minimum gap (updatePoint's clamp), which is a
    // time-domain guarantee only - with a long total duration, two points 1s apart can be under a
    // pixel apart on screen while each "Ns, N VUs" label is ~55-70px wide, so their default same-
    // height labels would fully overlap. Staggering alternating points further from the point
    // itself when either neighbor is this close horizontally spreads crowded labels across two
    // heights instead of stacking them on top of each other.
    const prevX = index > 0 ? toX(points[index - 1].t) : null
    const nextX = index < points.length - 1 ? toX(points[index + 1].t) : null
    const crowded =
      (prevX !== null && Math.abs(x - prevX) < LABEL_COLLISION_PX) ||
      (nextX !== null && Math.abs(x - nextX) < LABEL_COLLISION_PX)
    const stagger = crowded && index % 2 === 1 ? LABEL_STAGGER_PX : 0

    const above = toY(p.vus) - 12 - stagger
    const below = toY(p.vus) + 18 + stagger
    const y = above < PAD_TOP + 8 ? below : above
    const isFirst = index === 0
    const isLast = index === points.length - 1
    const anchor: 'start' | 'middle' | 'end' = isFirst ? 'start' : isLast ? 'end' : 'middle'
    const dx = isFirst ? 5 : isLast ? -5 : 0
    return { x: x + dx, y, anchor }
  }

  return (
    <div className="stage-graph">
      <div className="stage-graph-header">
        <span className="axis-chart-title">Load profile (VUs over time)</span>
        <label className="stage-graph-duration">
          Total (s)
          <input
            type="number"
            min={2}
            max={600}
            value={totalDurationSeconds}
            disabled={disabled}
            onChange={(e) => {
              const next = Math.max(2, Math.min(600, Number(e.target.value)))
              // Rescale every point's time proportionally so the shape of the ramp is preserved
              // when the overall window is stretched or squeezed, rather than clamping points off
              // the end of a shortened graph.
              const scale = next / totalDurationSeconds
              const last = points.length - 1
              const rescaled = points.map((p, i) => ({
                ...p,
                t: i === 0 ? 0 : i === last ? next : Math.round(p.t * scale),
              }))

              // Squeezing a small `next` used to clamp every middle point to the same t=1 (each one
              // independently bounded to [1, next-1], so any two or more collapsed onto the same
              // timestamp) - a forward pass over the middle points only (each at least 1s after the
              // previous) followed by a backward pass (each at least 1s before the next, cascading
              // down from the fixed last point at `next`) instead spreads them out as evenly as the
              // available window actually allows, only falling back to a shared timestamp in the
              // genuinely-impossible case of more middle points than seconds of room between them.
              // The first (0) and last (`next`) points stay exactly fixed - the forward pass must
              // stop before `last`, or it could push the last point past `next`.
              for (let i = 1; i < last; i++) {
                rescaled[i].t = Math.max(rescaled[i].t, rescaled[i - 1].t + 1)
              }
              for (let i = last - 1; i >= 1; i--) {
                rescaled[i].t = Math.min(rescaled[i].t, rescaled[i + 1].t - 1)
              }

              // Safety net for the genuinely-impossible case (more middle points than seconds of
              // room for them, e.g. 5 middle points squeezed into a 2s window): the backward pass
              // above can still drive an inner point below 0 while cascading a too-small window's
              // constraint back through the chain. Clamping to 0 trades a further tie for a point
              // (never a negative/out-of-range timestamp) - already a degenerate input the graph
              // can't meaningfully represent either way.
              for (let i = 1; i < last; i++) {
                rescaled[i].t = Math.max(0, rescaled[i].t)
              }

              onChange(rescaled)
              onTotalDurationChange(next)
            }}
          />
        </label>
      </div>
      <svg
        ref={svgRef}
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        width="100%"
        height={HEIGHT}
        role="img"
        aria-label="Load profile editor"
        className={disabled ? 'stage-graph-svg stage-graph-svg-disabled' : 'stage-graph-svg'}
        onClick={handleBackgroundClick}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
      >
        <text x={PAD_LEFT - 6} y={PAD_TOP + 4} textAnchor="end" className="axis-chart-label">
          {Math.round(maxVus)}
        </text>
        <text x={PAD_LEFT - 6} y={HEIGHT - PAD_BOTTOM} textAnchor="end" className="axis-chart-label">
          0
        </text>

        <line x1={PAD_LEFT} y1={PAD_TOP} x2={PAD_LEFT} y2={HEIGHT - PAD_BOTTOM} className="axis-chart-axis" />
        <line x1={PAD_LEFT} y1={HEIGHT - PAD_BOTTOM} x2={WIDTH - PAD_RIGHT} y2={HEIGHT - PAD_BOTTOM} className="axis-chart-axis" />

        {xTicks.map((t) => (
          <text key={t} x={toX(t)} y={HEIGHT - 6} textAnchor="middle" className="axis-chart-label">
            {t}s
          </text>
        ))}

        {points.length > 1 && <polyline points={path} fill="none" stroke="var(--accent)" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" />}

        {points.map((p, i) => (
          <circle
            key={i}
            cx={toX(p.t)}
            cy={toY(p.vus)}
            r={POINT_RADIUS}
            className="stage-graph-point"
            onPointerDown={(e) => handlePointerDown(i, e)}
            onClick={(e) => e.stopPropagation()}
            onDoubleClick={(e) => handlePointDoubleClick(i, e)}
          >
            <title>{`${p.t}s → ${p.vus} VUs`}</title>
          </circle>
        ))}

        {points.map((p, i) => {
          const { x, y, anchor } = labelFor(p, i)
          return (
            <text key={`label-${i}`} x={x} y={y} textAnchor={anchor} className="stage-graph-point-label">
              {p.t}s, {p.vus} VUs
            </text>
          )
        })}
      </svg>
      <p className="stage-graph-hint">Click empty space to add a point, drag a point to reshape the ramp, double-click a middle point to remove it.</p>

      <ul className="stage-graph-points-list">
        {points.map((p, i) => {
          const isFirst = i === 0
          const isLast = i === points.length - 1
          return (
            <li className="stage-graph-point-row" key={i}>
              <span className="stage-graph-point-row-index">{i + 1}</span>
              <label className="stage-graph-point-row-field">
                t (s)
                <input
                  type="number"
                  min={0}
                  max={totalDurationSeconds}
                  value={p.t}
                  disabled={disabled || isFirst || isLast}
                  onChange={(e) => updatePoint(i, { t: Number(e.target.value), vus: p.vus })}
                />
              </label>
              <label className="stage-graph-point-row-field">
                VUs
                <input
                  type="number"
                  min={0}
                  max={200}
                  value={p.vus}
                  disabled={disabled}
                  onChange={(e) => updatePoint(i, { t: p.t, vus: Math.max(0, Math.min(200, Number(e.target.value))) })}
                />
              </label>
              <button type="button" onClick={() => removePoint(i)} disabled={disabled || isFirst || isLast}>
                Remove
              </button>
            </li>
          )
        })}
      </ul>
    </div>
  )
}
