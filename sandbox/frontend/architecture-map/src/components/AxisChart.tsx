interface Point {
  x: number
  y: number
}

interface Props {
  title: string
  points: Point[]
  totalSeconds: number
  formatY: (v: number) => string
  color?: string
}

const WIDTH = 260
const HEIGHT = 110
const PAD_LEFT = 34
const PAD_BOTTOM = 18
const PAD_TOP = 8
const PAD_RIGHT = 8

// A small line chart with real axis labels (elapsed seconds on X, value range on Y) - unlike
// Sparkline (a bare trend indicator with one direct-labeled current value), this is for a reader
// who needs to know the actual scale of what's plotted, not just its shape.
export function AxisChart({ title, points, totalSeconds, formatY, color = 'var(--accent)' }: Props) {
  const maxY = Math.max(1, ...points.map((p) => p.y)) * 1.15
  const plotW = WIDTH - PAD_LEFT - PAD_RIGHT
  const plotH = HEIGHT - PAD_TOP - PAD_BOTTOM

  const toX = (x: number) => PAD_LEFT + (totalSeconds > 0 ? (x / totalSeconds) * plotW : 0)
  const toY = (y: number) => PAD_TOP + plotH - (y / maxY) * plotH

  const path = points.map((p) => `${toX(p.x).toFixed(1)},${toY(p.y).toFixed(1)}`).join(' ')
  const xTicks = [0, Math.round(totalSeconds / 2), totalSeconds]

  return (
    <div className="axis-chart">
      <div className="axis-chart-title">{title}</div>
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} width={WIDTH} height={HEIGHT} role="img" aria-label={title}>
        {/* Y axis: just the max label - the baseline (0) is implied by the X axis line */}
        <text x={PAD_LEFT - 6} y={PAD_TOP + 4} textAnchor="end" className="axis-chart-label">
          {formatY(maxY)}
        </text>
        <text x={PAD_LEFT - 6} y={HEIGHT - PAD_BOTTOM} textAnchor="end" className="axis-chart-label">
          0
        </text>

        {/* Axis lines */}
        <line x1={PAD_LEFT} y1={PAD_TOP} x2={PAD_LEFT} y2={HEIGHT - PAD_BOTTOM} className="axis-chart-axis" />
        <line
          x1={PAD_LEFT}
          y1={HEIGHT - PAD_BOTTOM}
          x2={WIDTH - PAD_RIGHT}
          y2={HEIGHT - PAD_BOTTOM}
          className="axis-chart-axis"
        />

        {/* X axis ticks (elapsed seconds) */}
        {xTicks.map((t) => (
          <text key={t} x={toX(t)} y={HEIGHT - 4} textAnchor="middle" className="axis-chart-label">
            {t}s
          </text>
        ))}

        {points.length > 1 && (
          <polyline points={path} fill="none" stroke={color} strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" />
        )}
      </svg>
    </div>
  )
}
