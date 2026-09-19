import { RAMP_PRESETS, type TrafficConfigState } from '../hooks/useTrafficConfig'
import { StageGraphEditor } from './StageGraphEditor'
import { EndpointSequenceBuilder } from './EndpointSequenceBuilder'
import { DataPoolControls } from './DataPoolControls'
import { CustomScenarioControls } from './CustomScenarioControls'

interface Props {
  config: TrafficConfigState
  disabled: boolean
  running: boolean
  onRun: () => void
}

// Left sidebar: everything that defines what the next traffic run will do - which endpoints, in
// what order, and how much load. Purely config; the run's live progress and finished report live
// in the header (TrafficResultPanel) instead, since this panel's job ends once "Run traffic" fires.
export function TrafficConfigPanel({ config, disabled, running, onRun }: Props) {
  const selectedPresetDescription = RAMP_PRESETS.find((p) => p.id === config.rampPreset)?.description

  return (
    <div className="traffic-config-panel">
      <button className="run-traffic-button" onClick={onRun} disabled={disabled || !config.canRun}>
        {running ? `Running ${config.scenarioName.trim() || 'flow'}...` : 'Run traffic'}
      </button>

      <h3 className="traffic-panel-subheading">What to call</h3>
      <EndpointSequenceBuilder disabled={disabled} endpoints={config.endpointOptions} sequence={config.sequence} onChange={config.setSequence} />

      <h3 className="traffic-panel-subheading">Test data</h3>
      <DataPoolControls
        disabled={disabled}
        sources={config.dataSources}
        enabled={config.dataPoolEnabled}
        onEnabledChange={config.setDataPoolEnabled}
        sourceId={config.dataPoolSourceId}
        onSourceIdChange={config.setDataPoolSourceId}
        count={config.dataPoolCount}
        onCountChange={config.setDataPoolCount}
        mode={config.dataPoolMode}
        onModeChange={config.setDataPoolMode}
      />

      <h3 className="traffic-panel-subheading">How much load</h3>
      <div className="stop-mode-row">
        <label>
          <input type="radio" name="stopMode" checked={config.stopMode === 'duration'} onChange={() => config.setStopMode('duration')} disabled={disabled} />
          Run for a duration
        </label>
        <label>
          <input type="radio" name="stopMode" checked={config.stopMode === 'iterations'} onChange={() => config.setStopMode('iterations')} disabled={disabled} />
          Run a fixed number of iterations
        </label>
      </div>

      {config.stopMode === 'duration' ? (
        <>
          <select value={config.rampPreset} onChange={(e) => config.setRampPreset(e.target.value)} disabled={disabled}>
            {RAMP_PRESETS.map((p) => (
              <option key={p.id} value={p.id}>
                {p.label}
              </option>
            ))}
          </select>
          {selectedPresetDescription && <p className="scenario-description-text">{selectedPresetDescription}</p>}
          <StageGraphEditor
            points={config.points}
            onChange={config.setPoints}
            totalDurationSeconds={config.totalDuration}
            onTotalDurationChange={config.setTotalDuration}
            disabled={disabled}
          />
        </>
      ) : (
        <div className="iterations-config">
          <label>
            VUs
            <input type="number" min={1} value={config.flatVus} onChange={(e) => config.setFlatVus(Math.max(1, Number(e.target.value)))} disabled={disabled} />
          </label>
          <label>
            Iterations
            <input
              type="number"
              min={1}
              max={100_000}
              value={config.iterationsTarget}
              onChange={(e) => config.setIterationsTarget(Math.max(1, Math.min(100_000, Number(e.target.value))))}
              disabled={disabled}
            />
          </label>
          <p className="scenario-description-text">
            Runs until this many iterations complete, shared across the given VUs - however long that takes, independent of any ramp shape.
          </p>
        </div>
      )}

      <h3 className="traffic-panel-subheading">Save this combination</h3>
      <CustomScenarioControls
        disabled={disabled}
        scenarioName={config.scenarioName}
        onScenarioNameChange={config.setScenarioName}
        current={{
          steps: config.sequence,
          mode: config.stopMode,
          totalDurationSeconds: config.totalDuration,
          points: config.points,
          vus: config.flatVus,
          iterations: config.iterationsTarget,
          dataPool: config.dataPoolEnabled && config.dataPoolSourceId ? { sourceId: config.dataPoolSourceId, count: config.dataPoolCount, mode: config.dataPoolMode } : undefined,
        }}
        onLoad={config.loadScenario}
        onReset={config.resetScenario}
      />
    </div>
  )
}
