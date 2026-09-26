import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Recreates shortener-service and traffic-service - prefetch is read once at consumer startup, not
// a live setting (see RabbitMqConsumer's own _prefetchCount field).
export function RabbitMqPrefetchControl(_props: CapabilityControlProps) {
  const [current, setCurrent] = useState<number | null>(null)
  const [prefetchCount, setPrefetchCount] = useState(10)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getRabbitMqPrefetch()
      .then((r) => {
        setCurrent(r.prefetchCount)
        setPrefetchCount(r.prefetchCount)
      })
      .catch(() => {})
  }, [])

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      const r = await controlApi.setRabbitMqPrefetch(prefetchCount)
      setCurrent(r.prefetchCount)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="scale-control rabbitmq-prefetch-control">
      <label>
        Consumer prefetch (QoS)
        <input type="number" value={prefetchCount} onChange={(e) => setPrefetchCount(Number(e.target.value))} min={1} max={1000} disabled={busy} />
      </label>
      <button onClick={apply} disabled={busy || prefetchCount === current}>
        {busy ? 'Applying...' : `Set to ${prefetchCount}`}
      </button>
      <p className="infra-toggle-description">Recreates shortener-service and traffic-service (read once at consumer startup) - takes a few seconds.</p>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
