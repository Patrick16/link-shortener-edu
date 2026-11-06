import { controlApi } from '../api/controlApi'
import type { InfraConfigSnapshot, InfraStatus, ManagedContainer, MongoReadPreference, ReplicaCount, ReplicationLagEntry } from '../types/controlApi'
import type { ConfigPatch } from '../types/lessons'

export interface ApplySystemConfigResult {
  applied: string[]
  failed: string[]
}

export interface InfraConfigDiffRow {
  label: string
  current: string
  desired: string
}

// Same hardcoded pair TrafficRunCoordinator.ReadReplicationLagsAsync uses server-side - there are
// only ever these two replicas in this stack, and GetReplicationLagAsync 404s for anything else.
const REPLICATION_LAG_SERVICE_IDS = ['postgres-replica1', 'postgres-replica2']

function toReplicaCounts(containers: ManagedContainer[]): ReplicaCount[] {
  const counts = new Map<string, number>()
  for (const c of containers) {
    if (c.state !== 'running') continue
    counts.set(c.serviceId, (counts.get(c.serviceId) ?? 0) + 1)
  }
  return [...counts.entries()].map(([serviceId, count]) => ({ serviceId, count }))
}

// Reads every control-api-exposed capability's current value - the same bundle RunSnapshot
// already carries minus the run-result fields (see InfraConfigSnapshot). Used both to "save
// current config as a new preset" and to diff a saved preset against what's actually running
// right now. Each optional capability's GET is best-effort (same spirit as
// TrafficRunCoordinator.SaveSnapshotAsync's own try/catch reads server-side) - one being
// unreachable (e.g. Sentinel not running) shouldn't block capturing the rest.
export async function captureCurrentInfraConfig(): Promise<InfraConfigSnapshot> {
  const [infra, containers, pgcatPool, sentinel, prefetch, mongoReadPreference, npgsqlPoolSize, l1Cache, negativeCache, replicationLags] = await Promise.all([
    controlApi.getInfraStatus(),
    controlApi.listContainers(),
    controlApi.getPgcatPoolSettings().catch(() => null),
    controlApi.getSentinelConfig().catch(() => null),
    controlApi.getRabbitMqPrefetch().catch(() => null),
    controlApi.getMongoReadPreference().catch(() => null),
    controlApi.getNpgsqlPoolSize().catch(() => null),
    controlApi.getL1CacheEnabled().catch(() => null),
    controlApi.getNegativeCacheEnabled().catch(() => null),
    Promise.all(
      REPLICATION_LAG_SERVICE_IDS.map(async (serviceId): Promise<ReplicationLagEntry | null> => {
        try {
          const lag = await controlApi.getReplicationLag(serviceId)
          return { serviceId, delayMs: lag.delayMs }
        } catch {
          return null
        }
      }),
    ),
  ])

  return {
    infra,
    replicas: toReplicaCounts(containers),
    pgcatPool,
    sentinel,
    replicationLags: replicationLags.filter((l): l is ReplicationLagEntry => l !== null),
    rabbitMqPrefetchCount: prefetch?.prefetchCount ?? null,
    mongoReadPreference: mongoReadPreference?.preference ?? null,
    npgsqlPoolSize: npgsqlPoolSize?.poolSize ?? null,
    l1CacheEnabled: l1Cache?.enabled ?? null,
    negativeCacheEnabled: negativeCache?.enabled ?? null,
  }
}

