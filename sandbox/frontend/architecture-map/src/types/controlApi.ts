// Mirrors sandbox/infra/control-api/Models/*.cs - keep in sync by hand, no shared schema.

export interface ManagedContainer {
  serviceId: string
  containerId: string
  state: string
  status: string
  containerNumber: number
}

export interface ScaleRequest {
  replicas: number
}

export interface ScaleResult {
  serviceId: string
  replicas: number
  success: boolean
  output: string
}

export type ChaosType = 'Delay' | 'Loss' | 'Partition'

export interface ChaosRequest {
  type: ChaosType
  amount: number
  durationSeconds: number
}

export interface ChaosAction {
  serviceId: string
  chaosContainerId: string
  type: ChaosType
  durationSeconds: number
  startedAt: string
}

export interface TrafficStage {
  durationSeconds: number
  targetVus: number
}

export interface TrafficRequest {
  scenario: string
  vus: number
  durationSeconds: number
  endpoints: string[]
  stages?: TrafficStage[]
}

// One real HTTP route the flow runner can call - see EndpointDefinition on the backend.
// PathTemplate/BodyTemplate use "{{varName}}" placeholders resolved by k6-scripts/flow.js.
export interface EndpointDefinition {
  id: string
  serviceId: string
  method: string
  pathTemplate: string
  bodyTemplate: string | null
  produces: Record<string, string>
  consumes: string[]
  description: string
}

// Persisted exactly as the graph edits it (time + VUs at that point) - see ScenarioPoint on the
// backend for why storing points beats pre-converting to k6 stages.
export interface ScenarioPoint {
  t: number
  vus: number
}

export interface CustomScenario {
  name: string
  endpoints: string[]
  totalDurationSeconds: number
  points: ScenarioPoint[]
}

// NginxBypassed is the one field framed as "the interesting state", not "is it on" - see
// InfraStatus on the backend.
export interface InfraStatus {
  nginxBypassed: boolean
  pgcatEnabled: boolean
  cacheEnabled: boolean
}

export interface LatencyStats {
  avg: number
  min: number
  med: number
  max: number
  p90: number
  p95: number
}

export interface CheckResult {
  name: string
  passes: number
  fails: number
}

export interface StatusCount {
  label: string
  count: number
}

export interface TrafficReport {
  scenario: string
  exitCode: number
  httpRequests: number
  httpRequestRate: number
  failedRequests: number
  failedRequestRate: number
  iterations: number
  iterationRate: number
  vus: number
  httpReqDuration: LatencyStats | null
  checks: CheckResult[]
  statusBreakdown: StatusCount[]
  rawOutput: string
}

export interface TrafficProgress {
  elapsedSeconds: number
  totalSeconds: number
  percentComplete: number
  activeVus: number
  iterationsSoFar: number
  iterationsPerSecond: number
}

export interface ResourceSample {
  serviceId: string
  cpuPercent: number
  memoryUsageBytes: number
  memoryLimitBytes: number
  timestamp: string
}
