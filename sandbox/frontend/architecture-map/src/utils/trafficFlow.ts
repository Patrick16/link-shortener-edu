// The connections k6-scripts/*.js actually exercise. smoke.js/spike.js hit both the create path
// and the redirect path (which also always publishes a click event, regardless of the k6 client's
// own redirects:0 setting - that's a client-side option, RedirectApi processes the request fully
// either way); read-heavy.js only exercises the redirect side. Traffic now goes through nginx
// (see sandbox/infra/nginx/nginx.conf), not straight to link-api/redirect-api, so that hop is part
// of the real path too. Used to highlight/animate the edges real traffic is flowing through while
// a run is active, rather than animating the whole graph indiscriminately.
export const TRAFFIC_FLOW_EDGES: ReadonlyArray<readonly [string, string]> = [
  ['k6', 'nginx'],
  ['nginx', 'link-api'],
  ['nginx', 'redirect-api'],
  ['link-api', 'rabbitmq'],
  ['rabbitmq', 'shortener-service'],
  ['shortener-service', 'links-db'],
  ['redirect-api', 'redis'],
  ['redirect-api', 'links-db'],
  ['redirect-api', 'rabbitmq'],
  ['rabbitmq', 'traffic-service'],
  ['traffic-service', 'clicks-db'],
]

export function isTrafficFlowEdge(from: string, to: string): boolean {
  return TRAFFIC_FLOW_EDGES.some(([f, t]) => f === from && t === to)
}