// Re-applies every experimental infra control captured in a config snapshot, one at a time (not
// Promise.all) - several of these recreate the same underlying containers (pgcat/cache toggles,
// RabbitMQ prefetch, Mongo read preference, Npgsql pool size all touch overlapping DB-touching
// services), so firing them concurrently risks one `docker compose up -d --force-recreate`
// stepping on another's. Best-effort per setting - one control failing to apply shouldn't stop
// the rest from going through. Shared by "reuse a past run's config" (reuseRunConfig.ts) and
// "apply a saved preset" (PresetsModal.tsx).
export async function applyInfraConfig(config: InfraConfigSnapshot): Promise<ApplySystemConfigResult> {
  const applied: string[] = []
  const failed: string[] = []

  async function step(label: string, run: () => Promise<unknown>) {
    try {
      await run()
      applied.push(label)
    } catch (err) {
      // Logged here, not just pushed into `failed` - `failed` only ever carries the label (surfaced
      // in the UI as "X failed"), so without this the actual reason (a ControlApiError's status/body,
      // a network failure, ...) was discarded entirely. TopologyPanel's own catch tells the user to
      // "see the console for details" - this is what makes that claim true.
      console.error(`applyInfraConfig: "${label}" failed`, err)
      failed.push(label)
    }
  }

  await step('load balancing', () => controlApi.setNginxEnabled(!config.infra.nginxBypassed))
  await step('connection pooling', () => controlApi.setPgcatEnabled(config.infra.pgcatEnabled))
  // Redis/Sentinel before caching - caching has nothing to cache against until redis-master (and,
  // if desired, Sentinel) is already in its target state, same reasoning as haproxy running after
  // connection pooling above.
  await step('redis', () => controlApi.setRedisEnabled(config.infra.redisEnabled))
  await step('redis sentinel', () => controlApi.setRedisSentinelEnabled(config.infra.redisSentinelEnabled))
  await step('caching', () => controlApi.setCacheEnabled(config.infra.cacheEnabled))
  await step('messaging transport', () => controlApi.setMessagingMode(config.infra.messagingMode))
  await step('haproxy', () => controlApi.setHaproxyEnabled(config.infra.haproxyEnabled))
  await step('postgres replicas', () => controlApi.setPostgresReplicasEnabled(config.infra.postgresReplicasEnabled))
  await step('mongo replicas', () => controlApi.setMongoReplicasEnabled(config.infra.mongoReplicasEnabled))

  // Replicas lists every service that was running at capture time, most of which (redis, mongo,
  // rabbitmq, monitoring tools...) were never scalable in the first place - only replay counts
  // for the explicit scalable allowlist instead of 400ing against the rest on every apply.
  const scalableIds = new Set(await controlApi.listScalableServices().catch(() => []))
  for (const replica of config.replicas.filter((r) => scalableIds.has(r.serviceId))) {
    await step(`${replica.serviceId} replicas`, async () => {
      const result = await controlApi.scale(replica.serviceId, replica.count)
      if (!result.success) throw new Error(result.output)
    })
  }

  if (config.pgcatPool) {
    await step('pgcat pool settings', () => controlApi.setPgcatPoolSettings(config.pgcatPool!))
  }
  if (config.sentinel) {
    await step('Sentinel config', () => controlApi.setSentinelConfig(config.sentinel!))
  }
  for (const lag of config.replicationLags ?? []) {
    await step(`${lag.serviceId} replication lag`, () => controlApi.setReplicationLag(lag.serviceId, lag.delayMs))
  }
  if (config.rabbitMqPrefetchCount != null) {
    await step('RabbitMQ prefetch', () => controlApi.setRabbitMqPrefetch(config.rabbitMqPrefetchCount!))
  }
  if (config.mongoReadPreference) {
    await step('Mongo read preference', () => controlApi.setMongoReadPreference(config.mongoReadPreference!))
  }
  if (config.npgsqlPoolSize != null) {
    await step('Npgsql pool size', () => controlApi.setNpgsqlPoolSize(config.npgsqlPoolSize!))
  }
  if (config.l1CacheEnabled != null) {
    await step('L1 caching', () => controlApi.setL1CacheEnabled(config.l1CacheEnabled!))
  }
  if (config.negativeCacheEnabled != null) {
    await step('Negative caching', () => controlApi.setNegativeCacheEnabled(config.negativeCacheEnabled!))
  }

  return { applied, failed }
}

function formatPgcatPool(pool: InfraConfigSnapshot['pgcatPool']): string {
  return pool ? `${pool.poolMode}, rw-split ${pool.readWriteSplitting ? 'on' : 'off'}, size ${pool.poolSize}` : '—'
}

