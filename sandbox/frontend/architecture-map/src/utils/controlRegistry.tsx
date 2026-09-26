import type { ComponentType } from 'react'
import type { ArchComponent } from '../types/architecture'
import type { ManagedContainer } from '../types/controlApi'
import { ScaleCapabilityControl } from '../components/ScaleCapabilityControl'
import { NginxToggleControl } from '../components/NginxToggleControl'
import { PgcatToggleControl } from '../components/PgcatToggleControl'
import { CacheToggleControl } from '../components/CacheToggleControl'
import { FlushCacheControl } from '../components/FlushCacheControl'
import { PgcatPoolControl } from '../components/PgcatPoolControl'
import { NpgsqlPoolSizeControl } from '../components/NpgsqlPoolSizeControl'
import { SentinelConfigControl } from '../components/SentinelConfigControl'
import { RabbitMqPrefetchControl } from '../components/RabbitMqPrefetchControl'
import { MongoReadPreferenceControl } from '../components/MongoReadPreferenceControl'
import { ReplicationLagControl } from '../components/ReplicationLagControl'
import { PgcatConnectionsPanel } from '../components/PgcatConnectionsPanel'
import { PostgresConnectionsPanel } from '../components/PostgresConnectionsPanel'
import type { CapabilityControlProps } from './capabilityControlProps'

export type { CapabilityControlProps }

// One entry per capability string from architecture.json - NodePanel no longer decides which
// control to render per serviceId, it just looks each of a node's capabilities up here.
const CONTROL_COMPONENTS: Record<string, ComponentType<CapabilityControlProps>> = {
  scalable: ScaleCapabilityControl,
  'nginx-toggle': NginxToggleControl,
  'pgcat-toggle': PgcatToggleControl,
  'cache-toggle': CacheToggleControl,
  'flush-cache': FlushCacheControl,
  'pgcat-pool': PgcatPoolControl,
  'npgsql-pool-size': NpgsqlPoolSizeControl,
  'sentinel-config': SentinelConfigControl,
  'rabbitmq-prefetch': RabbitMqPrefetchControl,
  'mongo-read-preference': MongoReadPreferenceControl,
  'replication-lag': ReplicationLagControl,
  'pgcat-connections': PgcatConnectionsPanel,
  'postgres-connections': PostgresConnectionsPanel,
}

export function renderCapabilityControls(component: ArchComponent, serviceId: string, instances: ManagedContainer[]) {
  return component.capabilities?.map((cap) => {
    const Control = CONTROL_COMPONENTS[cap]
    if (!Control) return null
    return <Control key={cap} component={component} serviceId={serviceId} instances={instances} />
  })
}
