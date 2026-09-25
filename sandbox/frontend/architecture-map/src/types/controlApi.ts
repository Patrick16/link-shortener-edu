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

// Which endpoint, and how long to sleep afterward before the next step (or the next iteration) -
// see FlowStep on the backend, in particular why this is what lets the well-known create-then-
// resolve race be fixed (or deliberately left alone) from the UI instead of a hardcoded sleep.
export interface FlowStep {
  endpointId: string
  pauseAfterSeconds: number
}

// Picks how each iteration draws from a preloaded DataPoolRequest - "sequential" cycles through it
// in order (spread across VUs/iterations so concurrent draws don't collide), "random" picks
// independently each time. See DataPoolRequest on the backend.
export type DataPoolMode = 'sequential' | 'random'

// Preloads Count real values from a DataSourceDefinition before the run starts and seeds them into
// every iteration, so a step consuming that variable without an earlier step in the same sequence
// producing it fresh (e.g. testing "Resolve link" alone) hits varied real records instead of the
// same one fixture value every time - see DataPoolRequest on the backend.
export interface DataPoolRequest {
  sourceId: string
  count: number
  mode: DataPoolMode
}

export interface TrafficRequest {
  scenario: string
  vus: number
  durationSeconds: number
  steps: FlowStep[]
  stages?: TrafficStage[]
  iterations?: number
  dataPool?: DataPoolRequest
}

// A bulk, paginated read that can preload a DataPoolRequest's pool - see DataSourceDefinition on
// the backend for why this is a separate, smaller registry from EndpointDefinition/FlowStep.
export interface DataSourceDefinition {
  id: string
  serviceId: string
  producesVar: string
  description: string
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

// Mode picks which shape is live - "duration" uses totalDurationSeconds/points (the ramp graph),
// "iterations" uses vus/iterations (a flat VU count running a fixed shared iteration count).
export type ScenarioMode = 'duration' | 'iterations'

export interface CustomScenario {
  name: string
  steps: FlowStep[]
  mode: ScenarioMode
  totalDurationSeconds: number
  points: ScenarioPoint[]
  vus: number
  iterations: number
  dataPool?: DataPoolRequest
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

// Every request is tagged with the step (endpoint id) it came from, so status codes break down per
// endpoint instead of one pooled total - see EndpointStatusBreakdown on the backend.
export interface EndpointStatusBreakdown {
  endpointId: string
  statusCounts: StatusCount[]
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
  statusBreakdownByEndpoint: EndpointStatusBreakdown[]
  rawOutput: string
}

// targetIterations is only set for an iteration-count run - the UI shows "X/Y iterations" instead
// of "Xs/Ys" when it's present, since there's no fixed total duration in that mode.
//
// phase is "preparing" for the brief window (if any) where a requested data pool is being fetched
// from the real app before k6 even starts - preparedCount/preparedTarget carry that fetch's own
// progress then (percentComplete mirrors them so the same progress bar can be reused); every other
// field is meaningless during that phase. It's "running" for every push once k6 itself is going,
// same as before data pools existed.
export interface TrafficProgress {
  elapsedSeconds: number
  totalSeconds: number
  percentComplete: number
  activeVus: number
  iterationsSoFar: number
  iterationsPerSecond: number
  targetIterations: number | null
  phase: 'preparing' | 'running'
  preparedCount: number | null
  preparedTarget: number | null
}

export interface ResourceSample {
  serviceId: string
  cpuPercent: number
  memoryUsageBytes: number
  memoryLimitBytes: number
  tcpConnections: number
  timestamp: string
}

// Client side (app -> pgcat) vs server side (pgcat -> postgres) - the gap between them is the
// actual point of a connection pooler, so both are shown rather than just a single combined number.
export interface PoolConnectionStats {
  database: string
  clientIdle: number
  clientActive: number
  clientWaiting: number
  serverActive: number
  serverIdle: number
  serverUsed: number
}

export interface PgcatConnectionStats {
  pools: PoolConnectionStats[]
}

export interface PostgresConnectionStats {
  connectionsByDatabase: Record<string, number>
  total: number
}

// Which physical container is actually playing which role right now - Redis Sentinel and MongoDB's
// replica set can both re-elect a leader with zero involvement from this app, so architecture.json's
// static node ids/labels can't be trusted for this the way they can for everything else. role is
// "master"/"replica"/"unreachable" for Redis, "primary"/"secondary"/"unreachable" for Mongo.
export interface NodeRole {
  serviceId: string
  role: string
}

export interface InfraTopology {
  roles: NodeRole[]
}

export interface ReplicaCount {
  serviceId: string
  count: number
}

// pgcat.toml pool_mode/read-write-splitting/pool_size, applied identically to all 3 pools - see
// PgcatPoolSettings on the backend for why (collapses the file's original differentiated 10/20/10
// pool sizes into one shared value once this control is used).
export type PgcatPoolMode = 'transaction' | 'session'

export interface PgcatPoolSettings {
  poolMode: PgcatPoolMode
  readWriteSplitting: boolean
  poolSize: number
}

// Artificial WAL-replay delay on a Postgres standby (recovery_min_apply_delay) - see ReplicationLag
// on the backend.
export interface ReplicationLag {
  delayMs: number
}

// The 3 Sentinel-tunable failover parameters, applied to all three redis-sentinel-N containers at
// once - see SentinelConfig on the backend.
export interface SentinelConfig {
  downAfterMs: number
  quorum: number
  failoverTimeoutMs: number
}

// Consumer QoS for shortener-service/traffic-service's RabbitMQ consumers - read once at consumer
// startup, so setting this recreates both containers (see SetRabbitMqPrefetchAsync on the backend).
export interface RabbitMqPrefetch {
  prefetchCount: number
}

// readPreference on traffic-service's Mongo connection string - see SetMongoReadPreferenceAsync on
// the backend.
export type MongoReadPreference = 'primary' | 'secondaryPreferred'

export interface MongoReadPreferenceStatus {
  preference: MongoReadPreference
}

// Npgsql's own client-side "Maximum Pool Size", applied to every DB-touching service - the
// client-pool-size half of the same picture PgcatConnectionStats shows the server-pool-size half
// of. See SetNpgsqlPoolSizeAsync on the backend.
export interface NpgsqlPoolSize {
  poolSize: number
}

// Full detail for one past run - everything needed to answer "what configuration produced this
// result", not just the report on its own.
export interface RunSnapshot {
  id: string
  timestamp: string
  request: TrafficRequest
  infra: InfraStatus
  replicas: ReplicaCount[]
  pgcatConnections: PgcatConnectionStats | null
  postgresConnections: PostgresConnectionStats | null
  report: TrafficReport
}

// Lightweight row for the history list - see RunSummary on the backend for why it's separate from
// RunSnapshot (avoids pulling every run's full raw k6 output just to render a list).
export interface RunSummary {
  id: string
  timestamp: string
  scenario: string
  httpRequests: number
  failedRequests: number
  exitCode: number
}
