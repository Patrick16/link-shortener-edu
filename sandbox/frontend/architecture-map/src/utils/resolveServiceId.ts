import type { ArchComponent } from '../types/architecture'

// Most architecture.json component ids are already the real docker-compose service name
// (link-api, redis, rabbitmq, ...). The three "-db" components are the exception - they're all
// logically-separate databases living inside the single `postgres` container, so their real
// controllable service id comes from the "compose" link's "...#serviceName" suffix instead.
export function resolveServiceId(component: ArchComponent, knownServiceIds: Set<string>): string | null {
  if (knownServiceIds.has(component.id)) {
    return component.id
  }

  const compose = component.details.links?.compose
  const hashIndex = compose?.lastIndexOf('#') ?? -1
  if (compose && hashIndex >= 0) {
    const serviceId = compose.slice(hashIndex + 1)
    if (knownServiceIds.has(serviceId)) {
      return serviceId
    }
  }

  return null
}