function formatSentinel(sentinel: InfraConfigSnapshot['sentinel']): string {
  return sentinel ? `down-after ${sentinel.downAfterMs}ms, quorum ${sentinel.quorum}, failover ${sentinel.failoverTimeoutMs}ms` : '—'
}

function replicaMap(replicas: ReplicaCount[]): Map<string, number> {
  return new Map(replicas.map((r) => [r.serviceId, r.count]))
}

// Only the fields that actually differ, so applying a preset that already matches the live stack
// shows "nothing to change" instead of an apply button that would still fire `--force-recreate`
// calls for no reason. `scalableIds` scopes the replica comparison to services that can actually
// be scaled - everything else keeps whatever count it happens to be running at capture time.
export function diffInfraConfig(current: InfraConfigSnapshot, desired: InfraConfigSnapshot, scalableIds: string[]): InfraConfigDiffRow[] {
  const rows: InfraConfigDiffRow[] = []

  function addIfDiff(label: string, a: string, b: string) {
    if (a !== b) rows.push({ label, current: a, desired: b })
  }

  addIfDiff('Load balancing', current.infra.nginxBypassed ? 'OFF (bypassed)' : 'ON', desired.infra.nginxBypassed ? 'OFF (bypassed)' : 'ON')
  addIfDiff('Connection pooling', current.infra.pgcatEnabled ? 'ON' : 'OFF', desired.infra.pgcatEnabled ? 'ON' : 'OFF')
  addIfDiff('Caching', current.infra.cacheEnabled ? 'ON' : 'OFF', desired.infra.cacheEnabled ? 'ON' : 'OFF')
  addIfDiff('Messaging transport', current.infra.messagingMode, desired.infra.messagingMode)
  addIfDiff('HAProxy', current.infra.haproxyEnabled ? 'ON' : 'OFF', desired.infra.haproxyEnabled ? 'ON' : 'OFF')
  addIfDiff('Postgres replicas', current.infra.postgresReplicasEnabled ? 'ON' : 'OFF', desired.infra.postgresReplicasEnabled ? 'ON' : 'OFF')
  addIfDiff('Redis', current.infra.redisEnabled ? 'ON' : 'OFF', desired.infra.redisEnabled ? 'ON' : 'OFF')
  addIfDiff('Redis Sentinel', current.infra.redisSentinelEnabled ? 'ON' : 'OFF', desired.infra.redisSentinelEnabled ? 'ON' : 'OFF')
  addIfDiff('Mongo replicas', current.infra.mongoReplicasEnabled ? 'ON' : 'OFF', desired.infra.mongoReplicasEnabled ? 'ON' : 'OFF')

  const currentReplicas = replicaMap(current.replicas)
  const desiredReplicas = replicaMap(desired.replicas)
  for (const serviceId of scalableIds) {
    const a = currentReplicas.get(serviceId) ?? 1
    const b = desiredReplicas.get(serviceId) ?? 1
    addIfDiff(`${serviceId} replicas`, `×${a}`, `×${b}`)
  }

  addIfDiff('Pgcat pool', formatPgcatPool(current.pgcatPool), formatPgcatPool(desired.pgcatPool))
  addIfDiff('Sentinel', formatSentinel(current.sentinel), formatSentinel(desired.sentinel))
  addIfDiff('RabbitMQ prefetch', String(current.rabbitMqPrefetchCount ?? '—'), String(desired.rabbitMqPrefetchCount ?? '—'))
  addIfDiff('Mongo read preference', current.mongoReadPreference ?? '—', desired.mongoReadPreference ?? '—')
  addIfDiff('Npgsql pool size', String(current.npgsqlPoolSize ?? '—'), String(desired.npgsqlPoolSize ?? '—'))
  addIfDiff(
    'L1 caching',
    current.l1CacheEnabled == null ? '—' : current.l1CacheEnabled ? 'ON' : 'OFF',
    desired.l1CacheEnabled == null ? '—' : desired.l1CacheEnabled ? 'ON' : 'OFF',
  )
  addIfDiff(
    'Negative caching',
    current.negativeCacheEnabled == null ? '—' : current.negativeCacheEnabled ? 'ON' : 'OFF',
    desired.negativeCacheEnabled == null ? '—' : desired.negativeCacheEnabled ? 'ON' : 'OFF',
  )

  const lagServiceIds = new Set([...(current.replicationLags ?? []), ...(desired.replicationLags ?? [])].map((l) => l.serviceId))
  for (const serviceId of lagServiceIds) {
    const a = current.replicationLags?.find((l) => l.serviceId === serviceId)?.delayMs ?? 0
    const b = desired.replicationLags?.find((l) => l.serviceId === serviceId)?.delayMs ?? 0
    addIfDiff(`${serviceId} replication lag`, `${a}ms`, `${b}ms`)
  }

  return rows
}

