import type { InfraStatus } from '../types/controlApi'

// Static, capability-driven badges shown on a node when which job it's currently doing isn't
// obvious just from "visible vs hidden" - today only nginx needs this: it has two independent
// roles (HTTP load balancing, gated by nginxBypassed; internal gRPC fan-out, gated by
// messagingMode) and can be doing either, both, or - once hidden by the topology filter - neither.
// See architecture.json's activeWhen on the nginx component for the same two conditions.
export function getRoleBadges(componentId: string, status: InfraStatus | null): string[] {
  if (componentId !== 'nginx' || !status) return []
  const badges: string[] = []
  if (!status.nginxBypassed) badges.push('LB')
  if (status.messagingMode === 'grpc') badges.push('gRPC')
  return badges
}
