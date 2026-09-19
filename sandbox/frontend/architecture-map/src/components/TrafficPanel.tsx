import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { TrafficRunState } from '../hooks/useTrafficRun'
import type { EndpointDefinition, FlowStep, ScenarioMode, TrafficStage } from '../types/controlApi'
import { AxisChart } from './AxisChart'
import { StageGraphEditor, type StagePoint } from './StageGraphEditor'
import { EndpointSequenceBuilder } from './EndpointSequenceBuilder'
import { CustomScenarioControls } from './CustomScenarioControls'

const DEFAULT_FLAT_VUS = 10
const DEFAULT_ITERATIONS = 100

const LATENCY_ROWS: Array<{ key: 'avg' | 'med' | 'p90' | 'p95' | 'max'; label: string }> = [
  { key: 'avg', label: 'avg' },
  { key: 'med', label: 'median' },
  { key: 'p90', label: 'p90' },
  { key: 'p95', label: 'p95' },
  { key: 'max', label: 'max' },
]

interface RampPreset {
  id: string
  label: string
  description: string
  totalDurationSeconds: number
  points: StagePoint[]
}

// Load *shape* presets - what kind of test this is has nothing to do with which endpoints get
// called (that's the sequence builder below), only with how VUs move over time. Every preset stays
// fully editable on the graph afterwards; picking one is just a sensible starting shape.
const RAMP_PRESETS: RampPreset[] = [
  {
    id: 'load',
    label: 'Load Testing',
    description: 'Steady expected concurrency, held for the whole run - confirms the system meets normal performance expectations under typical traffic.',
    totalDurationSeconds: 30,
    points: [
      { t: 0, vus: 10 },
      { t: 30, vus: 10 },
    ],
  },
  {
    id: 'stress',
    label: 'Stress Testing',
    description: 'Climbs steadily well past normal levels - watch the live error rate and status codes to find the point where the system starts to fail, and how it behaves past that point.',
    totalDurationSeconds: 60,
    points: [
      { t: 0, vus: 0 },
      { t: 60, vus: 150 },
    ],
  },
  {
    id: 'spike',
    label: 'Spike Testing',
    description: 'A sudden jump to a high VU count, held briefly, then a sudden drop - simulates a burst of traffic (e.g. from a news mention) rather than gradual growth.',
    totalDurationSeconds: 20,
    points: [
      { t: 0, vus: 0 },
      { t: 3, vus: 80 },
      { t: 13, vus: 80 },
      { t: 20, vus: 0 },
    ],
  },
  {
    id: 'soak',
    label: 'Soak / Endurance Testing',
    description: 'Moderate load held for as long as this tool allows (real soak tests run for hours) - useful for spotting problems that only show up over time, like slow memory growth or degrading latency.',
    totalDurationSeconds: 300,
    points: [
      { t: 0, vus: 12 },
      { t: 300, vus: 12 },
    ],
  },
]

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
  const [endpointOptions, setEndpointOptions] = useState<EndpointDefinition[]>([])
  const [sequence, setSequence] = useState<FlowStep[]>([])
  const [stopMode, setStopMode] = useState<ScenarioMode>('duration')
  const [rampPreset, setRampPreset] = useState(RAMP_PRESETS[0].id)
  const [totalDuration, setTotalDuration] = useState(RAMP_PRESETS[0].totalDurationSeconds)
  const [points, setPoints] = useState<StagePoint[]>(RAMP_PRESETS[0].points)
  const [flatVus, setFlatVus] = useState(DEFAULT_FLAT_VUS)
  const [iterationsTarget, setIterationsTarget] = useState(DEFAULT_ITERATIONS)
  const [scenarioName, setScenarioName] = useState('')

  useEffect(() => {
    controlApi.listEndpoints().then(setEndpointOptions).catch(() => {})
  }, [])

  // Loads that preset's own starting shape whenever the picker changes. Only meaningful in
  // duration mode - iterations mode has no ramp shape at all (flat VUs by definition).
  useEffect(() => {
    const preset = RAMP_PRESETS.find((p) => p.id === rampPreset)
    if (!preset) return
    setTotalDuration(preset.totalDurationSeconds)
    setPoints(preset.points)
  }, [rampPreset])

  const maxLatency = report?.httpReqDuration ? Math.max(...LATENCY_ROWS.map((r) => report.httpReqDuration![r.key])) : 0
  const selectedPresetDescription = RAMP_PRESETS.find((p) => p.id === rampPreset)?.description
  // No fixed total duration in iterations mode - the live charts' X axis just tracks how far
  // elapsed has gotten so far instead of a known end point.
  const chartTotalSeconds = progress?.targetIterations != null ? Math.max(1, progress.elapsedSeconds) : (progress?.totalSeconds ?? totalDuration)
  const vusPoints = progressHistory.map((p) => ({ x: p.elapsedSeconds, y: p.activeVus }))
  const ratePoints = progressHistory.map((p) => ({ x: p.elapsedSeconds, y: p.iterationsPerSecond }))
  const canRun = sequence.length > 0 && (stopMode === 'duration' ? points.length >= 2 : flatVus >= 1 && iterationsTarget >= 1)

  function handleRun() {
    start({
      scenario: scenarioName.trim() || 'flow',
      vus: stopMode === 'iterations' ? flatVus : (points[0]?.vus ?? 0),
      durationSeconds: totalDuration,
      steps: sequence,
      stages: stopMode === 'duration' ? pointsToStages(points) : undefined,
      iterations: stopMode === 'iterations' ? iterationsTarget : undefined,
    })
  }

  return (
    <div className="traffic-panel">
      <div className="traffic-panel-controls">
        <button onClick={handleRun} disabled={running || !canRun}>
          {running ? `Running ${scenarioName.trim() || 'flow'}...` : 'Run traffic'}
        </button>
      </div>

      <h3 className="traffic-panel-subheading">What to call</h3>
      <EndpointSequenceBuilder disabled={running} endpoints={endpointOptions} sequence={sequence} onChange={setSequence} />

      <h3 className="traffic-panel-subheading">How much load</h3>
      <div className="stop-mode-row">
        <label>
          <input type="radio" name="stopMode" checked={stopMode === 'duration'} onChange={() => setStopMode('duration')} disabled={running} />
          Run for a duration
        </label>
        <label>
          <input type="radio" name="stopMode" checked={stopMode === 'iterations'} onChange={() => setStopMode('iterations')} disabled={running} />
          Run a fixed number of iterations
        </label>
      </div>

      {stopMode === 'duration' ? (
        <>
          <select value={rampPreset} onChange={(e) => setRampPreset(e.target.value)} disabled={running}>
            {RAMP_PRESETS.map((p) => (
              <option key={p.id} value={p.id}>
                {p.label}
              </option>
            ))}
          </select>
          {selectedPresetDescription && <p className="scenario-description-text">{selectedPresetDescription}</p>}
          <StageGraphEditor points={points} onChange={setPoints} totalDurationSeconds={totalDuration} onTotalDurationChange={setTotalDuration} disabled={running} />
        </>
      ) : (
        <div className="iterations-config">
          <label>
            VUs
            <input type="number" min={1} value={flatVus} onChange={(e) => setFlatVus(Math.max(1, Number(e.target.value)))} disabled={running} />
          </label>
          <label>
            Iterations
            <input
              type="number"
              min={1}
              max={100_000}
              value={iterationsTarget}
              onChange={(e) => setIterationsTarget(Math.max(1, Math.min(100_000, Number(e.target.value))))}
              disabled={running}
            />
          </label>
          <p className="scenario-description-text">
            Runs until this many iterations complete, shared across the given VUs - however long that takes, independent of any ramp shape.
          </p>
        </div>
      )}

      <h3 className="traffic-panel-subheading">Save this combination</h3>
      <CustomScenarioControls
        disabled={running}
        scenarioName={scenarioName}
        onScenarioNameChange={setScenarioName}
        current={{
          steps: sequence,
          mode: stopMode,
          totalDurationSeconds: totalDuration,
          points,
          vus: flatVus,
          iterations: iterationsTarget,
        }}
        onLoad={(saved) => {
          setSequence(saved.steps)
          setStopMode(saved.mode)
          setPoints(saved.points.map((p) => ({ t: p.t, vus: p.vus })))
          setTotalDuration(saved.totalDurationSeconds)
          setFlatVus(saved.vus)
          setIterationsTarget(saved.iterations)
        }}
        onReset={() => {
          setSequence([])
          setStopMode('duration')
          setPoints(RAMP_PRESETS[0].points)
          setTotalDuration(RAMP_PRESETS[0].totalDurationSeconds)
          setFlatVus(DEFAULT_FLAT_VUS)
          setIterationsTarget(DEFAULT_ITERATIONS)
        }}
      />

      {error && <p className="service-card-error">{error}</p>}

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
