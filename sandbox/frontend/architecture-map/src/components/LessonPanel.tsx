import { Fragment, useEffect, useState } from 'react'
import architectureData from '../data/architecture.json'
import { controlApi, ControlApiError } from '../api/controlApi'
import { applyConfigPatch, captureLessonLiveConfig, matchesPatch, type LessonLiveConfig } from '../utils/infraConfig'
import { getQuickLinks } from '../utils/quickLink'
import { observabilityLinks } from '../utils/observabilityLinks'
import { markLessonCompleted } from '../utils/lessonProgress'
import type { ArchitectureData } from '../types/architecture'
import type { InfraStatus } from '../types/controlApi'
import type { LessonDef, RunResult, Trigger, WatchHint } from '../types/lessons'
import type { TrafficRunState } from '../hooks/useTrafficRun'

const data = architectureData as unknown as ArchitectureData
const metaById = new Map(data.components.map((c) => [c.id, c]))

interface Props {
  lesson: LessonDef
  onClose: () => void
  onBackToList: () => void
  onNavigate: (lessonId: string) => void
  onInfraChanged: () => void
  trafficRun: TrafficRunState
}

type Phase = 'run1' | 'run2'

// How long a chaos action's recovery is observed before giving up and reporting "stayed down" -
// long enough for Sentinel/rs0 to actually complete a real failover (seconds, not instant), short
// enough that a lesson with no fix (Postgres primary) doesn't hang the UI indefinitely.
const RECOVERY_WINDOW_MS = 15_000

function substitute(template: string, metrics: Record<string, number | string>): string {
  return template.replace(/\{\{(\w+)\}\}/g, (_, key: string) => (metrics[key] !== undefined ? String(metrics[key]) : `{{${key}}}`))
}

function metricsFromReport(report: NonNullable<TrafficRunState['report']>): Record<string, number> {
  return {
    p95Ms: Math.round(report.httpReqDuration?.p95 ?? 0),
    errorRatePct: Math.round(report.failedRequestRate * 1000) / 10,
  }
}

// Only Redis Sentinel/Mongo's replica set expose a live "who's in charge now" check - Postgres has
// no promote/repmgr/patroni anywhere in this stack (see lesson 4.5's own honest "no fix" note), and
// shortener-service/pgcat have no equivalent role concept at all, so recovery there is reported as
// "still down after the observation window" rather than guessed at.
async function isReachableAgain(targetServiceId: string): Promise<boolean> {
  try {
    if (targetServiceId === 'redis-master') {
      const topology = await controlApi.getRedisTopology()
      return topology.roles.some((r) => r.role === 'master')
    }
    if (targetServiceId === 'mongo1') {
      const topology = await controlApi.getMongoTopology()
      return topology.roles.some((r) => r.role === 'primary')
    }
    return false
  } catch {
    return false
  }
}

async function observeRecovery(targetServiceId: string, windowMs: number): Promise<number | null> {
  const start = Date.now()
  while (Date.now() - start < windowMs) {
    await new Promise((resolve) => setTimeout(resolve, 1000))
    if (await isReachableAgain(targetServiceId)) return Math.round((Date.now() - start) / 1000)
  }
  return null
}

async function runTracedRequest(trigger: Extract<Trigger, { kind: 'traced-request' }>): Promise<RunResult> {
  const result = await controlApi.tracedRequest({ serviceId: trigger.serviceId, method: trigger.method, path: trigger.path, setup: trigger.setup })
  return { metrics: { latencyMs: Math.round(result.latencyMs) }, traceId: result.traceId }
}

// A config change that touches any cache toggle recreates link-api/redirect-api/
// shortener-service (see InfraToggleService's RecreateCacheUsingServicesAsync) - that fresh
// container's first real request always eats a JIT/connection-pool warm-up cost of its own,
// unrelated to whatever this lesson is actually teaching. A load-test lesson's hundreds of
// requests average that one slow outlier away; a traced-request lesson measures exactly ONE
// request with nothing to average it away, so without this, "Run 1"/"Run 2" can report whichever
// side of the comparison happened to land on a cold container - found live (2026-11-06): lesson
// 1.3 showed negative caching making a redirect *slower* (639ms vs 244ms), the inverse of its own
// point, purely from this noise. One discarded warm-up call right after the config settles
// absorbs the cold-start cost before the real, measured call.
//
// --wait on the recreate itself (InfraToggleService) already closes the "container not healthy
// yet" window, but nginx's own upstream can still take one extra beat to notice a just-recreated
// backend's new IP even once Docker calls it healthy - found live: an occasional 502 on the
// warm-up call right after a toggle, gone on an immediate retry. One retry is enough; this has
// never been seen to need a second.
//
// Exported (not a LessonPanel-local function) so LessonsModal's own "Set config" button - this
// app's actual primary path into a lesson (Lessons list -> Set config -> Open -> Run 1) - gets the
// exact same warm-up LessonPanel's own restart()/applyFix() do, instead of a second copy drifting.
export async function warmUpIfTracedRequest(lesson: LessonDef): Promise<void> {
  if (lesson.trigger.kind !== 'traced-request') return
  const { trigger } = lesson
  try {
    const probe = async () => controlApi.tracedRequest({ serviceId: trigger.serviceId, method: trigger.method, path: trigger.path, setup: trigger.setup })
    const first = await probe()
    if (first.statusCode === 502 || first.statusCode === 503) {
      await probe()
    }
  } catch {
    // Not surfaced - the real call right after this still runs and reports its own error if the
    // dependency is genuinely down.
  }
}

