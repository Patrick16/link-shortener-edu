import type { CountBucket } from '../types'

// No charting library in this project - a handful of SVG rects is simpler than adding one for a
// single sparkline-sized panel. viewBox is a fixed 100x40 unit box scaled by CSS (width: 100%), so
// the chart stays crisp at any panel width without recomputing pixel coordinates on resize.
// Each bar's exact count lives in its <title> (a native SVG tooltip on hover), not as visible text -
// a plain number next to the bar would be one more thing to keep from colliding with "Total clicks"
// elsewhere in the same panel.
export function ClicksByDayChart({ buckets }: { buckets: CountBucket[] }) {
  if (buckets.length === 0) {
    return null
  }

  const max = Math.max(...buckets.map((bucket) => bucket.count))
  const barWidth = 100 / buckets.length

  return (
    <svg className="clicks-chart" viewBox="0 0 100 40" preserveAspectRatio="none" role="img" aria-label="Clicks by day">
      {buckets.map((bucket, index) => {
        const height = max === 0 ? 0 : (bucket.count / max) * 36
        return (
          <rect
            key={bucket.key}
            className="clicks-chart-bar"
            x={index * barWidth + barWidth * 0.15}
            y={40 - height}
            width={barWidth * 0.7}
            height={height}
          >
            <title>{`${bucket.key}: ${bucket.count} ${bucket.count === 1 ? 'click' : 'clicks'}`}</title>
          </rect>
        )
      })}
    </svg>
  )
}
