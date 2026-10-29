import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Recreates reporting-api only - the delay is read once at startup by
// MongoExtensions.AddMongoClickMetaReader, not a live setting. Only takes effect while the Mongo
// replicas toggle is on (rs0 has all 3 members) - otherwise there's nothing to hedge against.
export function MongoHedgeDelayControl(_props: CapabilityControlProps) {
  const [current, setCurrent] = useState<number | null>(null)
  const [delayMs, setDelayMs] = useState(15)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getMongoHedgeDelay()
      .then((r) => {
        setCurrent(r.delayMs)
        setDelayMs(r.delayMs)
      })
      .catch(() => {})
  }, [])

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      const r = await controlApi.setMongoHedgeDelay(delayMs)
      setCurrent(r.delayMs)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="scale-control mongo-hedge-delay-control">
      <label>
        Mongo hedge delay (ms)
        <input type="number" value={delayMs} onChange={(e) => setDelayMs(Number(e.target.value))} min={0} max={5000} disabled={busy} />
      </label>
      <button onClick={apply} disabled={busy || delayMs === current}>
        {busy ? 'Applying...' : `Set to ${delayMs}ms`}
      </button>
      <p className="infra-toggle-description">
        How long GET /reports/&#123;hash&#125;/clicks waits on one Mongo replica before also racing the
        other - whichever answers first wins, the other is cancelled. 0 means always race both from
        the start. Only has an effect while the Mongo replicas toggle is on. Recreates reporting-api
        (read once at startup) - takes a few seconds.
      </p>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