// Chaos actions are the only trigger kind this panel resolves itself end-to-end (load-test defers
// to the shared useTrafficRun/SignalR flow - see the effect below) - stop/degrade, then either
// probe real traffic (when the lesson declares probeEndpointId) or poll for reachability, then
// heal a `stop` back (a `degrade` already expires on its own durationSeconds).
//
// Two different lesson shapes share this one trigger kind, and need two different signals:
// - "did a request succeed against the degraded dependency" (4.1/4.4/4.7's own story - a circuit
//   breaker, a messaging-mode switch, a pooler instance) needs a REAL request fired during the
//   outage window - container reachability has nothing to do with it (found live 2026-11-06:
//   isReachableAgain doesn't even know shortener-service/pgcat, so these three lessons always
//   reported a flat 100%/100%, never showing their own fix actually working). That's probeEndpointId.
// - "did a replacement get elected" (4.6's own story - an un-sentineled Mongo set has nothing to
//   fail over to) genuinely IS about container/role reachability, not request traffic - no probe
//   needed, isReachableAgain's existing redis-master/mongo1 checks are the right signal as-is.
async function runChaos(trigger: Extract<Trigger, { kind: 'chaos' }>): Promise<RunResult> {
  if (trigger.action === 'stop') {
    await controlApi.stop(trigger.targetServiceId)
  } else {
    await controlApi.degrade(trigger.targetServiceId, { type: 'Partition', amount: 100, durationSeconds: Math.round(RECOVERY_WINDOW_MS / 1000) })
  }

  if (trigger.probeEndpointId) {
    const probe = await controlApi.chaosProbe({ endpointId: trigger.probeEndpointId, count: trigger.probeCount ?? 5 })
    if (trigger.action === 'stop') {
      await controlApi.start(trigger.targetServiceId).catch(() => {})
    }
    return { metrics: { errorRatePct: probe.errorRatePct, p95Ms: Math.round(probe.p95Ms) } }
  }

  const recoveredAfterSec = await observeRecovery(trigger.targetServiceId, RECOVERY_WINDOW_MS)

  if (trigger.action === 'stop') {
    await controlApi.start(trigger.targetServiceId).catch(() => {})
  }

  return {
    metrics: {
      recoverySec: recoveredAfterSec ?? Math.round(RECOVERY_WINDOW_MS / 1000),
      errorRatePct: recoveredAfterSec == null ? 100 : 0,
    },
  }
}

