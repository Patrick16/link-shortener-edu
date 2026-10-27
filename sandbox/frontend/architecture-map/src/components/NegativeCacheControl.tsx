import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { InfraToggleControl } from './InfraToggleControl'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Same self-contained, immediate-apply shape as L1CacheControl - a per-node runtime knob, not an
// InfraStatus/Topology toggle (see IInfraToggleService's own comment on GetNegativeCacheEnabled).
// Rendered on both link-api and redirect-api, same reasoning as L1CacheControl.
export function NegativeCacheControl(_props: CapabilityControlProps) {
  const [enabled, setEnabled] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getNegativeCacheEnabled()
      .then((r) => setEnabled(r.enabled))
      .catch(() => {})
  }, [])

  async function apply(next: boolean) {
    setBusy(true)
    setError(null)
    try {
      const r = await controlApi.setNegativeCacheEnabled(next)
      setEnabled(r.enabled)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  if (enabled === null) return null

  return (
    <>
      <InfraToggleControl
        label="Negative cache"
        description="On memoizes a 'this hash doesn't exist' result for 10s (both in-process and Redis) - a caller scanning random/nonexistent hashes stops reaching the DB on every repeat miss. Off reverts to a fresh DB query on every single miss, every time. Safe against the create-right-after-a-404 race: a real entity's positive cache entry is always checked before the negative one, so a hash created moments after being 404'd is found immediately, not blocked for the rest of the negative-cache window. Recreates both link-api and redirect-api."
        enabled={enabled}
        busy={busy}
        onToggle={apply}
      />
      {error && <p className="service-card-error">{error}</p>}
    </>
  )
}
