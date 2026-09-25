import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'

interface Props {
  serviceId: string
}

// Artificial WAL-replay delay on this one standby (recovery_min_apply_delay) - applied live via
// ALTER SYSTEM SET + pg_reload_conf(), no restart. Independent per replica, unlike the pgcat pool
// settings above which apply to all 3 pools at once.
export function ReplicationLagControl({ serviceId }: Props) {
  const [current, setCurrent] = useState<number | null>(null)
  const [delayMs, setDelayMs] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getReplicationLag(serviceId)
      .then((lag) => {
        setCurrent(lag.delayMs)
        setDelayMs(lag.delayMs)
      })
      .catch(() => {})
  }, [serviceId])

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      const lag = await controlApi.setReplicationLag(serviceId, delayMs)
      setCurrent(lag.delayMs)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="scale-control replication-lag-control">
      <label>
        Replication lag (ms)
        <input type="number" value={delayMs} onChange={(e) => setDelayMs(Number(e.target.value))} min={0} max={60000} disabled={busy} />
      </label>
      <button onClick={apply} disabled={busy || delayMs === current}>
        {busy ? 'Applying...' : `Set to ${delayMs}ms`}
      </button>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
