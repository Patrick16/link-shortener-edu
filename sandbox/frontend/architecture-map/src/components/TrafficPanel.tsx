import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { TrafficRunState } from '../hooks/useTrafficRun'
import type { TrafficScenarioInfo, TrafficStage } from '../types/controlApi'
import { AxisChart } from './AxisChart'
import { StageGraphEditor, type StagePoint } from './StageGraphEditor'
import { CustomScenarioControls } from './CustomScenarioControls'

// Not a real k6-scripts/*.js name (custom.js is excluded from ListTrafficScenarios on purpose) -
// picking it switches the panel into "choose your own endpoints" mode instead of a named script.
const CUSTOM_VALUE = '__custom__'

const LATENCY_ROWS: Array<{ key: 'avg' | 'med' | 'p90' | 'p95' | 'max'; label: string }> = [
  { key: 'avg', label: 'avg' },
  { key: 'med', label: 'median' },
  { key: 'p90', label: 'p90' },
  { key: 'p95', label: 'p95' },
  { key: 'max', label: 'max' },
]

interface Preset {
  totalDurationSeconds: number
  points: StagePoint[]
}

// Starting shape for each scenario's load profile - picked to actually look like what the name
// promises (a flat smoke check, a sharp spike, a gradual read-heavy ramp) rather than one generic
// default, but every point stays draggable afterwards. Keyed by k6-scripts/<name>.js's own name.
const PRESETS: Record<string, Preset> = {
  smoke: {
    totalDurationSeconds: 10,
    points: [
      { t: 0, vus: 3 },
      { t: 10, vus: 3 },
    ],
  },
  spike: {
    totalDurationSeconds: 20,
    points: [
      { t: 0, vus: 0 },
      { t: 3, vus: 50 },
      { t: 13, vus: 50 },
      { t: 20, vus: 0 },
    ],
  },
  'read-heavy': {
    totalDurationSeconds: 20,
    points: [
      { t: 0, vus: 0 },
      { t: 5, vus: 20 },
      { t: 20, vus: 20 },
    ],
  },
}
const DEFAULT_PRESET: Preset = { totalDurationSeconds: 10, points: [{ t: 0, vus: 3 }, { t: 10, vus: 3 }] }

// Consecutive points become k6 --stage segments - k6 only needs "ramp/hold to this many VUs over
// this many seconds", it has no notion of the graph's absolute time axis the UI edits in.
function pointsToStages(points: StagePoint[]): TrafficStage[] {
  const stages: TrafficStage[] = []
  for (let i = 1; i < points.length; i++) {
    stages.push({ durationSeconds: points[i].t - points[i - 1].t, targetVus: points[i].vus })
  }
  return stages
}

// Driven by a useTrafficRun() instance owned by App (not this component) - the graph needs to
// know whether traffic is running too, to animate the edges the flow actually exercises.
export function TrafficPanel({ running, progress, progressHistory, report, error, start }: TrafficRunState) {
  const [scenarios, setScenarios] = useState<TrafficScenarioInfo[]>([])
  const [scenario, setScenario] = useState('')
  const [totalDuration, setTotalDuration] = useState(DEFAULT_PRESET.totalDurationSeconds)
  const [points, setPoints] = useState<StagePoint[]>(DEFAULT_PRESET.points)
  const [selectedEndpoints, setSelectedEndpoints] = useState<string[]>(['create', 'redirect'])

  useEffect(() => {
    controlApi
      .listTrafficScenarios()
      .then((list) => {
        setScenarios(list)
        if (list.length > 0) setScenario(list[0].name)
      })
      .catch(() => {})
  }, [])

  // Loads that scenario's own starting profile whenever the picker changes - including the very
  // first time it's set from the scenario list above.
  useEffect(() => {
    if (!scenario) return
    if (scenario === CUSTOM_VALUE) {
      setSelectedEndpoints(['create', 'redirect'])
    }
    const preset = PRESETS[scenario] ?? DEFAULT_PRESET
    setTotalDuration(preset.totalDurationSeconds)
    setPoints(preset.points)
  }, [scenario])

  const isCustom = scenario === CUSTOM_VALUE
  const maxLatency = report?.httpReqDuration ? Math.max(...LATENCY_ROWS.map((r) => report.httpReqDuration![r.key])) : 0
  const selectedDescription = scenarios.find((s) => s.name === scenario)?.description
  const totalSeconds = progress?.totalSeconds ?? totalDuration
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
          <option value={CUSTOM_VALUE}>— Custom (choose endpoints) —</option>
        </select>
        <button
          onClick={() =>
            start({
              scenario: isCustom ? 'custom' : scenario,
              vus: points[0]?.vus ?? 0,
              durationSeconds: totalDuration,
              stages: pointsToStages(points),
              endpoints: isCustom ? selectedEndpoints : undefined,
            })
          }
          disabled={running || !scenario || points.length < 2 || (isCustom && selectedEndpoints.length === 0)}
        >
          {running ? `Running ${isCustom ? 'custom' : scenario}...` : 'Run traffic'}
        </button>
      </div>

      {isCustom ? (
        <>
          <p className="scenario-description-text">
            Pick which endpoints to generate load against - each iteration hits a random one from your selection - then draw the
            ramp below. Save a combination by name to reuse or tweak it later.
          </p>
          <CustomScenarioControls
            disabled={running}
            selectedEndpoints={selectedEndpoints}
            onEndpointsChange={setSelectedEndpoints}
            points={points}
            totalDurationSeconds={totalDuration}
            onLoad={(saved) => {
              setSelectedEndpoints(saved.endpoints)
              setPoints(saved.points.map((p) => ({ t: p.t, vus: p.vus })))
              setTotalDuration(saved.totalDurationSeconds)
            }}
            onReset={() => {
              setSelectedEndpoints(['create', 'redirect'])
              setPoints(DEFAULT_PRESET.points)
              setTotalDuration(DEFAULT_PRESET.totalDurationSeconds)
            }}
          />
        </>
      ) : (
        selectedDescription && <p className="scenario-description-text">{selectedDescription}</p>
      )}

      <StageGraphEditor points={points} onChange={setPoints} totalDurationSeconds={totalDuration} onTotalDurationChange={setTotalDuration} disabled={running} />

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

          {report.statusBreakdown.length > 0 && (
            <div className="status-list">
              <h3>Status codes</h3>
              {report.statusBreakdown.map((status) => {
                const widthPct = report.httpRequests > 0 ? (status.count / report.httpRequests) * 100 : 0
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