// --- Lesson Mode ---------------------------------------------------------------------------
//
// Lesson Mode reuses captureCurrentInfraConfig's InfraConfigSnapshot for everything it already
// covers, plus three standalone dials (circuit breakers, geoIP bulkhead, Mongo hedge delay) that
// InfraConfigSnapshot never captured - Presets/diffInfraConfig above never needed them (see that
// record's own backend comment), but lessons 4.1/4.2/5.1 do. Kept as a separate capture rather
// than widening InfraConfigSnapshot itself, which stays an exact mirror of the backend record on
// purpose (see this file's own header comment).
export interface LessonLiveConfig {
  snapshot: InfraConfigSnapshot
  replicaCounts: Map<string, number>
  circuitBreakersEnabled: boolean | null
  geoIpBulkheadLimit: number | null
  mongoHedgeDelayMs: number | null
}

export async function captureLessonLiveConfig(): Promise<LessonLiveConfig> {
  const [snapshot, containers, circuitBreakers, geoIpBulkhead, mongoHedgeDelay] = await Promise.all([
    captureCurrentInfraConfig(),
    controlApi.listContainers(),
    controlApi.getCircuitBreakersEnabled().catch(() => null),
    controlApi.getGeoIpBulkheadLimit().catch(() => null),
    controlApi.getMongoHedgeDelay().catch(() => null),
  ])

  return {
    snapshot,
    replicaCounts: new Map(toReplicaCounts(containers).map((r) => [r.serviceId, r.count])),
    circuitBreakersEnabled: circuitBreakers?.enabled ?? null,
    geoIpBulkheadLimit: geoIpBulkhead?.limit ?? null,
    mongoHedgeDelayMs: mongoHedgeDelay?.delayMs ?? null,
  }
}

// Does the live stand satisfy a lesson's partial ConfigPatch? Only the keys the patch actually
// declares are compared - never every field of `live`, or a toggle this lesson doesn't care about
// would spuriously block its Run/Next buttons (the opposite problem diffInfraConfig above solves
// for a full-snapshot Preset diff, where every field legitimately matters).
export function matchesPatch(live: LessonLiveConfig, patch: ConfigPatch): boolean {
  const { snapshot, replicaCounts } = live

  if (patch.infra) {
    for (const key of Object.keys(patch.infra) as (keyof InfraStatus)[]) {
      if (snapshot.infra[key] !== patch.infra[key]) return false
    }
  }
  if (patch.replicas) {
    for (const [serviceId, count] of Object.entries(patch.replicas)) {
      if ((replicaCounts.get(serviceId) ?? 1) !== count) return false
    }
  }
  if (patch.rabbitMqPrefetchCount != null && snapshot.rabbitMqPrefetchCount !== patch.rabbitMqPrefetchCount) return false
  if (patch.mongoReadPreference != null && snapshot.mongoReadPreference !== patch.mongoReadPreference) return false
  if (patch.npgsqlPoolSize != null && snapshot.npgsqlPoolSize !== patch.npgsqlPoolSize) return false
  if (patch.l1CacheEnabled != null && snapshot.l1CacheEnabled !== patch.l1CacheEnabled) return false
  if (patch.negativeCacheEnabled != null && snapshot.negativeCacheEnabled !== patch.negativeCacheEnabled) return false
  if (patch.circuitBreakersEnabled != null && live.circuitBreakersEnabled !== patch.circuitBreakersEnabled) return false
  if (patch.geoIpBulkheadLimit != null && live.geoIpBulkheadLimit !== patch.geoIpBulkheadLimit) return false
  if (patch.mongoHedgeDelayMs != null && live.mongoHedgeDelayMs !== patch.mongoHedgeDelayMs) return false

  return true
}

