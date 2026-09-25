import { controlApi } from '../api/controlApi'
import type { CustomScenario, RunSnapshot, ScenarioPoint, TrafficRequest } from '../types/controlApi'

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

export interface ApplySystemConfigResult {
  applied: string[]
  failed: string[]
}

// Re-applies every experimental infra control captured in a past run's snapshot, one at a time
// (not Promise.all) - several of these recreate the same underlying containers (pgcat/cache
// toggles, RabbitMQ prefetch, Mongo read preference, Npgsql pool size all touch overlapping
// DB-touching services), so firing them concurrently risks one `docker compose up -d --force-
// recreate` stepping on another's. Best-effort per setting, same reasoning as the backend's own
// snapshot capture: one control failing to apply shouldn't stop the rest from going through.
export async function applySystemConfig(snapshot: RunSnapshot): Promise<ApplySystemConfigResult> {
  const applied: string[] = []
  const failed: string[] = []

  async function step(label: string, run: () => Promise<unknown>) {
    try {
      await run()
      applied.push(label)
    } catch {
      failed.push(label)
    }
  }

  await step('load balancing', () => controlApi.setNginxEnabled(!snapshot.infra.nginxBypassed))
  await step('connection pooling', () => controlApi.setPgcatEnabled(snapshot.infra.pgcatEnabled))
  await step('caching', () => controlApi.setCacheEnabled(snapshot.infra.cacheEnabled))

  // Replicas snapshots every running service at run time, most of which (redis, mongo, rabbitmq,
  // monitoring tools...) were never scalable in the first place - only replay counts for the
  // explicit scalable allowlist instead of 400ing against the rest on every reuse.
  const scalableIds = new Set(await controlApi.listScalableServices().catch(() => []))
  for (const replica of snapshot.replicas.filter((r) => scalableIds.has(r.serviceId))) {
    await step(`${replica.serviceId} replicas`, async () => {
      const result = await controlApi.scale(replica.serviceId, replica.count)
      if (!result.success) throw new Error(result.output)
    })
  }

  if (snapshot.pgcatPool) {
    await step('pgcat pool settings', () => controlApi.setPgcatPoolSettings(snapshot.pgcatPool!))
  }
  if (snapshot.sentinel) {
    await step('Sentinel config', () => controlApi.setSentinelConfig(snapshot.sentinel!))
  }
  for (const lag of snapshot.replicationLags ?? []) {
    await step(`${lag.serviceId} replication lag`, () => controlApi.setReplicationLag(lag.serviceId, lag.delayMs))
  }
  if (snapshot.rabbitMqPrefetchCount != null) {
    await step('RabbitMQ prefetch', () => controlApi.setRabbitMqPrefetch(snapshot.rabbitMqPrefetchCount!))
  }
  if (snapshot.mongoReadPreference) {
    await step('Mongo read preference', () => controlApi.setMongoReadPreference(snapshot.mongoReadPreference!))
  }
  if (snapshot.npgsqlPoolSize != null) {
    await step('Npgsql pool size', () => controlApi.setNpgsqlPoolSize(snapshot.npgsqlPoolSize!))
  }

  return { applied, failed }
}
