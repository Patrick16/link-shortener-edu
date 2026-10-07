import { formatTimestamp } from '../utils/formatTimestamp'
import type { BaselineComparison } from '../types/controlApi'

interface Props {
  baseline?: BaselineComparison | null
}

function formatValue(value: number, unit: string): string {
  return unit === '%' ? `${value.toFixed(1)}%` : `${value.toFixed(unit === 'ms' ? 0 : 1)} ${unit}`
}

function formatPercentChange(percentChange: number | null): string {
  if (percentChange === null) {
    return '—'
  }
  const sign = percentChange > 0 ? '+' : ''
  return `${sign}${percentChange.toFixed(1)}%`
}

// Shown when this run has a same-scenario predecessor to compare against (see
// TrafficRunCoordinator.FindBaselineComparisonAsync on the backend - absent for the first run of a
// given scenario). Deliberately separate from BottleneckPanel's rule-based verdict: "is this faster
// or slower than last time" and "what in THIS run looks like a bottleneck" are different questions,
// and conflating them would mean a run that regressed vs. its baseline but still looks fine in
// isolation (or vice versa) has nowhere honest to be represented.
export function BaselineComparisonPanel({ baseline }: Props) {
  if (!baseline) {
    return null
  }

  return (
    <div className="baseline-panel">
      <h3>vs previous run of this scenario</h3>
      <p className="baseline-panel-note">Compared to the run from {formatTimestamp(baseline.baselineTimestamp)}.</p>
      <table className="baseline-table">
        <thead>
          <tr>
            <th>Metric</th>
            <th>This run</th>
            <th>Previous</th>
            <th>Change</th>
          </tr>
        </thead>
        <tbody>
          {baseline.metrics.map((metric) => {
            // Exact equality (not "isRegression is false") on purpose: isRegression only encodes
            // direction, so an unchanged metric (current === baseline) would otherwise fall into
            // the "improvement" branch and render green with 0.0%/"—" - a real, if cosmetic,
            // misread for anyone scanning the table for actual regressions.
            const changeClass =
              metric.current === metric.baseline
                ? 'baseline-change-neutral'
                : metric.isRegression
                  ? 'baseline-change-regression'
                  : 'baseline-change-improvement'
            return (
              <tr key={metric.name}>
                <td>{metric.name}</td>
                <td>{formatValue(metric.current, metric.unit)}</td>
                <td>{formatValue(metric.baseline, metric.unit)}</td>
                <td className={changeClass}>{formatPercentChange(metric.percentChange)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
