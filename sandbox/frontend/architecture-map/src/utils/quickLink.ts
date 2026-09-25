import type { ArchComponent } from '../types/architecture'

export interface QuickLink {
  label: string
  url: string
}

// Distinct from ComponentCard's "links" (repo-relative code/config paths, rendered as plain text) -
// this is a real clickable URL to whatever live admin/docs tool is appropriate for a node's own
// tech, computed from its id/type rather than authored per node in architecture.json. Every one of
// these tools is already part of the running stack (see docker-compose.yml); this just points at it.
// Returns a list, not a single link - a node can have more than one relevant tool (e.g. link-api gets
// both its own Scalar docs and the sqlite-fallback viewer for its RabbitMQ outage store).
export function getQuickLinks(component: ArchComponent): QuickLink[] {
  const links: QuickLink[] = []

  if (component.id === 'frontend-app' && component.details.port) {
    links.push({ label: 'Open app', url: `http://localhost:${component.details.port}` })
  }

  // The three real .NET Web APIs (not the workers, which have no HTTP surface besides health
  // checks) all mount Scalar at /scalar in Development - see each service's own Program.cs. Their
  // own `details.port` is already the externally-reachable one (nginx's port for link-api/
  // redirect-api, the direct port for auth-api), so no separate port needs recording here.
  if (component.type === 'service' && component.details.port) {
    links.push({ label: 'API docs (Scalar)', url: `http://localhost:${component.details.port}/scalar` })
  }

  // link-api/redirect-api each fall back to their own sqlite file when RabbitMQ is unreachable (see
  // SqliteMessageFallbackStore) - a dedicated sqlite-web viewer per service, not shared, since each
  // keeps its own file (see docker-compose.yml's link-api-fallback-viewer/redirect-api-fallback-viewer).
  if (component.id === 'link-api') {
    links.push({ label: 'SQLite fallback', url: 'http://localhost:8086' })
  }
  if (component.id === 'redirect-api') {
    links.push({ label: 'SQLite fallback', url: 'http://localhost:8087' })
  }

  if (component.id === 'rabbitmq' && component.details.managementPort) {
    links.push({ label: 'RabbitMQ dashboard', url: `http://localhost:${component.details.managementPort}` })
  }

  // One shared GUI per cluster, not per node - RedisInsight/Mongo Express/pgweb aren't tied to a
  // specific member's own port, they're separate tools pointed at the whole master+replicas / replica
  // set / cluster. Every node in that family gets the same link since clicking any one of them means
  // "show me this cluster's admin UI", not "show me this one container specifically".
  if (component.id.startsWith('redis')) {
    links.push({ label: 'RedisInsight', url: 'http://localhost:5540' })
  }

  if (component.id.startsWith('mongo')) {
    links.push({ label: 'Mongo Express', url: 'http://localhost:8085' })
  }

  // Postgres family: the 3 logical databases on the primary, the two streaming replicas, and pgcat
  // (the pooler sitting in front of all of them) - pgweb connects to the primary directly (see
  // docker-compose.yml's pgweb service) and can switch databases from its own connection screen.
  if (component.type === 'database' || component.id === 'pgcat' || component.id.startsWith('postgres-replica')) {
    links.push({ label: 'pgweb', url: 'http://localhost:8084' })
  }

  return links
}
