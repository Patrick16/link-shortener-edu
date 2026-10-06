import { controlApi } from '../api/controlApi'
import type { InfraConfigSnapshot, ManagedContainer, ReplicaCount, ReplicationLagEntry } from '../types/controlApi'

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
  const [infra, containers, pgcatPool, sentinel, prefetch, mongoReadPreference, npgsqlPoolSize, replicationLags] = await Promise.all([
    controlApi.getInfraStatus(),
    controlApi.listContainers(),
    controlApi.getPgcatPoolSettings().catch(() => null),
    controlApi.getSentinelConfig().catch(() => null),
    controlApi.getRabbitMqPrefetch().catch(() => null),
    controlApi.getMongoReadPreference().catch(() => null),
    controlApi.getNpgsqlPoolSize().catch(() => null),
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
    } catch {
      failed.push(label)
    }
  }

  await step('load balancing', () => controlApi.setNginxEnabled(!config.infra.nginxBypassed))
  await step('connection pooling', () => controlApi.setPgcatEnabled(config.infra.pgcatEnabled))
  await step('caching', () => controlApi.setCacheEnabled(config.infra.cacheEnabled))
  await step('messaging transport', () => controlApi.setMessagingMode(config.infra.messagingMode))

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

  const lagServiceIds = new Set([...(current.replicationLags ?? []), ...(desired.replicationLags ?? [])].map((l) => l.serviceId))
  for (const serviceId of lagServiceIds) {
    const a = current.replicationLags?.find((l) => l.serviceId === serviceId)?.delayMs ?? 0
    const b = desired.replicationLags?.find((l) => l.serviceId === serviceId)?.delayMs ?? 0
    addIfDiff(`${serviceId} replication lag`, `${a}ms`, `${b}ms`)
  }

  return rows
}
