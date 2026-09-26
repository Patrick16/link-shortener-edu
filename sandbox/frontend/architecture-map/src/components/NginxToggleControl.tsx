import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { InfraToggleControl } from './InfraToggleControl'
import type { InfraStatus } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Self-contained wrapper around the generic InfraToggleControl for the nginx node - fetches its
// own InfraStatus and owns its own busy/error state, rather than sharing either with the other two
// standing infra toggles (pgcat/redis-master never render on the same node panel as this one).
export function NginxToggleControl(_props: CapabilityControlProps) {
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
      setInfraStatus(await controlApi.setNginxEnabled(enabled))
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
        label="Load balancing"
        description="Off routes load-test traffic straight to a single link-api/redirect-api container, bypassing nginx - shows the system without balancing across replicas. nginx itself keeps running, so the app UI is unaffected."
        enabled={!infraStatus.nginxBypassed}
        busy={busy}
        onToggle={toggle}
      />
      {error && <p className="service-card-error">{error}</p>}
    </>
  )
}
