// Lesson Mode's content types - see .notes/PLAN.md's "Feature: Lesson Mode" entry for the design
// rationale. Deliberately NOT a new config shape: ConfigPatch mirrors InfraConfigSnapshot's own
// field names 1:1, just with every one optional, so a lesson declares only the axis it teaches
// and utils/infraConfig.ts's matchesPatch/applyPatch never have to invent a second vocabulary.
import type { FlowStep, InfraStatus, TracedRequestSetup } from './controlApi'

export interface ConfigPatch {
  infra?: Partial<InfraStatus>
  // serviceId -> replica count, a subset of listScalableServices() - never every running
  // container the way InfraConfigSnapshot.replicas lists them.
  replicas?: Record<string, number>
  rabbitMqPrefetchCount?: number
  mongoReadPreference?: string
  npgsqlPoolSize?: number
  l1CacheEnabled?: boolean
  negativeCacheEnabled?: boolean
  circuitBreakersEnabled?: boolean
  geoIpBulkheadLimit?: number
  mongoHedgeDelayMs?: number
}

export type Trigger =
  // Steps/vus/duration are the exact same shape CustomScenario already uses (see controlApi.ts) -
  // built inline here rather than saved to ScenarioStore, so a lesson's own test run never shows
  // up in the user-facing Presets/Scenarios picker.
  | { kind: 'load-test'; steps: FlowStep[]; vus: number; durationSeconds: number; description: string }
  | { kind: 'traced-request'; serviceId: string; method: string; path: string; setup?: TracedRequestSetup; description: string }
  // probeEndpointId is required for a chaos lesson whose point is request-level behavior
  // (errorRatePct/p95Ms) - it's what actually gets called during the outage window. Omitted only
  // for a lesson whose real signal IS container reachability (e.g. "did a new primary get
  // elected"), where runChaos falls back to isReachableAgain/recoverySec instead - see
  // LessonPanel.tsx's own comment on runChaos.
  | {
      kind: 'chaos'
      action: 'stop' | 'degrade'
      targetServiceId: string
      description: string
      probeEndpointId?: string
      probeCount?: number
    }

export type WatchHint =
  // Resolved against utils/quickLink.ts's getQuickLinks(component) for nodeId, matched by label -
  // no separate links file, see PLAN.md.
  | { kind: 'node-link'; nodeId: string; linkLabel: string; note: string }
  // Resolved against utils/observabilityLinks.ts, matched by label.
  | { kind: 'observability-link'; label: string; note: string }
  // Only valid when this lesson's trigger is 'traced-request' - resolved via the matching
  // observabilityLinks entry's traceUrlTemplate + the run's captured traceId.
  | { kind: 'trace-link'; label: string; note: string }

export interface LessonConclusion {
  afterRun1: string
  afterRun2: string
}

export interface LessonDef {
  id: string
  chapterId: string
  title: string
  nextLessonId: string | null
  problem: { summary: string; docLink?: string }
  // Which real graph nodes get the shared highlight treatment while this lesson is open - see
  // ServiceNode.tsx/Diagram.tsx's highlightedNodeIds. The graph itself never changes for a lesson
  // beyond this.
  highlightNodeIds: string[]
  // Exact RabbitMQ queue name (e.g. "shortener-service.link-created") to poll via
  // controlApi.getQueueDepth once a load-test run finishes, merged into that run's metrics as
  // "queueDepth" - k6 itself has no idea RabbitMQ exists, so this can't come from TrafficReport
  // the way p95Ms/errorRatePct do. Omit for a lesson that doesn't watch a queue.
  watchQueueName?: string
  config: { start: ConfigPatch; target: ConfigPatch }
  trigger: Trigger
  watch: { metrics: string[]; hints: WatchHint[] }
  conclusion: LessonConclusion
  fix: { explanation: string }
}

export interface ChapterDef {
  id: string
  title: string
}

export interface LessonsFile {
  chapters: ChapterDef[]
  lessons: LessonDef[]
}

// Captured once per run, kept in LessonPanel's own local state (session-only beyond localStorage
// resume) - never sent back to the backend as its own entity.
export interface RunResult {
  metrics: Record<string, number | string>
  traceId?: string
  traceLink?: string
}
