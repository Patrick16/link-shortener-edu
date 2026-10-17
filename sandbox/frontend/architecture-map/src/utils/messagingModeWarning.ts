import type { InfraStatus } from '../types/controlApi'

// Persistent, capability-driven reminder for the nodes affected by messaging-mode=grpc - the
// TopologyPanel's switch-time warning only fires once, when the toggle is flipped, but a node
// panel opened later (after the fan-out gap is forgotten) gives no other clue that its numbers are
// frozen. Scoped to the three nodes the gap actually touches (see
// sandbox/docs/scenarios/pitfalls/sync-dispatch-breaks-pubsub-fanout.md and
// RedirectController.cs's own comment for the mechanism this paraphrases) - not link-api, whose
// gRPC edge has only one consumer (ShortenerService) either way, so nothing there loses fan-out.
const WARNINGS: Record<string, string> = {
  'redirect-api':
    'gRPC messaging mode is active: click.tracked is dispatched straight to TrafficService instead of being published to RabbitMQ, so the other two fan-out consumers - ShortenerService’s click-count bump and ReportingService’s ClickHouse write - never receive it. Demo-only mode (shows sync coupling’s latency cost), not a production option.',
  'shortener-service':
    'gRPC messaging mode is active: RedirectApi dispatches click.tracked straight to TrafficService in this mode, so this service never receives it - the "my links" click-count counter is frozen until messaging mode switches back. Demo-only mode, not a production option.',
  'reporting-service':
    'gRPC messaging mode is active: RedirectApi dispatches click.tracked straight to TrafficService in this mode, so this service never receives it - ClickHouse-backed reports are frozen until messaging mode switches back. Demo-only mode, not a production option.',
}

export function getMessagingModeWarning(componentId: string, status: InfraStatus | null): string | null {
  if (!status || status.messagingMode !== 'grpc') return null
  return WARNINGS[componentId] ?? null
}
