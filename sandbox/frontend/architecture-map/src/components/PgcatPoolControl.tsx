import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { EnumToggleControl } from './EnumToggleControl'
import { InfraToggleControl } from './InfraToggleControl'
import type { PgcatPoolMode, PgcatPoolSettings } from '../types/controlApi'

const POOL_MODE_OPTIONS: { value: PgcatPoolMode; label: string }[] = [
  { value: 'transaction', label: 'Transaction' },
  { value: 'session', label: 'Session' },
]

// Applies to all 3 pools (users_db/links_db/clicks_db) at once - see PgcatPoolSettings. Rewrites
// pgcat.toml directly; pgcat's own autoreload picks the change up within ~15s, no recreate.
export function PgcatPoolControl() {
  const [settings, setSettings] = useState<PgcatPoolSettings | null>(null)
  const [draft, setDraft] = useState<PgcatPoolSettings | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getPgcatPoolSettings()
      .then((s) => {
        setSettings(s)
        setDraft(s)
      })
      .catch(() => {})
  }, [])

  async function apply(next: PgcatPoolSettings) {
    setDraft(next)
    setBusy(true)
    setError(null)
    try {
      setSettings(await controlApi.setPgcatPoolSettings(next))
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  if (!draft) {
    return null
  }

  const dirty = settings && (draft.poolMode !== settings.poolMode || draft.readWriteSplitting !== settings.readWriteSplitting || draft.poolSize !== settings.poolSize)

  return (
    <div className="pgcat-pool-control">
      <h4>Pool settings (all 3 pools)</h4>
      <EnumToggleControl
        label="Pool mode"
        options={POOL_MODE_OPTIONS}
        value={draft.poolMode}
        busy={busy}
        onChange={(poolMode) => setDraft({ ...draft, poolMode })}
      />
      <InfraToggleControl
        label="Read/write splitting"
        description="Off routes every query (including plain SELECTs) to the primary only, same as query_parser_read_write_splitting=false - no load balancing across the 2 replicas."
        enabled={draft.readWriteSplitting}
        busy={busy}
        onToggle={(readWriteSplitting) => setDraft({ ...draft, readWriteSplitting })}
      />
      <label className="pgcat-pool-size">
        Pool size (per pool)
        <input
          type="number"
          value={draft.poolSize}
          onChange={(e) => setDraft({ ...draft, poolSize: Number(e.target.value) })}
          min={1}
          max={200}
          disabled={busy}
        />
      </label>
      <button disabled={busy || !dirty} onClick={() => apply(draft)}>
        {busy ? 'Applying...' : 'Apply'}
      </button>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
