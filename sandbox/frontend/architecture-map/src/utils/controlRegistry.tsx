import type { ComponentType } from 'react'
import type { ArchComponent } from '../types/architecture'
import type { ManagedContainer } from '../types/controlApi'
import { ScaleCapabilityControl } from '../components/ScaleCapabilityControl'
import { FlushCacheControl } from '../components/FlushCacheControl'
import { PgcatPoolControl } from '../components/PgcatPoolControl'
import { NpgsqlPoolSizeControl } from '../components/NpgsqlPoolSizeControl'
import { SentinelConfigControl } from '../components/SentinelConfigControl'
import { RabbitMqPrefetchControl } from '../components/RabbitMqPrefetchControl'
import { MongoReadPreferenceControl } from '../components/MongoReadPreferenceControl'
import { ReplicationLagControl } from '../components/ReplicationLagControl'
import { PgcatConnectionsPanel } from '../components/PgcatConnectionsPanel'
import { PostgresConnectionsPanel } from '../components/PostgresConnectionsPanel'
import { DlqStatsPanel } from '../components/DlqStatsPanel'
import type { CapabilityControlProps } from './capabilityControlProps'

export type { CapabilityControlProps }

// One entry per capability string from architecture.json - NodePanel no longer decides which
// control to render per serviceId, it just looks each of a node's capabilities up here.
// nginx-toggle/pgcat-toggle/cache-toggle/messaging-mode used to live here (one per node) - all 4
// moved into the global TopologyPanel (see App.tsx), which stages and saves all of them at once
// instead of applying immediately from a single node's panel.
const CONTROL_COMPONENTS: Record<string, ComponentType<CapabilityControlProps>> = {
  scalable: ScaleCapabilityControl,
  'flush-cache': FlushCacheControl,
  'pgcat-pool': PgcatPoolControl,
  'npgsql-pool-size': NpgsqlPoolSizeControl,
  'sentinel-config': SentinelConfigControl,
  'rabbitmq-prefetch': RabbitMqPrefetchControl,
  'mongo-read-preference': MongoReadPreferenceControl,
  'replication-lag': ReplicationLagControl,
  'pgcat-connections': PgcatConnectionsPanel,
  'postgres-connections': PostgresConnectionsPanel,
  'dlq-stats': DlqStatsPanel,
}

export function renderCapabilityControls(component: ArchComponent, serviceId: string, instances: ManagedContainer[]) {
  return component.capabilities?.map((cap) => {
    const Control = CONTROL_COMPONENTS[cap]
    if (!Control) return null
    return <Control key={cap} component={component} serviceId={serviceId} instances={instances} />
  })
}