// Applies only the keys a lesson's patch actually declares - deliberately NOT applyInfraConfig
// above, which always re-applies all 9 InfraStatus toggles unconditionally (right for "restore a
// full Preset snapshot," wasteful and container-churny for "flip the one or two things this
// lesson teaches," since each is its own `docker compose --force-recreate`). Ordering mirrors
// applyInfraConfig's own for every field that overlaps (Redis/Sentinel before caching, haproxy
// after pooling).
export async function applyConfigPatch(patch: ConfigPatch): Promise<ApplySystemConfigResult> {
  const applied: string[] = []
  const failed: string[] = []

  async function step(label: string, run: () => Promise<unknown>) {
    try {
      await run()
      applied.push(label)
    } catch (err) {
      console.error(`applyConfigPatch: "${label}" failed`, err)
      failed.push(label)
    }
  }

  const infra = patch.infra ?? {}
  if (infra.nginxBypassed != null) await step('load balancing', () => controlApi.setNginxEnabled(!infra.nginxBypassed))
  if (infra.pgcatEnabled != null) await step('connection pooling', () => controlApi.setPgcatEnabled(infra.pgcatEnabled!))
  if (infra.redisEnabled != null) await step('redis', () => controlApi.setRedisEnabled(infra.redisEnabled!))
  if (infra.redisSentinelEnabled != null) await step('redis sentinel', () => controlApi.setRedisSentinelEnabled(infra.redisSentinelEnabled!))
  if (infra.cacheEnabled != null) await step('caching', () => controlApi.setCacheEnabled(infra.cacheEnabled!))
  if (infra.messagingMode != null) await step('messaging transport', () => controlApi.setMessagingMode(infra.messagingMode!))
  if (infra.haproxyEnabled != null) await step('haproxy', () => controlApi.setHaproxyEnabled(infra.haproxyEnabled!))
  if (infra.postgresReplicasEnabled != null) await step('postgres replicas', () => controlApi.setPostgresReplicasEnabled(infra.postgresReplicasEnabled!))
  if (infra.mongoReplicasEnabled != null) await step('mongo replicas', () => controlApi.setMongoReplicasEnabled(infra.mongoReplicasEnabled!))

  if (patch.replicas) {
    for (const [serviceId, count] of Object.entries(patch.replicas)) {
      await step(`${serviceId} replicas`, async () => {
        const result = await controlApi.scale(serviceId, count)
        if (!result.success) throw new Error(result.output)
      })
    }
  }

  if (patch.rabbitMqPrefetchCount != null) await step('RabbitMQ prefetch', () => controlApi.setRabbitMqPrefetch(patch.rabbitMqPrefetchCount!))
  if (patch.mongoReadPreference != null) await step('Mongo read preference', () => controlApi.setMongoReadPreference(patch.mongoReadPreference as MongoReadPreference))
  if (patch.npgsqlPoolSize != null) await step('Npgsql pool size', () => controlApi.setNpgsqlPoolSize(patch.npgsqlPoolSize!))
  if (patch.l1CacheEnabled != null) await step('L1 caching', () => controlApi.setL1CacheEnabled(patch.l1CacheEnabled!))
  if (patch.negativeCacheEnabled != null) await step('Negative caching', () => controlApi.setNegativeCacheEnabled(patch.negativeCacheEnabled!))
  if (patch.circuitBreakersEnabled != null) await step('Circuit breakers', () => controlApi.setCircuitBreakersEnabled(patch.circuitBreakersEnabled!))
  if (patch.geoIpBulkheadLimit != null) await step('GeoIP bulkhead', () => controlApi.setGeoIpBulkheadLimit(patch.geoIpBulkheadLimit!))
  if (patch.mongoHedgeDelayMs != null) await step('Mongo hedge delay', () => controlApi.setMongoHedgeDelay(patch.mongoHedgeDelayMs!))

  return { applied, failed }
}
