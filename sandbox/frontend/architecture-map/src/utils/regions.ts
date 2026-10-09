// Static grouping of every component into the architectural "stage" it belongs to, purely for the
// visual swimlane-style region boxes the graph draws behind its nodes (see layoutGraph.ts /
// RegionNode.tsx) - this is domain knowledge (which box a node conceptually belongs to), not
// something derivable from architecture.json's edges alone, so it's hand-maintained here rather
// than inferred. Order matters: it's also the left-to-right reading order the labels imply, and
// roughly (not strictly - dagre still routes by actual edges) matches the real request flow.
export interface RegionDef {
  id: string
  label: string
}

export const REGIONS: RegionDef[] = [
  { id: 'region-clients', label: 'Clients' },
  { id: 'region-lb', label: 'Load balancer' },
  { id: 'region-api', label: 'API' },
  { id: 'region-cache', label: 'Cache' },
  { id: 'region-bus', label: 'Message bus' },
  { id: 'region-workers', label: 'Workers' },
  { id: 'region-storage', label: 'Storage' },
]

const COMPONENT_REGION: Record<string, string> = {
  'k6': 'region-clients',
  'frontend-app': 'region-clients',
  'nginx': 'region-lb',
  'auth-api': 'region-api',
  'link-api': 'region-api',
  'redirect-api': 'region-api',
  'reporting-api': 'region-api',
  'redis-master': 'region-cache',
  'redis-replica1': 'region-cache',
  'redis-replica2': 'region-cache',
  'redis-sentinel-1': 'region-cache',
  'redis-sentinel-2': 'region-cache',
  'redis-sentinel-3': 'region-cache',
  'rabbitmq': 'region-bus',
  'shortener-service': 'region-workers',
  'traffic-service': 'region-workers',
  'reporting-service': 'region-workers',
  // haproxy is a load balancer, but region-lb above is nginx's client-facing one - haproxy sits
  // entirely between the DB-touching services and Postgres (same position the old single pgcat
  // node held), so it belongs with storage, not with nginx.
  'haproxy': 'region-storage',
  'pgcat': 'region-storage',
  'users-db': 'region-storage',
  'links-db': 'region-storage',
  'clicks-db': 'region-storage',
  'postgres-replica1': 'region-storage',
  'postgres-replica2': 'region-storage',
  'mongo1': 'region-storage',
  'mongo2': 'region-storage',
  'mongo3': 'region-storage',
  'clickhouse': 'region-storage',
}

export function getRegionId(componentId: string): string | undefined {
  return COMPONENT_REGION[componentId]
}
