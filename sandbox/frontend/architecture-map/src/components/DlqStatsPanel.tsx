import { useEffect, useRef, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { DeadLetterMessageSample, DeadLetterQueueStats } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const DLQ_POLL_MS = 3000
const SAMPLE_SIZE = 5

// Polled, not fetch-once - same reasoning as PgcatConnectionsPanel/PostgresConnectionsPanel. Every
// "{queue}.dead" queue is declared up front by RabbitMqConsumer (see its own comment on
// QueueDeclareAsync), so this always has a row per consumer, zero-count or not - that's deliberate:
// seeing every row at 0 is what proves the counter itself is working, not just silent because
// nothing qualifies to show.
export function DlqStatsPanel(_props: CapabilityControlProps) {
  const [dlqStats, setDlqStats] = useState<DeadLetterQueueStats | null>(null)
  // Tracks only "did the last poll fail", not the error detail - this is a stale-data flag for the
  // viewer, not a diagnostic surface (that's what control-api's own logs are for).
  const [isStale, setIsStale] = useState(false)
  const [clearing, setClearing] = useState(false)
  const [clearResult, setClearResult] = useState<string | null>(null)
  const [clearError, setClearError] = useState<string | null>(null)

  // Only one queue's sample view is open at a time - this is a "peek before you replay" aid, not a
  // log viewer, so there's no need to juggle several expanded rows at once.
  const [expandedQueue, setExpandedQueue] = useState<string | null>(null)
  const [sampling, setSampling] = useState(false)
  const [sampleResult, setSampleResult] = useState<DeadLetterMessageSample[] | null>(null)
  const [sampleError, setSampleError] = useState<string | null>(null)

  // Replay's cooldown is enforced server-side as ONE lock shared across every queue (see
  // DlqStatsCapability), not per-queue - so a single in-flight flag here (rather than one per row)
  // matches what the backend actually does and stops the viewer from firing a second replay that
  // the server would just 429 anyway.
  const [replayingQueue, setReplayingQueue] = useState<string | null>(null)
  const [replayResult, setReplayResult] = useState<string | null>(null)
  const [replayError, setReplayError] = useState<string | null>(null)

  // Guards async setState calls the same way the poll loop's own `cancelled` flag below guards
  // its own - a ref (not state) since unmount detection doesn't need to trigger a re-render, and a
  // plain boolean captured by a closure wouldn't survive across separate async handlers. The
  // effect body re-arms the ref to true on every (re)run, not just at useRef's initializer - React
  // 18 StrictMode double-invokes this effect once in dev (mount -> cleanup -> mount again) without
  // actually discarding the component, so without this line the cleanup's `false` from that first
  // simulated unmount would stick forever and silently no-op every real async handler's setState
  // for the rest of the component's life (this was live-verified: it's what made Sample/Replay's
  // own setState calls never land, leaving the panel stuck on "Sampling...").
  const mountedRef = useRef(true)
  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    function refresh() {
      controlApi
        .getDlqStats()
        .then((s) => {
          if (cancelled) return
          setDlqStats(s)
          setIsStale(false)
        })
        .catch(() => !cancelled && setIsStale(true))
    }
    refresh()
    const interval = setInterval(refresh, DLQ_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [])

  async function clearDlq() {
    setClearing(true)
    setClearError(null)
    setClearResult(null)
    try {
      const res = await controlApi.clearDlq()
      if (!mountedRef.current) return
      setClearResult(res.cleared === 0 ? 'Nothing to clear.' : `Cleared ${res.cleared} message${res.cleared === 1 ? '' : 's'}.`)
      // The poll loop above would pick this up within DLQ_POLL_MS anyway, but refetching right away
      // avoids the counters looking stuck at their pre-clear values for up to 3s after a successful clear.
      controlApi
        .getDlqStats()
        .then((s) => mountedRef.current && setDlqStats(s))
        .catch(() => {})
      setExpandedQueue(null)
    } catch (err) {
      if (mountedRef.current) setClearError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      if (mountedRef.current) setClearing(false)
    }
  }

  async function toggleSample(queueName: string) {
    if (expandedQueue === queueName) {
      setExpandedQueue(null)
      return
    }
    setExpandedQueue(queueName)
    setSampling(true)
    setSampleError(null)
    setSampleResult(null)
    try {
      const res = await controlApi.sampleDlq(queueName, SAMPLE_SIZE)
      if (mountedRef.current) setSampleResult(res)
    } catch (err) {
      if (mountedRef.current) setSampleError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      if (mountedRef.current) setSampling(false)
    }
  }

  async function replay(queueName: string, count: number) {
    setReplayingQueue(queueName)
    setReplayError(null)
    setReplayResult(null)
    try {
      const res = await controlApi.replayDlq({ queueName, count })
      if (!mountedRef.current) return
      setReplayResult(
        res.replayed === 0 ? 'Nothing to replay.' : `Replayed ${res.replayed} message${res.replayed === 1 ? '' : 's'}.`,
      )
      // Same reasoning as clearDlq above - refetch right away instead of waiting for the next poll tick.
      controlApi
        .getDlqStats()
        .then((s) => mountedRef.current && setDlqStats(s))
        .catch(() => {})
      if (expandedQueue === queueName) {
        setExpandedQueue(null)
        setSampleResult(null)
      }
    } catch (err) {
      if (mountedRef.current) setReplayError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      if (mountedRef.current) setReplayingQueue(null)
    }
  }

  if (!dlqStats) {
    // Nothing fetched successfully yet - distinct from isStale, which only applies once there's a
    // previous reading on screen that a later poll failed to refresh.
    return isStale ? <p className="connection-stats-hint">Dead-letter queue data unavailable.</p> : null
  }

  return (
    <div className="connection-stats">
      <h4>Dead-letter queues {isStale && <span className="connection-stats-stale">(stale)</span>}</h4>
      {dlqStats.queues.length === 0 ? (
        <p className="connection-stats-hint">No dead-letter queues declared yet.</p>
      ) : (
        dlqStats.queues.map((queue) => (
          <div key={queue.queueName}>
            <div className="connection-stats-row">
              <span className="connection-stats-db">{queue.queueName}</span>
              <span className="connection-stats-value">{queue.messageCount}</span>
              <span className="dlq-row-actions">
                <button
                  onClick={() => toggleSample(queue.queueName)}
                  disabled={queue.messageCount === 0 && expandedQueue !== queue.queueName}
                >
                  {expandedQueue === queue.queueName ? 'Hide' : 'Sample'}
                </button>
                <button
                  onClick={() => replay(queue.queueName, queue.messageCount)}
                  disabled={queue.messageCount === 0 || replayingQueue !== null}
                >
                  {replayingQueue === queue.queueName ? 'Replaying...' : 'Replay'}
                </button>
              </span>
            </div>
            {expandedQueue === queue.queueName && (
              <div className="dlq-sample-list">
                {sampling && <p className="connection-stats-hint">Sampling...</p>}
                {sampleError && <p className="service-card-error">{sampleError}</p>}
                {sampleResult && sampleResult.length === 0 && (
                  <p className="connection-stats-hint">Queue is empty.</p>
                )}
                {sampleResult?.map((msg, i) => (
                  <div className="dlq-sample-item" key={msg.messageId ?? i}>
                    <div className="dlq-sample-meta">
                      <span>{msg.messageId ?? '(no message id)'}</span>
                      <span>{msg.reason ?? 'unknown reason'}</span>
                    </div>
                    <pre className="dlq-sample-payload">{msg.payload}</pre>
                  </div>
                ))}
              </div>
            )}
          </div>
        ))
      )}
      {replayResult && <p className="flush-cache-result">{replayResult}</p>}
      {replayError && <p className="service-card-error">{replayError}</p>}
      <div className="flush-cache-control">
        <button onClick={clearDlq} disabled={clearing || dlqStats.totalMessages === 0}>
          {clearing ? 'Clearing...' : 'Clear DLQ'}
        </button>
        {clearResult && <span className="flush-cache-result">{clearResult}</span>}
      </div>
      {clearError && <p className="service-card-error">{clearError}</p>}
      <p className="connection-stats-hint">
        {isStale
          ? "Last update failed - showing the most recent known counts, not necessarily current."
          : '"Sample" peeks at a few messages without removing them; "Replay" re-publishes everything ' +
            "currently in that queue onto the live queue it dead-lettered from (safe here since every " +
            'current consumer is redelivery-safe - see PLAN.md item 13). "Clear DLQ" purges them instead, ' +
            'for demo cleanup only.'}
      </p>
    </div>
  )
}
