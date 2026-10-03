import { applyInfraConfig } from './infraConfig'
import type { CustomScenario, RunSnapshot, ScenarioPoint, TrafficRequest } from '../types/controlApi'
import type { ApplySystemConfigResult } from './infraConfig'

// Inverse of useTrafficConfig's pointsToStages - a past run only kept the pre-expanded Stages, so
// reconstructing the ramp graph's own Points back out of them (for loadScenario) means walking the
// same durations back into cumulative time, starting from the run's own starting Vus.
function stagesToPoints(request: TrafficRequest): ScenarioPoint[] {
  const points: ScenarioPoint[] = [{ t: 0, vus: request.vus }]
  let t = 0
  for (const stage of request.stages ?? []) {
    t += stage.durationSeconds
    points.push({ t, vus: stage.targetVus })
  }
  return points
}

// Adapts a past run's TrafficRequest into the CustomScenario shape useTrafficConfig.loadScenario
// already knows how to apply - the two shapes differ only in that TrafficRequest carries pre-expanded
// Stages while CustomScenario carries the graph's own Points/mode.
export function runRequestToScenario(request: TrafficRequest): CustomScenario {
  const isIterations = request.iterations != null
  const points = isIterations ? [] : stagesToPoints(request)
  return {
    name: request.scenario,
    steps: request.steps,
    mode: isIterations ? 'iterations' : 'duration',
    totalDurationSeconds: isIterations ? request.durationSeconds : (points.at(-1)?.t ?? request.durationSeconds),
    points,
    vus: isIterations ? request.vus : (points[0]?.vus ?? request.vus),
    iterations: request.iterations ?? 0,
    dataPool: request.dataPool,
  }
}

export type { ApplySystemConfigResult }

// A past run's RunSnapshot already carries every field InfraConfigSnapshot needs (same names), so
// re-applying its system config is just applyInfraConfig under a name that reads better at this
// call site - the actual capture/apply logic lives in utils/infraConfig.ts, shared with the
// Presets feature's own "apply a saved preset."
export const applySystemConfig = applyInfraConfig satisfies (snapshot: RunSnapshot) => Promise<ApplySystemConfigResult>
