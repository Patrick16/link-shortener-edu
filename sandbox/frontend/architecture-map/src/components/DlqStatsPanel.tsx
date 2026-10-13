import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { DeadLetterQueueStats } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const DLQ_POLL_MS = 3000

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
      setClearResult(res.cleared === 0 ? 'Nothing to clear.' : `Cleared ${res.cleared} message${res.cleared === 1 ? '' : 's'}.`)
      // The poll loop above would pick this up within DLQ_POLL_MS anyway, but refetching right away
      // avoids the counters looking stuck at their pre-clear values for up to 3s after a successful clear.
      controlApi
        .getDlqStats()
        .then(setDlqStats)
        .catch(() => {})
    } catch (err) {
      setClearError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setClearing(false)
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
          <div className="connection-stats-row" key={queue.queueName}>
            <span className="connection-stats-db">{queue.queueName}</span>
            <span className="connection-stats-value">{queue.messageCount}</span>
          </div>
        ))
      )}
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
          : 'Nothing consumes these today - a non-zero count means a message exhausted its retries (or failed to ' +
            'deserialize) and is sitting there unread. "Clear DLQ" purges them for demo purposes only - this ' +
            'project never changes its event contracts, so no real poison messages are expected here in practice.'}
      </p>
    </div>
  )
}
