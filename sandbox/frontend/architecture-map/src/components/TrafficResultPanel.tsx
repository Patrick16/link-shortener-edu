import { useState } from 'react'
import { AxisChart } from './AxisChart'
import { TrafficReportView } from './TrafficReportView'
import type { TrafficRunState } from '../hooks/useTrafficRun'

interface Props extends Pick<TrafficRunState, 'running' | 'progress' | 'progressHistory' | 'report' | 'error'> {
  fallbackTotalSeconds: number
}

const COLLAPSE_KEY = 'traffic-result-collapsed'

function readCollapsed(): boolean {
  try {
    return localStorage.getItem(COLLAPSE_KEY) === '1'
  } catch {
    return false
  }
}

// Header strip: while a run is active, its live progress bar + charts; once it finishes, the same
// report a moment ago would have shown inline. Config lives in the left sidebar instead - this
// panel only ever reflects a run already in flight or just completed, never edits anything.
//
// A full report (stat tiles + latency + checks + per-endpoint status + raw output) can get tall
// enough to push the graph and sidebars below the fold if the header were left to grow freely, so
// its content sits in a height-capped, scrollable box and can be collapsed to a single line.
export function TrafficResultPanel({ running, progress, progressHistory, report, error, fallbackTotalSeconds }: Props) {
  const [collapsed, setCollapsed] = useState(readCollapsed)

  function toggleCollapsed() {
    setCollapsed((prev) => {
      const next = !prev
      try {
        localStorage.setItem(COLLAPSE_KEY, next ? '1' : '0')
      } catch {
        // Best-effort only - a private window or blocked storage just means it resets next time.
      }
      return next
    })
  }

  // No fixed total duration in iterations mode - the live charts' X axis just tracks how far
  // elapsed has gotten so far instead of a known end point.
  const chartTotalSeconds = progress?.targetIterations != null ? Math.max(1, progress.elapsedSeconds) : (progress?.totalSeconds ?? fallbackTotalSeconds)
  const vusPoints = progressHistory.map((p) => ({ x: p.elapsedSeconds, y: p.activeVus }))
  const ratePoints = progressHistory.map((p) => ({ x: p.elapsedSeconds, y: p.iterationsPerSecond }))

  if (!running && !report && !error) {
    return <div className="traffic-result-panel traffic-result-empty">Configure a run on the left, then hit Run traffic.</div>
  }

  return (
    <div className="traffic-result-panel">
      <div className="traffic-result-header">
        <span className="traffic-result-header-label">
          {running
            ? 'Running...'
            : report
              ? `Last run - ${report.httpRequests} requests, ${report.failedRequests} failed`
              : 'Last run'}
        </span>
        <button type="button" className="traffic-result-toggle" onClick={toggleCollapsed}>
          {collapsed ? 'Show details' : 'Hide details'}
        </button>
      </div>

      {error && <p className="service-card-error">{error}</p>}

      {!collapsed && (
        <div className="traffic-result-content">
          {running && (
            <div className="traffic-progress">
              <div className="progress-bar-track">
                <div className="progress-bar-fill" style={{ width: `${progress?.percentComplete ?? 0}%` }} />
              </div>
              <div className="progress-bar-label">
                {progress
                  ? progress.targetIterations != null
                    ? `${progress.iterationsSoFar}/${progress.targetIterations} iterations (${progress.percentComplete}%)`
                    : `${progress.elapsedSeconds}s / ${progress.totalSeconds}s (${progress.percentComplete}%)`
                  : 'starting...'}
              </div>
              {progressHistory.length > 1 && (
                <div className="live-charts">
                  <AxisChart title="Active VUs" points={vusPoints} totalSeconds={chartTotalSeconds} formatY={(v) => v.toFixed(0)} />
                  <AxisChart title="Iterations/s" points={ratePoints} totalSeconds={chartTotalSeconds} formatY={(v) => v.toFixed(0)} />
                </div>
              )}
            </div>
          )}

          {!running && report && <TrafficReportView report={report} />}
        </div>
      )}
    </div>
  )
}
