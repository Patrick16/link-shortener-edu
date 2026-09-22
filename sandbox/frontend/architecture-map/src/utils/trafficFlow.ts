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

export function isTrafficFlowEdge(from: string, to: string): boolean {
  return TRAFFIC_FLOW_EDGES.some(([f, t]) => f === from && t === to)
}
