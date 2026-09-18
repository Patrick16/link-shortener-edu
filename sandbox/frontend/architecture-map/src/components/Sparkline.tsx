interface Props {
  label: string
  values: number[]
  max: number
  formatValue: (v: number) => string
  color?: string
}

const WIDTH = 160
const HEIGHT = 40
const PADDING = 4

// A single-hue magnitude sparkline (CPU% or memory) - one series, so no legend, just a direct
// label of the current value next to the line. Thin 2px stroke, rounded line join, no axis chrome.
export function Sparkline({ label, values, max, formatValue, color = 'var(--accent)' }: Props) {
  const current = values.length > 0 ? values[values.length - 1] : 0
  const points = values.length > 1 ? toPoints(values, max) : ''

  return (
    <div className="sparkline">
      <div className="sparkline-header">
        <span className="sparkline-label">{label}</span>
        <span className="sparkline-value">{formatValue(current)}</span>
      </div>
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        width={WIDTH}
        height={HEIGHT}
        role="img"
        aria-label={`${label}: ${formatValue(current)}`}
      >
        {values.length > 1 && (
          <polyline
            points={points}
            fill="none"
            stroke={color}
            strokeWidth={2}
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        )}
      </svg>
    </div>
  )
}

function toPoints(values: number[], max: number): string {
  const step = (WIDTH - PADDING * 2) / (values.length - 1)
  return values
    .map((v, i) => {
      const x = PADDING + i * step
      const ratio = max > 0 ? Math.min(v / max, 1) : 0
      const y = HEIGHT - PADDING - ratio * (HEIGHT - PADDING * 2)
      return `${x.toFixed(1)},${y.toFixed(1)}`
    })
    .join(' ')
}
