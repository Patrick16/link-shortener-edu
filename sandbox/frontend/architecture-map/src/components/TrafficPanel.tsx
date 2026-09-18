import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { TrafficRunState } from '../hooks/useTrafficRun'
import type { TrafficScenarioInfo } from '../types/controlApi'
import { AxisChart } from './AxisChart'

const LATENCY_ROWS: Array<{ key: 'avg' | 'med' | 'p90' | 'p95' | 'max'; label: string }> = [
  { key: 'avg', label: 'avg' },
  { key: 'med', label: 'median' },
  { key: 'p90', label: 'p90' },
  { key: 'p95', label: 'p95' },
  { key: 'max', label: 'max' },
]

// Driven by a useTrafficRun() instance owned by App (not this component) - the graph needs to
// know whether traffic is running too, to animate the edges the flow actually exercises.
export function TrafficPanel({ running, progress, progressHistory, report, error, start }: TrafficRunState) {
  const [scenarios, setScenarios] = useState<TrafficScenarioInfo[]>([])
  const [scenario, setScenario] = useState('')
  const [vus, setVus] = useState(3)
  const [duration, setDuration] = useState(10)

  useEffect(() => {
    controlApi
      .listTrafficScenarios()
      .then((list) => {
        setScenarios(list)
        if (list.length > 0) setScenario(list[0].name)
      })
      .catch(() => {})
  }, [])

  const maxLatency = report?.httpReqDuration ? Math.max(...LATENCY_ROWS.map((r) => report.httpReqDuration![r.key])) : 0
  const selectedDescription = scenarios.find((s) => s.name === scenario)?.description
  const totalSeconds = progress?.totalSeconds ?? duration
  const vusPoints = progressHistory.map((p) => ({ x: p.elapsedSeconds, y: p.activeVus }))
  const ratePoints = progressHistory.map((p) => ({ x: p.elapsedSeconds, y: p.iterationsPerSecond }))

  return (
    <div className="traffic-panel">
      <div className="traffic-panel-controls">
        <select value={scenario} onChange={(e) => setScenario(e.target.value)} disabled={running || scenarios.length === 0}>
          {scenarios.length === 0 && <option>No scenarios available</option>}
          {scenarios.map((s) => (
            <option key={s.name} value={s.name}>
              {s.name}
            </option>
          ))}
        </select>
        <label>
          VUs
          <input type="number" value={vus} onChange={(e) => setVus(Number(e.target.value))} min={1} disabled={running} />
        </label>
        <label>
          Duration (s)
          <input type="number" value={duration} onChange={(e) => setDuration(Number(e.target.value))} min={1} max={120} disabled={running} />
        </label>
        <button onClick={() => start({ scenario, vus, durationSeconds: duration })} disabled={running || !scenario}>
          {running ? `Running ${scenario}...` : 'Run traffic'}
        </button>
      </div>

      {selectedDescription && <p className="scenario-description-text">{selectedDescription}</p>}

      {error && <p className="service-card-error">{error}</p>}

      {running && (
        <div className="traffic-progress">
          <div className="progress-bar-track">
            <div className="progress-bar-fill" style={{ width: `${progress?.percentComplete ?? 0}%` }} />
          </div>
          <div className="progress-bar-label">
            {progress ? `${progress.elapsedSeconds}s / ${progress.totalSeconds}s (${progress.percentComplete}%)` : 'starting...'}
          </div>
          {progressHistory.length > 1 && (
            <div className="live-charts">
              <AxisChart title="Active VUs" points={vusPoints} totalSeconds={totalSeconds} formatY={(v) => v.toFixed(0)} />
              <AxisChart title="Iterations/s" points={ratePoints} totalSeconds={totalSeconds} formatY={(v) => v.toFixed(0)} />
            </div>
          )}
        </div>
      )}

      {report && (
        <div className="traffic-report">
          <div className="stat-tiles">
            <div className="stat-tile">
              <span className="stat-tile-value">{report.httpRequests}</span>
              <span className="stat-tile-label">requests ({report.httpRequestRate.toFixed(1)}/s)</span>
            </div>
            <div className="stat-tile">
              <span className={report.failedRequests > 0 ? 'stat-tile-value stat-tile-value-danger' : 'stat-tile-value'}>
                {report.failedRequests}
              </span>
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
                      <div
                        className={passPct >= 50 ? 'check-bar-fill' : 'check-bar-fill check-bar-fill-critical'}
                        style={{ width: `${passPct}%` }}
                      />
                    </div>
                    <span className="check-row-value">
                      {check.passes}/{total}
                    </span>
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
      )}
    </div>
  )
}
