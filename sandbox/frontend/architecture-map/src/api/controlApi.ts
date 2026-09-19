import type {
  ChaosRequest,
  ChaosAction,
  CustomScenario,
  EndpointDefinition,
  InfraStatus,
  ManagedContainer,
  PgcatConnectionStats,
  PostgresConnectionStats,
  ResourceSample,
  RunSnapshot,
  RunSummary,
  ScaleResult,
  TrafficRequest,
} from '../types/controlApi'

const BASE_URL = import.meta.env.VITE_CONTROL_API_URL || 'http://localhost:5299'

export class ControlApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ControlApiError'
    this.status = status
  }
}

async function send(path: string, method: 'GET' | 'POST' | 'DELETE', body?: unknown): Promise<Response> {
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

async function request<T>(path: string, method: 'GET' | 'POST' | 'DELETE' = 'GET', body?: unknown): Promise<T> {
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

  listScalableServices: () => request<string[]>('/api/containers/scalable'),

  scale: (serviceId: string, replicas: number) =>
    request<ScaleResult>(`/api/containers/${serviceId}/scale`, 'POST', { replicas }),

  // Fire-and-forget: the run itself is reported over SignalR (trafficProgress/trafficCompleted/
  // trafficFailed), not in this response - see useTrafficRun.
  startTraffic: async (traffic: TrafficRequest): Promise<void> => {
    await send('/api/traffic', 'POST', traffic)
  },

  flushRedisCache: () => request<{ flushed: string }>('/api/containers/redis/flush-cache', 'POST'),

  listEndpoints: () => request<EndpointDefinition[]>('/api/endpoints'),

  getInfraStatus: () => request<InfraStatus>('/api/infra/status'),
  setNginxEnabled: (enabled: boolean) => request<InfraStatus>('/api/infra/nginx', 'POST', { enabled }),
  setPgcatEnabled: (enabled: boolean) => request<InfraStatus>('/api/infra/pgcat', 'POST', { enabled }),
  setCacheEnabled: (enabled: boolean) => request<InfraStatus>('/api/infra/cache', 'POST', { enabled }),

  listScenarios: () => request<CustomScenario[]>('/api/scenarios'),
  saveScenario: (scenario: CustomScenario) => request<CustomScenario>('/api/scenarios', 'POST', scenario),
  deleteScenario: (name: string) => send(`/api/scenarios/${encodeURIComponent(name)}`, 'DELETE'),

  getPgcatConnections: () => request<PgcatConnectionStats>('/api/containers/pgcat/connections'),
  getPostgresConnections: () => request<PostgresConnectionStats>('/api/containers/postgres/connections'),

  listRuns: () => request<RunSummary[]>('/api/runs'),
  getRun: (id: string) => request<RunSnapshot>(`/api/runs/${encodeURIComponent(id)}`),
}
