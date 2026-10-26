import { useEffect, useState } from 'react'
import { AxisChart } from './AxisChart'
import { TrafficReportModal } from './TrafficReportModal'
import type { TrafficRunState } from '../hooks/useTrafficRun'

interface Props
  extends Pick<TrafficRunState, 'running' | 'progress' | 'progressHistory' | 'report' | 'error' | 'cancelled' | 'cancel' | 'verdict' | 'traceHops'> {
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

// Header strip: while a run is active, its live progress bar + charts, collapsible the same way as
// before. Once it finishes, the report no longer grows the strip inline - it pops up in
// TrafficReportModal instead (auto-opened once, reopenable via "View results"), so a tall report
// never pushes the graph/sidebars below the fold. Config lives in the left sidebar instead - this
// panel only ever reflects a run already in flight or just completed, never edits anything.
export function TrafficResultPanel({ running, progress, progressHistory, report, error, cancelled, cancel, verdict, traceHops, fallbackTotalSeconds }: Props) {
  const [collapsed, setCollapsed] = useState(readCollapsed)
  const [cancelling, setCancelling] = useState(false)
  const [modalOpen, setModalOpen] = useState(false)

  // Each finished run hands back a fresh `report` object, so this fires exactly once per
  // completion - popping the modal open automatically instead of making the user hunt for a
  // "view results" button right after the run they were just watching ends.
  useEffect(() => {
    if (report) setModalOpen(true)
  }, [report])

  async function handleCancel() {
    setCancelling(true)
    try {
      await cancel()
    } finally {
      setCancelling(false)
    }
  }

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

  if (!running && !report && !error && !cancelled) {
    return <div className="traffic-result-panel traffic-result-empty">Configure a run on the left, then hit Run traffic.</div>
  }

  return (
    <div className="traffic-result-panel">
      <div className="traffic-result-header">
        <span className="traffic-result-header-label">
          {running
            ? progress?.phase === 'preparing'
              ? 'Preparing test data...'
              : 'Running...'
            : report
              ? `Last run - ${report.httpRequests} requests, ${report.failedRequests} failed`
              : cancelled
                ? 'Run cancelled'
                : 'Last run'}
        </span>
        <div className="traffic-result-header-actions">
          {running && (
            <button type="button" className="traffic-result-cancel" onClick={handleCancel} disabled={cancelling}>
              {cancelling ? 'Cancelling...' : 'Cancel run'}
            </button>
          )}
          {running ? (
            <button type="button" className="traffic-result-toggle" onClick={toggleCollapsed}>
              {collapsed ? 'Show details' : 'Hide details'}
            </button>
          ) : (
            report && (
              <button type="button" className="traffic-result-toggle" onClick={() => setModalOpen(true)}>
                View results
              </button>
            )
          )}
        </div>
      </div>

      {error && <p className="service-card-error">{error}</p>}

      {running && !collapsed && (
        <div className="traffic-result-content">
          {progress?.phase === 'preparing' && (
            <div className="traffic-progress">
              <div className="progress-bar-track">
                <div className="progress-bar-fill" style={{ width: `${progress.percentComplete}%` }} />
              </div>
              <div className="progress-bar-label">
                Fetching test data - {progress.preparedCount ?? 0}/{progress.preparedTarget ?? '?'} ({progress.percentComplete}%)
              </div>
            </div>
          )}

          {progress?.phase !== 'preparing' && (
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
        </div>
      )}

      {modalOpen && report && (
        <TrafficReportModal
          title="Last run"
          subtitle={`${report.httpRequests} requests, ${report.failedRequests} failed`}
          report={report}
          verdict={verdict}
          traceHops={traceHops}
          onClose={() => setModalOpen(false)}
        />
      )}
    </div>
  )
}