// Covers every ConfigPatch field a lesson can declare, not just infra/npgsqlPoolSize - found live
// (2026-11-06) that e.g. lesson 4.2's geoIpBulkheadLimit axis rendered no badge at all. Reads start
// OR target (whichever declares a given key - most lessons only declare the axis they change in
// one of the two) so the badge shows regardless of which half of the lesson named it.
function ConfigBadges({ lesson, live }: { lesson: LessonDef; live: LessonLiveConfig | null }) {
  const declared = { ...lesson.config.start, ...lesson.config.target }
  const infraKeys = Object.keys({ ...lesson.config.start.infra, ...lesson.config.target.infra }) as (keyof InfraStatus)[]

  const scalarBadges: { label: string; value: string | number | undefined }[] = []
  if (declared.npgsqlPoolSize != null) scalarBadges.push({ label: 'npgsql-pool-size', value: live?.snapshot.npgsqlPoolSize ?? undefined })
  if (declared.rabbitMqPrefetchCount != null) scalarBadges.push({ label: 'rabbitmq-prefetch', value: live?.snapshot.rabbitMqPrefetchCount ?? undefined })
  if (declared.mongoReadPreference != null) scalarBadges.push({ label: 'mongo-read-preference', value: live?.snapshot.mongoReadPreference ?? undefined })
  if (declared.l1CacheEnabled != null) scalarBadges.push({ label: 'l1-cache', value: boolLabel(live?.snapshot.l1CacheEnabled) })
  if (declared.negativeCacheEnabled != null) scalarBadges.push({ label: 'negative-cache', value: boolLabel(live?.snapshot.negativeCacheEnabled) })
  if (declared.circuitBreakersEnabled != null) scalarBadges.push({ label: 'circuit-breakers', value: boolLabel(live?.circuitBreakersEnabled) })
  if (declared.geoIpBulkheadLimit != null) scalarBadges.push({ label: 'geoip-bulkhead', value: live?.geoIpBulkheadLimit ?? undefined })
  if (declared.mongoHedgeDelayMs != null) scalarBadges.push({ label: 'mongo-hedge-delay', value: live?.mongoHedgeDelayMs ?? undefined })
  if (declared.replicas) {
    for (const serviceId of Object.keys(declared.replicas)) {
      scalarBadges.push({ label: `${serviceId} replicas`, value: live?.replicaCounts.get(serviceId) ?? undefined })
    }
  }

  if (infraKeys.length === 0 && scalarBadges.length === 0) return null

  return (
    <div className="lesson-config-badges">
      {infraKeys.map((key) => {
        const value = live?.snapshot.infra[key]
        const on = typeof value === 'boolean' ? value : undefined
        return (
          <span key={key} className={on === undefined ? 'infra-toggle-btn' : on ? 'infra-toggle-btn infra-toggle-btn-on' : 'infra-toggle-btn infra-toggle-btn-off'}>
            {key} {typeof value === 'boolean' ? (value ? 'ON' : 'OFF') : String(value ?? '?')}
          </span>
        )
      })}
      {scalarBadges.map((b) => (
        <span key={b.label} className="lesson-value-badge">
          {b.label}: {b.value ?? '?'}
        </span>
      ))}
    </div>
  )
}

function boolLabel(value: boolean | null | undefined): string | undefined {
  return value == null ? undefined : value ? 'ON' : 'OFF'
}

function WatchHintView({ hint, traceId }: { hint: WatchHint; traceId: string | undefined }) {
  if (hint.kind === 'node-link') {
    const component = metaById.get(hint.nodeId)
    const link = component && getQuickLinks(component).find((l) => l.label === hint.linkLabel)
    if (!link) return null
    return (
      <div className="lesson-watch-hint">
        <a className="node-panel-quick-link" href={link.url} target="_blank" rel="noreferrer">
          {link.label} ↗
        </a>
        <p className="lesson-hint-note">{hint.note}</p>
      </div>
    )
  }

  if (hint.kind === 'observability-link') {
    const link = observabilityLinks.find((l) => l.label === hint.label)
    if (!link) return null
    return (
      <div className="lesson-watch-hint">
        <a className="node-panel-quick-link" href={link.url} target="_blank" rel="noreferrer" title={link.title}>
          {link.label} ↗
        </a>
        <p className="lesson-hint-note">{hint.note}</p>
      </div>
    )
  }

  // trace-link: both trace-capable backends are offered (same "show every one, let the tooltip
  // explain which precondition applies" convention observabilityLinks.ts already uses) rather than
  // guessing which observability mode is currently running.
  if (!traceId) {
    return <p className="lesson-hint-note">Run the scenario first to get a trace id.</p>
  }
  const traceLinks = observabilityLinks.filter((l) => l.traceUrlTemplate)
  return (
    <div className="lesson-watch-hint">
      <p className="lesson-hint-note">{hint.note}</p>
      {traceLinks.map((l) => (
        <a
          key={l.label}
          className="node-panel-quick-link"
          href={l.traceUrlTemplate!.replace('{{traceId}}', traceId)}
          target="_blank"
          rel="noreferrer"
          title={l.title}
        >
          {hint.label} ({l.label}) ↗
        </a>
      ))}
    </div>
  )
}

function Metrics({ metrics }: { metrics: Record<string, number | string> }) {
  return (
    <>
      {Object.entries(metrics).map(([key, value]) => (
        <div key={key} className="lesson-metric-row">
          <span>{key}</span>
          <span>{value}</span>
        </div>
      ))}
    </>
  )
}

function Comparison({ run1, run2 }: { run1: RunResult; run2: RunResult }) {
  const keys = Object.keys(run1.metrics)
  return (
    <div className="lesson-comparison-grid">
      <div />
      <div className="lesson-comparison-head">Run 1</div>
      <div className="lesson-comparison-head lesson-comparison-head-accent">Run 2</div>
      {keys.map((key) => (
        <Fragment key={key}>
          <div className="lesson-comparison-label">{key}</div>
          <div>{run1.metrics[key]}</div>
          <div className="lesson-comparison-better">{run2.metrics[key]}</div>
        </Fragment>
      ))}
    </div>
  )
}

