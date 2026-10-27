import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { InfraToggleControl } from './InfraToggleControl'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Self-contained, immediate-apply - not staged through TopologyPanel like the Redis/caching
// toggles (see InfraStatus's own comment on why L1 caching doesn't belong there: it's a per-node
// runtime knob, not a change to which nodes are in the active-path graph). Same
// fetch-on-mount/apply-on-click shape as RabbitMqPrefetchControl/MongoReadPreferenceControl, but
// reuses InfraToggleControl for the actual on/off UI since this is boolean, same as those toggles.
// Rendered on both link-api and redirect-api (see architecture.json) - both recreate together on
// every change (SetL1CacheEnabledAsync), so toggling from either node's panel affects both.
export function L1CacheControl(_props: CapabilityControlProps) {
  const [enabled, setEnabled] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getL1CacheEnabled()
      .then((r) => setEnabled(r.enabled))
      .catch(() => {})
  }, [])

  async function apply(next: boolean) {
    setBusy(true)
    setError(null)
    try {
      const r = await controlApi.setL1CacheEnabled(next)
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
        label="L1 (in-process) cache"
        description="On checks an in-process IMemoryCache (2s TTL) before ever reaching Redis - a hot key resolves without a network round trip at all. Off falls back to Redis-only. Each replica keeps its own L1 copy, never invalidated across replicas - another replica can serve a stale value for up to 2s after a delete/update elsewhere. Recreates both link-api and redirect-api."
        enabled={enabled}
        busy={busy}
        onToggle={apply}
      />
      {error && <p className="service-card-error">{error}</p>}
    </>
  )
}
