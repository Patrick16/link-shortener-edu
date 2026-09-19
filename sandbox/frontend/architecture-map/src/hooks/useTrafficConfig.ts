import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { StagePoint } from '../components/StageGraphEditor'
import type { CustomScenario, DataPoolMode, DataSourceDefinition, EndpointDefinition, FlowStep, ScenarioMode, TrafficRequest, TrafficStage } from '../types/controlApi'

const DEFAULT_FLAT_VUS = 10
const DEFAULT_ITERATIONS = 100
const DEFAULT_DATA_POOL_COUNT = 1000

export interface RampPreset {
  id: string
  label: string
  description: string
  totalDurationSeconds: number
  points: StagePoint[]
}

// Load *shape* presets - what kind of test this is has nothing to do with which endpoints get
// called (that's the sequence builder), only with how VUs move over time. Every preset stays fully
// editable on the graph afterwards; picking one is just a sensible starting shape.
export const RAMP_PRESETS: RampPreset[] = [
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

export function pointsToStages(points: StagePoint[]): TrafficStage[] {
  const stages: TrafficStage[] = []
  for (let i = 1; i < points.length; i++) {
    stages.push({ durationSeconds: points[i].t - points[i - 1].t, targetVus: points[i].vus })
  }
  return stages
}

// Owns every input that goes into a traffic run's configuration - split out of the old single
// TrafficPanel so the config UI (left sidebar) and the run's live/latest result (header) can be
// two separate components without duplicating this state or fighting over where it lives.
export function useTrafficConfig() {
  const [endpointOptions, setEndpointOptions] = useState<EndpointDefinition[]>([])
  const [sequence, setSequence] = useState<FlowStep[]>([])
  const [stopMode, setStopMode] = useState<ScenarioMode>('duration')
  const [rampPreset, setRampPreset] = useState(RAMP_PRESETS[0].id)
  const [totalDuration, setTotalDuration] = useState(RAMP_PRESETS[0].totalDurationSeconds)
  const [points, setPoints] = useState<StagePoint[]>(RAMP_PRESETS[0].points)
  const [flatVus, setFlatVus] = useState(DEFAULT_FLAT_VUS)
  const [iterationsTarget, setIterationsTarget] = useState(DEFAULT_ITERATIONS)
  const [scenarioName, setScenarioName] = useState('')

  const [dataSources, setDataSources] = useState<DataSourceDefinition[]>([])
  const [dataPoolEnabled, setDataPoolEnabled] = useState(false)
  const [dataPoolSourceId, setDataPoolSourceId] = useState('')
  const [dataPoolCount, setDataPoolCount] = useState(DEFAULT_DATA_POOL_COUNT)
  const [dataPoolMode, setDataPoolMode] = useState<DataPoolMode>('sequential')

  useEffect(() => {
    controlApi.listEndpoints().then(setEndpointOptions).catch(() => {})
    controlApi.listDataSources().then(setDataSources).catch(() => {})
  }, [])

  // Defaults to whatever the registry offers first, the same pattern EndpointSequenceBuilder uses
  // for its own service/endpoint pickers - there's currently just the one source, but this stays
  // correct if a second one is ever added.
  useEffect(() => {
    if (dataSources.length > 0 && !dataSources.some((s) => s.id === dataPoolSourceId)) {
      setDataPoolSourceId(dataSources[0].id)
    }
  }, [dataSources, dataPoolSourceId])

  // Loads that preset's own starting shape whenever the picker changes. Only meaningful in
  // duration mode - iterations mode has no ramp shape at all (flat VUs by definition).
  useEffect(() => {
    const preset = RAMP_PRESETS.find((p) => p.id === rampPreset)
    if (!preset) return
    setTotalDuration(preset.totalDurationSeconds)
    setPoints(preset.points)
  }, [rampPreset])

  const canRun = sequence.length > 0 && (stopMode === 'duration' ? points.length >= 2 : flatVus >= 1 && iterationsTarget >= 1)

  function buildRequest(): TrafficRequest {
    return {
      scenario: scenarioName.trim() || 'flow',
      vus: stopMode === 'iterations' ? flatVus : (points[0]?.vus ?? 0),
      durationSeconds: totalDuration,
      steps: sequence,
      stages: stopMode === 'duration' ? pointsToStages(points) : undefined,
      iterations: stopMode === 'iterations' ? iterationsTarget : undefined,
      dataPool: dataPoolEnabled && dataPoolSourceId ? { sourceId: dataPoolSourceId, count: dataPoolCount, mode: dataPoolMode } : undefined,
    }
  }

  function loadScenario(saved: CustomScenario) {
    setSequence(saved.steps)
    setStopMode(saved.mode)
    setPoints(saved.points.map((p) => ({ t: p.t, vus: p.vus })))
    setTotalDuration(saved.totalDurationSeconds)
    setFlatVus(saved.vus)
    setIterationsTarget(saved.iterations)
    setDataPoolEnabled(saved.dataPool != null)
    if (saved.dataPool) {
      setDataPoolSourceId(saved.dataPool.sourceId)
      setDataPoolCount(saved.dataPool.count)
      setDataPoolMode(saved.dataPool.mode)
    }
  }

  function resetScenario() {
    setSequence([])
    setStopMode('duration')
    setPoints(RAMP_PRESETS[0].points)
    setTotalDuration(RAMP_PRESETS[0].totalDurationSeconds)
    setFlatVus(DEFAULT_FLAT_VUS)
    setIterationsTarget(DEFAULT_ITERATIONS)
    setDataPoolEnabled(false)
    setDataPoolCount(DEFAULT_DATA_POOL_COUNT)
    setDataPoolMode('sequential')
  }

  return {
    endpointOptions,
    sequence,
    setSequence,
    stopMode,
    setStopMode,
    rampPreset,
    setRampPreset,
    totalDuration,
    setTotalDuration,
    points,
    setPoints,
    flatVus,
    setFlatVus,
    iterationsTarget,
    setIterationsTarget,
    scenarioName,
    setScenarioName,
    dataSources,
    dataPoolEnabled,
    setDataPoolEnabled,
    dataPoolSourceId,
    setDataPoolSourceId,
    dataPoolCount,
    setDataPoolCount,
    dataPoolMode,
    setDataPoolMode,
    canRun,
    buildRequest,
    loadScenario,
    resetScenario,
  }
}

export type TrafficConfigState = ReturnType<typeof useTrafficConfig>
