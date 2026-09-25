import type { ArchComponent } from '../types/architecture'

export interface QuickLink {
  label: string
  url: string
}

// Distinct from ComponentCard's "links" (repo-relative code/config paths, rendered as plain text) -
// this is a real clickable URL to whatever live admin/docs tool is appropriate for a node's own
// tech, computed from its id/type rather than authored per node in architecture.json. Every one of
// these tools is already part of the running stack (see docker-compose.yml); this just points at it.
export function getQuickLink(component: ArchComponent): QuickLink | null {
  if (component.id === 'frontend-app' && component.details.port) {
    return { label: 'Open app', url: `http://localhost:${component.details.port}` }
  }

  // The three real .NET Web APIs (not the workers, which have no HTTP surface besides health
  // checks) all mount Scalar at /scalar in Development - see each service's own Program.cs. Their
  // own `details.port` is already the externally-reachable one (nginx's port for link-api/
  // redirect-api, the direct port for auth-api), so no separate port needs recording here.
  if (component.type === 'service' && component.details.port) {
    return { label: 'API docs (Scalar)', url: `http://localhost:${component.details.port}/scalar` }
  }

  if (component.id === 'rabbitmq' && component.details.managementPort) {
    return { label: 'RabbitMQ dashboard', url: `http://localhost:${component.details.managementPort}` }
  }

  // One shared GUI per cluster, not per node - RedisInsight/Mongo Express aren't tied to a specific
  // member's own port, they're separate tools pointed at the whole master+replicas / replica set.
  // Every node in that family gets the same link since clicking any one of them means "show me this
  // cluster's admin UI", not "show me this one container specifically".
  if (component.id.startsWith('redis')) {
    return { label: 'RedisInsight', url: 'http://localhost:5540' }
  }

  if (component.id.startsWith('mongo')) {
    return { label: 'Mongo Express', url: 'http://localhost:8085' }
  }

  return null
}