// Mounted in app-sidebar-left the same way NodePanel/K6ConfigPanel are (a new Selection kind in
// App.tsx, not a modal) - see .notes/PLAN.md's Lesson Mode entry for why: it has to stay open
// while the graph underneath keeps reacting to Topology-panel/Presets changes.
export function LessonPanel({ lesson, onClose, onBackToList, onNavigate, onInfraChanged, trafficRun }: Props) {
  const [live, setLive] = useState<LessonLiveConfig | null>(null)
  const [run1, setRun1] = useState<RunResult | null>(null)
  const [run2, setRun2] = useState<RunResult | null>(null)
  const [fixExpanded, setFixExpanded] = useState(false)
  const [pendingPhase, setPendingPhase] = useState<Phase | null>(null)
  const [applying, setApplying] = useState(false)
  const [error, setError] = useState<string | null>(null)

  function refreshLive() {
    captureLessonLiveConfig().then(setLive).catch(() => {})
  }

  useEffect(() => {
    setRun1(null)
    setRun2(null)
    setFixExpanded(false)
    setPendingPhase(null)
    setError(null)
    refreshLive()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [lesson.id])

  // `live` otherwise only ever refreshes after THIS panel's own Restart/Apply-fix buttons - found
  // live (2026-11-06): dialing a lesson's config in by hand (Topology panel, a node's own control,
  // Presets) leaves it stale, so matchesStart/matchesTarget keep failing even once the real infra
  // genuinely matches, and Run 1/Run 2/Next lesson all stay stuck disabled until the user goes
  // through this panel's own "Apply for me" instead. A lesson's config can span almost any toggle
  // this app has (replicas, prefetch, pool sizes, read preference, ...), so there's no single other
  // hook's state to subscribe to instead - a light poll while this panel is open is simpler and
  // catches every case, same "no polling elsewhere, so poll here" tradeoff useInfraStatus's own
  // comment already accepts for its narrower 4-field slice.
  useEffect(() => {
    const id = setInterval(refreshLive, 3000)
    return () => clearInterval(id)
  }, [])

  function finishPhase(phase: Phase, result: RunResult) {
    if (phase === 'run1') {
      setRun1(result)
    } else {
      setRun2(result)
      markLessonCompleted(lesson.id)
    }
    setPendingPhase(null)
  }

  // Only the load-test trigger kind needs this - it reports completion over the shared
  // useTrafficRun/SignalR connection (same one K6ConfigPanel drives), not a direct await.
  useEffect(() => {
    if (!pendingPhase || lesson.trigger.kind !== 'load-test') return
    if (trafficRun.running) return
    if (trafficRun.report) {
      const metrics = metricsFromReport(trafficRun.report)
      if (!lesson.watchQueueName) {
        finishPhase(pendingPhase, { metrics })
        return
      }
      // queueDepth can't come from TrafficReport (k6 has no idea RabbitMQ exists) - a separate
      // live poll right after the run, same "fresh rabbitmqctl snapshot" the RabbitMQ node's own
      // DLQ panel already reads. Best-effort: a failed poll still finishes the phase, just
      // without this one metric, rather than leaving the panel stuck on "running" forever.
      controlApi.getQueueDepth(lesson.watchQueueName).then(
        (depth) => finishPhase(pendingPhase, { metrics: { ...metrics, queueDepth: depth.messages } }),
        () => finishPhase(pendingPhase, { metrics }),
      )
    } else if (trafficRun.error) {
      setError(trafficRun.error)
      setPendingPhase(null)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [trafficRun.running, trafficRun.report, trafficRun.error, pendingPhase])

  async function runScenario(phase: Phase) {
    setError(null)
    setPendingPhase(phase)
    const { trigger } = lesson
    try {
      if (trigger.kind === 'load-test') {
        await trafficRun.start({ scenario: `lesson-${lesson.id}-${phase}`, vus: trigger.vus, durationSeconds: trigger.durationSeconds, steps: trigger.steps })
        return // completion handled by the effect above
      }
      const result = trigger.kind === 'traced-request' ? await runTracedRequest(trigger) : await runChaos(trigger)
      finishPhase(phase, result)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
      setPendingPhase(null)
    }
  }

  async function restart() {
    setApplying(true)
    setError(null)
    try {
      await applyConfigPatch(lesson.config.start)
      onInfraChanged()
      await warmUpIfTracedRequest(lesson)
      refreshLive()
      setRun1(null)
      setRun2(null)
      setFixExpanded(false)
    } catch {
      setError("Failed to reset this lesson's config - see the console for details.")
    } finally {
      setApplying(false)
    }
  }

  async function applyFix() {
    setApplying(true)
    setError(null)
    try {
      await applyConfigPatch(lesson.config.target)
      onInfraChanged()
      await warmUpIfTracedRequest(lesson)
      refreshLive()
    } catch {
      setError('Failed to apply the fix - see the console for details.')
    } finally {
      setApplying(false)
    }
  }

  const matchesStart = live ? matchesPatch(live, lesson.config.start) : false
  const matchesTarget = live ? matchesPatch(live, lesson.config.target) : false
  const running = pendingPhase !== null

  return (
    <div className="side-panel lesson-panel">
      <div className="side-panel-header">
        <h2>
          {lesson.id} {lesson.title}
        </h2>
        <button onClick={onClose} aria-label="Close">
          &times;
        </button>
      </div>

      <div className="lesson-panel-toprow">
        <button onClick={onBackToList} className="lesson-back-link">
          &larr; Back to lessons
        </button>
        <button onClick={restart} disabled={applying}>
          &#x27F2; Restart lesson
        </button>
      </div>

      <p className="lesson-panel-problem">{lesson.problem.summary}</p>
      {lesson.problem.docLink && <p className="lesson-panel-doclink">{lesson.problem.docLink}</p>}

      <ConfigBadges lesson={lesson} live={live} />

      <div className="lesson-card">
        <div className="lesson-card-label">TEST CONFIGURATION</div>
        <div className="lesson-card-mono">{lesson.trigger.description}</div>
      </div>

      {lesson.watch.hints.length > 0 && (
        <div className="lesson-card">
          <div className="lesson-card-label">WHERE ELSE TO LOOK</div>
          {lesson.watch.hints.map((hint, i) => (
            <WatchHintView key={i} hint={hint} traceId={(run2 ?? run1)?.traceId} />
          ))}
        </div>
      )}

      {error && <p className="service-card-error">{error}</p>}

      {!run1 && (
        <>
          <button className="run-traffic-button lesson-run-button" disabled={!matchesStart || running} onClick={() => runScenario('run1')}>
            {pendingPhase === 'run1' ? 'Running...' : '▶ Run scenario (run 1)'}
          </button>
          {live && !matchesStart && (
            <p className="lesson-hint">Live config doesn&apos;t match this lesson&apos;s starting point yet - use &quot;Set config&quot; from the list, or Restart lesson.</p>
          )}
        </>
      )}

      {run1 && (
        <>
          <div className="lesson-card">
            <div className="lesson-card-label">RESULT — RUN 1</div>
            <Metrics metrics={run1.metrics} />
          </div>
          <p className="lesson-conclusion">{substitute(lesson.conclusion.afterRun1, run1.metrics)}</p>

          <div className="lesson-card lesson-fix-card">
            <div className="lesson-fix-header" onClick={() => setFixExpanded((v) => !v)}>
              <span>PROPOSED FIX</span>
              <span>{fixExpanded ? '▾ collapse' : '▸ expand'}</span>
            </div>
            {!fixExpanded && <p className="lesson-hint-note">Try to work out the fix yourself before expanding.</p>}
            {fixExpanded && (
              <>
                <p>{lesson.fix.explanation}</p>
                <button onClick={applyFix} disabled={applying}>
                  {applying ? 'Applying...' : 'Apply for me'}
                </button>
              </>
            )}
          </div>

          {!run2 && (
            <>
              <button className="run-traffic-button lesson-run-button" disabled={!matchesTarget || running} onClick={() => runScenario('run2')}>
                {pendingPhase === 'run2' ? 'Running...' : '▶ Run scenario (run 2)'}
              </button>
              {live && !matchesTarget && <p className="lesson-hint">Apply the fix above (or dial it in yourself) before running again.</p>}
            </>
          )}
        </>
      )}

      {run1 && run2 && (
        <>
          <div className="lesson-card">
            <div className="lesson-card-label">RUN COMPARISON</div>
            <Comparison run1={run1} run2={run2} />
          </div>
          <p className="lesson-conclusion">{substitute(lesson.conclusion.afterRun2, run2.metrics)}</p>
        </>
      )}

      <div className="lesson-panel-footer">
        <button disabled={!matchesTarget || !lesson.nextLessonId} onClick={() => lesson.nextLessonId && onNavigate(lesson.nextLessonId)}>
          Next lesson →
        </button>
      </div>
    </div>
  )
}
