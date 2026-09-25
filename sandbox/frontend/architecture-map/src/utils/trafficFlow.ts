// The connections k6-scripts/*.js actually exercise. smoke.js/spike.js hit both the create path
// and the redirect path (which also always publishes a click event, regardless of the k6 client's
// own redirects:0 setting - that's a client-side option, RedirectApi processes the request fully
// either way); read-heavy.js only exercises the redirect side. Traffic now goes through nginx
// (see sandbox/infra/nginx/nginx.conf) on the way in and pgcat (see sandbox/infra/pgcat/pgcat.toml)
// on the way to Postgres, not straight to link-api/redirect-api or postgres, so both hops are part
// of the real path too. Used to highlight/animate the edges real traffic is flowing through while
// a run is active, rather than animating the whole graph indiscriminately.
export const TRAFFIC_FLOW_EDGES: ReadonlyArray<readonly [string, string]> = [
  ['k6', 'nginx'],
  ['nginx', 'link-api'],
  ['nginx', 'redirect-api'],
  ['link-api', 'rabbitmq'],
  ['rabbitmq', 'shortener-service'],
  ['shortener-service', 'pgcat'],
  ['pgcat', 'links-db'],
  ['redirect-api', 'redis-master'],
  ['redirect-api', 'pgcat'],
  ['redirect-api', 'rabbitmq'],
  ['rabbitmq', 'traffic-service'],
  ['traffic-service', 'pgcat'],
  ['traffic-service', 'mongo1'],
  ['pgcat', 'clicks-db'],
]

// 'redis-master'/'mongo1' in the list above stand for "whichever node is currently that cluster's
// leader", not literally always that one container - Sentinel/replica-set failover can promote a
// different node without this app doing anything (see useInfraTopology), and if it does, traffic
// really is flowing to the new leader, not the demoted (or unreachable) one the static id names.
// Highlighting the old id in that case would show the flow passing through a node that's actually
// down, which is exactly the wrong moment to get this wrong.
const REDIS_NODES = ['redis-master', 'redis-replica1', 'redis-replica2']
const MONGO_NODES = ['mongo1', 'mongo2', 'mongo3']

function resolveLeader(nodes: readonly string[], fallback: string, roles: Record<string, string>): string {
  return nodes.find((id) => roles[id] === 'master' || roles[id] === 'primary') ?? fallback
}

export function isTrafficFlowEdge(from: string, to: string, roles: Record<string, string> = {}): boolean {
  const redisLeader = resolveLeader(REDIS_NODES, 'redis-master', roles)
  const mongoLeader = resolveLeader(MONGO_NODES, 'mongo1', roles)
  return TRAFFIC_FLOW_EDGES.some(([f, t]) => {
    const resolvedTo = t === 'redis-master' ? redisLeader : t === 'mongo1' ? mongoLeader : t
    return f === from && resolvedTo === to
  })
}
