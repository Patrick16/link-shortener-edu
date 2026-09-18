// The connections k6-scripts/*.js actually exercise (create a link via LinkApi, then visit it via
// RedirectApi) - both smoke.js and spike.js drive this same underlying flow, just at different
// rates. Used to highlight/animate the edges real traffic is flowing through while a run is active,
// rather than animating the whole graph indiscriminately.
export const TRAFFIC_FLOW_EDGES: ReadonlyArray<readonly [string, string]> = [
  ['link-api', 'rabbitmq'],
  ['rabbitmq', 'shortener-service'],
  ['shortener-service', 'links-db'],
  ['redirect-api', 'redis'],
  ['redirect-api', 'links-db'],
]

export function isTrafficFlowEdge(from: string, to: string): boolean {
  return TRAFFIC_FLOW_EDGES.some(([f, t]) => f === from && t === to)
}
