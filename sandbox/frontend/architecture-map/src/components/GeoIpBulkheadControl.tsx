import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Recreates traffic-service and reporting-service - the limit is read once at startup by
// GeoIpExtensions.AddGeoIpResolution's concurrency limiter, not a live setting.
export function GeoIpBulkheadControl(_props: CapabilityControlProps) {
  const [current, setCurrent] = useState<number | null>(null)
  const [limit, setLimit] = useState(10)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getGeoIpBulkheadLimit()
      .then((r) => {
        setCurrent(r.limit)
        setLimit(r.limit)
      })
      .catch(() => {})
  }, [])

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      const r = await controlApi.setGeoIpBulkheadLimit(limit)
      setCurrent(r.limit)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="scale-control geoip-bulkhead-control">
      <label>
        Geo-IP concurrency limit
        <input type="number" value={limit} onChange={(e) => setLimit(Number(e.target.value))} min={1} max={1000} disabled={busy} />
      </label>
      <button onClick={apply} disabled={busy || limit === current}>
        {busy ? 'Applying...' : `Set to ${limit}`}
      </button>
      <p className="infra-toggle-description">Caps in-flight ip-api.com calls - excess calls are rejected immediately (no geo data) instead of queuing. Recreates traffic-service and reporting-service (read once at startup) - takes a few seconds.</p>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
