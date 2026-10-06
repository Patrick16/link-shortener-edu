import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
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
      <p className="connection-stats-hint">
        {isStale
          ? "Last update failed - showing the most recent known counts, not necessarily current."
          : 'Nothing consumes these today - a non-zero count means a message exhausted its retries (or failed to ' +
            'deserialize) and is sitting there unread.'}
      </p>
    </div>
  )
}
