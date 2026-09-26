import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { InfraToggleControl } from './InfraToggleControl'
import type { InfraStatus } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Self-contained wrapper around the generic InfraToggleControl for the redis-master node - see
// NginxToggleControl for why each of the 3 standing toggles repeats this fetch/busy/error logic
// instead of sharing it.
export function CacheToggleControl(_props: CapabilityControlProps) {
  const [infraStatus, setInfraStatus] = useState<InfraStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi.getInfraStatus().then(setInfraStatus).catch(() => {})
  }, [])

  async function toggle(enabled: boolean) {
    setBusy(true)
    setError(null)
    try {
      setInfraStatus(await controlApi.setCacheEnabled(enabled))
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  if (!infraStatus) {
    return null
  }

  return (
    <>
      <InfraToggleControl
        label="Caching"
        description="Off makes LinkApi/RedirectApi skip Redis entirely and always read Postgres - shows the system without caching. Recreates 2 containers, takes a few seconds."
        enabled={infraStatus.cacheEnabled}
        busy={busy}
        onToggle={toggle}
      />
      {error && <p className="service-card-error">{error}</p>}
    </>
  )
}
