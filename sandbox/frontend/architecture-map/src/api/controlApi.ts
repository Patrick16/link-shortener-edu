import type { ChaosRequest, ChaosAction, ManagedContainer, ResourceSample, TrafficRequest } from '../types/controlApi'

const BASE_URL = import.meta.env.VITE_CONTROL_API_URL || 'http://localhost:5299'

export class ControlApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ControlApiError'
    this.status = status
  }
}

async function send(path: string, method: 'GET' | 'POST', body?: unknown): Promise<Response> {
  const response = await fetch(`${BASE_URL}${path}`, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })

  if (!response.ok) {
    const message = await response.text().catch(() => '')
    throw new ControlApiError(response.status, message || response.statusText)
  }

  return response
}

async function request<T>(path: string, method: 'GET' | 'POST' = 'GET', body?: unknown): Promise<T> {
  return (await send(path, method, body)).json() as Promise<T>
}

export const controlApi = {
  hubUrl: `${BASE_URL}/hub/status`,

  listContainers: () => request<ManagedContainer[]>('/api/containers'),

  stop: (serviceId: string) => request<ManagedContainer>(`/api/containers/${serviceId}/stop`, 'POST'),
  start: (serviceId: string) => request<ManagedContainer>(`/api/containers/${serviceId}/start`, 'POST'),
  restart: (serviceId: string) => request<ManagedContainer>(`/api/containers/${serviceId}/restart`, 'POST'),

  degrade: (serviceId: string, chaos: ChaosRequest) =>
    request<ChaosAction>(`/api/containers/${serviceId}/degrade`, 'POST', chaos),

  heal: (serviceId: string) => request<{ stopped: number }>(`/api/containers/${serviceId}/heal`, 'POST'),

  statsHistory: (serviceId: string) => request<ResourceSample[]>(`/api/containers/${serviceId}/stats/history`),

  listTrafficScenarios: () => request<string[]>('/api/traffic/scenarios'),

  // Fire-and-forget: the run itself is reported over SignalR (trafficProgress/trafficCompleted/
  // trafficFailed), not in this response - see useTrafficRun.
  startTraffic: async (traffic: TrafficRequest): Promise<void> => {
    await send('/api/traffic', 'POST', traffic)
  },
}
