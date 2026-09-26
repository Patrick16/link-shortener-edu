import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { SentinelConfig } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Applies to all 3 redis-sentinel-N containers at once (each tracks its own local config
// independently - see SetSentinelConfigAsync on the backend), shown identically regardless of which
// of the 3 sentinel nodes was clicked.
export function SentinelConfigControl(_props: CapabilityControlProps) {
  const [current, setCurrent] = useState<SentinelConfig | null>(null)
  const [draft, setDraft] = useState<SentinelConfig | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getSentinelConfig()
      .then((c) => {
        setCurrent(c)
        setDraft(c)
      })
      .catch(() => {})
  }, [])

  async function apply() {
    if (!draft) return
    setBusy(true)
    setError(null)
    try {
      setCurrent(await controlApi.setSentinelConfig(draft))
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  if (!draft) {
    return null
  }

  const dirty = current && (draft.downAfterMs !== current.downAfterMs || draft.quorum !== current.quorum || draft.failoverTimeoutMs !== current.failoverTimeoutMs)

  return (
    <div className="pgcat-pool-control sentinel-config-control">
      <h4>Failover timing (all 3 sentinels)</h4>
      <label className="pgcat-pool-size">
        Down-after (ms)
        <input
          type="number"
          value={draft.downAfterMs}
          onChange={(e) => setDraft({ ...draft, downAfterMs: Number(e.target.value) })}
          min={100}
          max={60000}
          disabled={busy}
        />
      </label>
      <label className="pgcat-pool-size">
        Quorum
        <input type="number" value={draft.quorum} onChange={(e) => setDraft({ ...draft, quorum: Number(e.target.value) })} min={1} max={3} disabled={busy} />
      </label>
      <label className="pgcat-pool-size">
        Failover timeout (ms)
        <input
          type="number"
          value={draft.failoverTimeoutMs}
          onChange={(e) => setDraft({ ...draft, failoverTimeoutMs: Number(e.target.value) })}
          min={1000}
          max={300000}
          disabled={busy}
        />
      </label>
      <button disabled={busy || !dirty} onClick={apply}>
        {busy ? 'Applying...' : 'Apply'}
      </button>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
