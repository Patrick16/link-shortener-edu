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
    const isFirst = index === 0
    const isLast = index === points.length - 1
    const prev = points[index - 1]
    const following = points[index + 1]

    const clampedT = isFirst
      ? 0
      : isLast
        ? totalDurationSeconds
        : Math.max(prev.t + MIN_GAP_SECONDS, Math.min(following.t - MIN_GAP_SECONDS, next.t))

    const updated = [...points]
    updated[index] = { t: clampedT, vus: next.vus }
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

  const path = points.map((p) => `${toX(p.t).toFixed(1)},${toY(p.vus).toFixed(1)}`).join(' ')
  const xTicks = [0, Math.round(totalDurationSeconds / 2), totalDurationSeconds]

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
              onChange(
                points.map((p, i) => ({
                  ...p,
                  t: i === 0 ? 0 : i === last ? next : Math.max(1, Math.min(next - 1, Math.round(p.t * scale))),
                })),
              )
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
      </svg>
      <p className="stage-graph-hint">Click empty space to add a point, drag a point to reshape the ramp, double-click a middle point to remove it.</p>
    </div>
  )
}
