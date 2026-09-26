import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// The client-pool-size half of the picture the Connections panel below already shows the
// server-pool-size half of ("clients: apps -> pgcat / servers: pgcat -> postgres"). Applied to
// every DB-touching service's Npgsql connection string, distinct from pgcat's own pool_size (item
// above) - shrinking this demonstrates Npgsql's own client-side pool-wait saturation, independent
// of anything pgcat does. Recreates 5 containers, same allowlist as the pgcat enable/disable toggle.
export function NpgsqlPoolSizeControl(_props: CapabilityControlProps) {
  const [current, setCurrent] = useState<number | null>(null)
  const [poolSize, setPoolSize] = useState(100)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getNpgsqlPoolSize()
      .then((s) => {
        setCurrent(s.poolSize)
        setPoolSize(s.poolSize)
      })
      .catch(() => {})
  }, [])

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      const s = await controlApi.setNpgsqlPoolSize(poolSize)
      setCurrent(s.poolSize)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="pgcat-pool-control">
      <h4>Npgsql client pool size (all DB-touching services)</h4>
      <label className="pgcat-pool-size">
        Maximum Pool Size
        <input type="number" value={poolSize} onChange={(e) => setPoolSize(Number(e.target.value))} min={1} max={500} disabled={busy} />
      </label>
      <button disabled={busy || poolSize === current} onClick={apply}>
        {busy ? 'Applying...' : 'Apply'}
      </button>
      <p className="infra-toggle-description">Recreates auth-api/link-api/redirect-api/shortener-service/traffic-service - takes a few seconds.</p>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
