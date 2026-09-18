// Mirrors sandbox/infra/control-api/Models/*.cs - keep in sync by hand, no shared schema.

export interface ManagedContainer {
  serviceId: string
  containerId: string
  state: string
  status: string
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

export interface TrafficRequest {
  scenario: string
  vus: number
  durationSeconds: number
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

export interface TrafficReport {
  scenario: string
  exitCode: number
  httpRequests: number
  httpRequestRate: number
  iterations: number
  iterationRate: number
  vus: number
  httpReqDuration: LatencyStats | null
  checks: CheckResult[]
  rawOutput: string
}

export interface TrafficProgress {
  elapsedSeconds: number
  totalSeconds: number
  percentComplete: number
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
