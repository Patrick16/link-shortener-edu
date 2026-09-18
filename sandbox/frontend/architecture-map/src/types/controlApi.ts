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

export interface TrafficResult {
  scenario: string
  exitCode: number
  output: string
}
