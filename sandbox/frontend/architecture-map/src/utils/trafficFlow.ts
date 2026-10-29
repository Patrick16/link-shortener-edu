import type { InfraStatus } from '../types/controlApi'

// The connections k6-scripts/flow.js can actually exercise, depending on which endpoint steps a
// run's sequence includes: a create-link step hits the create path, a redirect step hits the
// redirect path (which also always publishes a click event, regardless of the k6 client's own
// redirects:0 setting - that's a client-side option, RedirectApi processes the request fully
// either way).
//
// Which concrete edges that maps to depends on the live topology (see the Topology panel /
// architecture.json's activeWhen): nginx can be bypassed for k6's own traffic, messaging can be
// rabbitmq or sync-gRPC, and the DB hop can go through haproxy, straight to pgcat (haproxy
// bypassed), or straight to the database itself (pgcat bypassed too). Each of those swaps the
// edges that actually exist in the graph (see topologyFilter.ts), so the flow-highlighting list
// has to track the same toggles - a static list only ever matching the default topology meant
// every other topology lit up nothing (or the wrong, now-invisible edges) while traffic ran.
export type TrafficFlowStatus = Pick<InfraStatus, 'nginxBypassed' | 'messagingMode' | 'pgcatEnabled' | 'haproxyEnabled'>

// 'redis-master'/'mongo1' below stand for "whichever node is currently that cluster's leader", not
// literally always that one container - Sentinel/replica-set failover can promote a different node
// without this app doing anything (see useInfraTopology), and if it does, traffic really is flowing
// to the new leader, not the demoted (or unreachable) one the static id names. Highlighting the old
// id in that case would show the flow passing through a node that's actually down, which is exactly
// the wrong moment to get this wrong.
const REDIS_NODES = ['redis-master', 'redis-replica1', 'redis-replica2']
const MONGO_NODES = ['mongo1', 'mongo2', 'mongo3']

function resolveLeader(nodes: readonly string[], fallback: string, roles: Record<string, string>): string {
  return nodes.find((id) => roles[id] === 'master' || roles[id] === 'primary') ?? fallback
}

// The DB-facing hop for one writer/reader: matches exactly the three mutually-exclusive edge
// variants architecture.json declares for it (normal / haproxy-bypassed / pgcat-bypassed - see
// e.g. the shortener-service -> haproxy|pgcat|links-db edges and their activeWhen blocks).
function dbHop(pgcatEnabled: boolean, haproxyEnabled: boolean, from: string, table: string): readonly [string, string] {
  if (!pgcatEnabled) return [from, table]
  if (!haproxyEnabled) return [from, 'pgcat']
  return [from, 'haproxy']
}

export function buildTrafficFlowEdges(status: TrafficFlowStatus | null, roles: Record<string, string> = {}): ReadonlyArray<readonly [string, string]> {
  const nginxBypassed = status?.nginxBypassed ?? false
  const messagingMode = status?.messagingMode ?? 'rabbitmq'
  const pgcatEnabled = status?.pgcatEnabled ?? true
  const haproxyEnabled = status?.haproxyEnabled ?? true
  const redisLeader = resolveLeader(REDIS_NODES, 'redis-master', roles)
  const mongoLeader = resolveLeader(MONGO_NODES, 'mongo1', roles)

  const edges: Array<readonly [string, string]> = []

  if (nginxBypassed) {
    edges.push(['k6', 'link-api'], ['k6', 'redirect-api'])
  } else {
    edges.push(['k6', 'nginx'], ['nginx', 'link-api'], ['nginx', 'redirect-api'])
  }

  if (messagingMode === 'grpc') {
    edges.push(['link-api', 'nginx'], ['nginx', 'shortener-service'], ['redirect-api', 'nginx'], ['nginx', 'traffic-service'])
  } else {
    edges.push(['link-api', 'rabbitmq'], ['rabbitmq', 'shortener-service'], ['redirect-api', 'rabbitmq'], ['rabbitmq', 'traffic-service'])
  }

  edges.push(dbHop(pgcatEnabled, haproxyEnabled, 'shortener-service', 'links-db'))
  edges.push(dbHop(pgcatEnabled, haproxyEnabled, 'redirect-api', 'links-db'))
  edges.push(dbHop(pgcatEnabled, haproxyEnabled, 'traffic-service', 'clicks-db'))

  if (pgcatEnabled && haproxyEnabled) edges.push(['haproxy', 'pgcat'])
  if (pgcatEnabled) edges.push(['pgcat', 'links-db'], ['pgcat', 'clicks-db'])

  edges.push(['redirect-api', redisLeader])
  edges.push(['traffic-service', mongoLeader])

  return edges
}

// The default topology's edge list - kept as a named export since it's a useful fixture (tests,
// anything wanting "the normal path" without a live InfraStatus to hand).
export const TRAFFIC_FLOW_EDGES: ReadonlyArray<readonly [string, string]> = buildTrafficFlowEdges(null)

export function isTrafficFlowEdge(from: string, to: string, roles: Record<string, string> = {}, status: TrafficFlowStatus | null = null): boolean {
  return buildTrafficFlowEdges(status, roles).some(([f, t]) => f === from && t === to)
}
