import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { InfraToggleControl } from './InfraToggleControl'
import type { InfraStatus } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Self-contained wrapper around the generic InfraToggleControl for the pgcat node - see
// NginxToggleControl for why each of the 3 standing toggles repeats this fetch/busy/error logic
// instead of sharing it.
export function PgcatToggleControl(_props: CapabilityControlProps) {
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
      setInfraStatus(await controlApi.setPgcatEnabled(enabled))
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
        label="Connection pooling"
        description="Off reconnects every DB-touching service straight to Postgres, bypassing pgcat - shows the system without connection pooling. Recreates 5 containers, takes a few seconds."
        enabled={infraStatus.pgcatEnabled}
        busy={busy}
        onToggle={toggle}
      />
      {error && <p className="service-card-error">{error}</p>}
    </>
  )
}
