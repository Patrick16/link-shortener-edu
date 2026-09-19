import type { TrafficReport } from '../types/controlApi'

const LATENCY_ROWS: Array<{ key: 'avg' | 'med' | 'p90' | 'p95' | 'max'; label: string }> = [
  { key: 'avg', label: 'avg' },
  { key: 'med', label: 'median' },
  { key: 'p90', label: 'p90' },
  { key: 'p95', label: 'p95' },
  { key: 'max', label: 'max' },
]

interface Props {
  report: TrafficReport
}

// Pure presentation over one TrafficReport - shared by the "just finished" view (TrafficResultPanel)
// and the "looking at a past run" view (RunHistoryPanel), so the two don't drift into rendering the
// same numbers differently.
export function TrafficReportView({ report }: Props) {
  const maxLatency = report.httpReqDuration ? Math.max(...LATENCY_ROWS.map((r) => report.httpReqDuration![r.key])) : 0

  return (
    <div className="traffic-report">
      <div className="stat-tiles">
        <div className="stat-tile">
          <span className="stat-tile-value">{report.httpRequests}</span>
          <span className="stat-tile-label">requests ({report.httpRequestRate.toFixed(1)}/s)</span>
        </div>
        <div className="stat-tile">
          <span className={report.failedRequests > 0 ? 'stat-tile-value stat-tile-value-danger' : 'stat-tile-value'}>{report.failedRequests}</span>
          <span className="stat-tile-label">failed ({(report.failedRequestRate * 100).toFixed(1)}%)</span>
        </div>
        <div className="stat-tile">
          <span className="stat-tile-value">{report.iterations}</span>
          <span className="stat-tile-label">iterations ({report.iterationRate.toFixed(1)}/s)</span>
        </div>
        <div className="stat-tile">
          <span className="stat-tile-value">{report.vus}</span>
          <span className="stat-tile-label">max VUs</span>
        </div>
        <div className="stat-tile">
          <span className="stat-tile-value">{report.exitCode}</span>
          <span className="stat-tile-label">exit code</span>
        </div>
      </div>

      {report.httpReqDuration && (
        <div className="latency-chart">
          <h3>http_req_duration</h3>
          {LATENCY_ROWS.map((row) => {
            const value = report.httpReqDuration![row.key]
            const widthPct = maxLatency > 0 ? (value / maxLatency) * 100 : 0
            return (
              <div className="latency-row" key={row.key}>
                <span className="latency-row-label">{row.label}</span>
                <div className="latency-bar-track">
                  <div className="latency-bar-fill" style={{ width: `${widthPct}%` }} />
                </div>
                <span className="latency-row-value">{value.toFixed(2)}ms</span>
              </div>
            )
          })}
        </div>
      )}

      {report.checks.length > 0 && (
        <div className="checks-list">
          <h3>Checks</h3>
          {report.checks.map((check) => {
            const total = check.passes + check.fails
            const passPct = total > 0 ? (check.passes / total) * 100 : 0
            return (
              <div className="check-row" key={check.name}>
                <span className="check-row-label">{check.name}</span>
                <div className="check-bar-track">
                  <div className={passPct >= 50 ? 'check-bar-fill' : 'check-bar-fill check-bar-fill-critical'} style={{ width: `${passPct}%` }} />
                </div>
                <span className="check-row-value">
                  {check.passes}/{total}
                </span>
              </div>
            )
          })}
        </div>
      )}

      {report.statusBreakdownByEndpoint.length > 0 && (
        <div className="status-list">
          <h3>Status codes by endpoint</h3>
          {report.statusBreakdownByEndpoint.map((endpoint) => {
            const endpointTotal = endpoint.statusCounts.reduce((sum, s) => sum + s.count, 0)
            return (
              <div className="status-endpoint-group" key={endpoint.endpointId}>
                <div className="status-endpoint-label">{endpoint.endpointId}</div>
                {endpoint.statusCounts.map((status) => {
                  const widthPct = endpointTotal > 0 ? (status.count / endpointTotal) * 100 : 0
                  const isSuccess = status.label.startsWith('2') || status.label.startsWith('3')
                  return (
                    <div className="status-row" key={status.label}>
                      <span className="status-row-label">{status.label}</span>
                      <div className="status-bar-track">
                        <div className={isSuccess ? 'status-bar-fill' : 'status-bar-fill status-bar-fill-critical'} style={{ width: `${widthPct}%` }} />
                      </div>
                      <span className="status-row-value">{status.count}</span>
                    </div>
                  )
                })}
              </div>
            )
          })}
        </div>
      )}

      <details className="traffic-raw">
        <summary>Raw k6 output</summary>
        <pre>{report.rawOutput}</pre>
      </details>
    </div>
  )
}
